# WM-004I — Windows 세션 종료와 저장 완료 순서

2026-10-22, 0.9.11 prototype. 작업 전 GitHub 기본 브랜치의 버전, PR #12 병합 정보, `git fetch origin main`과 원격 CI 작업 상태를 다시 확인했다. 당시 최신 main은 **f7cfe8ad586e47128dbfca3ab5be64e8d73fe689**, 버전 0.9.10이었다. 사용자 변경이 없는 작업 트리에서 독립 브랜치를 만들었다. 기존 PR #12의 [CI](https://github.com/DongminKang0919/windows-desktop-notes/actions/runs/39771365744)는 자동 126개·중단 복구 10건·수명 smoke·UI artifact 성공 상태였다.

## 문제와 공식 규약 대조

기존 `Application.SessionEnding`은 항상 `e.Cancel = true` 뒤 `tray.ExitAsync()`를 시작했다. 저장 완료 상태도 Windows 종료에 취소 응답을 보냈으며, 취소된 OS 요청과 앱의 일반 종료를 섞었다. 일반 트레이 종료는 `FlushAllAsync`가 편집·작업 차단을 해제한 뒤 `Application.Shutdown`을 호출했다.

Microsoft의 [WPF SessionEnding](https://learn.microsoft.com/en-us/dotnet/api/system.windows.application.sessionending?view=windowsdesktop-10.0)은 Cancel 기본값이 false이며 true로 세션 종료를 취소할 수 있다고 설명한다. .NET 10의 [공식 WPF Application 구현](https://github.com/dotnet/wpf/blob/release/10.0/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Application.cs)의 `WmQueryEndSession`도 확인했다. 이 구현은 취소하지 않으면 `Shutdown()`을 호출한다. 따라서 true 응답 이후의 비동기 저장을 기대해서는 안 된다. 다른 프로그램이 나중에 Windows 종료를 취소하더라도 WPF 앱은 이미 종료될 수 있으며, 여기서는 그 시점에 메모가 저장돼 있어야 한다.

[WM_QUERYENDSESSION](https://learn.microsoft.com/en-us/windows/win32/shutdown/wm-queryendsession)은 신속한 응답과 작업 정리를 다음 단계로 미룰 것을 권고한다. [WM_ENDSESSION](https://learn.microsoft.com/en-us/windows/win32/shutdown/wm-endsession)은 실제 종료 여부와 강제 종료를 구분하며, 종료 확정 후에는 프로세스가 끝날 수 있다. 일반 Win32의 정리 권고를 WPF의 비취소 이후 저장 시간 보장으로 해석하지 않았다.

[ShutdownBlockReasonCreate](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-shutdownblockreasoncreate)는 호출 스레드 소유의 창에 짧은 보류 사유를 등록하고 완료 시 해제하도록 규정한다. [Windows 종료 변경·권고](https://learn.microsoft.com/en-us/windows/win32/shutdown/shutdown-changes-for-windows-vista)는 비표시 앱·강제 종료의 한계와 종료 차단을 보장할 수 없음을 설명한다. 보류 시 앱 소유의 비표시 HWND에 사유를 등록하며 Explorer HWND를 사용하지 않는다. API 실패와 사용자의 강제 종료를 데이터 보존 보장으로 취급하지 않는다.

## 상태와 동작

| 관측 상태 | 이번 요청 | 이후 동작 |
| --- | --- | --- |
| Saved | 취소하지 않음 | 이미 최신 버전이 저장됐으므로 WPF의 정상 종료 허용 |
| Dirty | 취소 응답 | 응답 뒤 Dispatcher에 저장 준비를 큐잉 |
| Saving | 취소 응답 | 진행 작업을 공유해 기다린 뒤 필요한 최신 버전까지 저장 |
| Failed | 취소 응답 | 보존 중인 최신 편집기로 저장 재시도 |
| Preparing | 취소 응답 | 초기화와 충돌하는 저장은 시작하지 않고 다음 요청에서 재판단 |
| Busy | 취소 응답 | 다른 작업과 경합하는 저장은 시작하지 않고 다음 요청에서 재판단 |
| Disposed | 허용으로 판정하지 않음 | 새 준비·알림 없음. 실제 앱 이벤트는 Run 종료 시 구독 해제 |

`SessionEndingCoordinator.Decide()`는 상태만 읽고, Program이 Cancel을 설정한 뒤 `PrepareAfterCancellation()`을 호출한다. 실제 큐는 `Dispatcher.BeginInvoke(Background)`이므로 종료 질의 안에서 RTF 직렬화·파일 I/O·모달 UI·Task.Wait/Result·중첩 DispatcherFrame을 실행하지 않는다. 네이티브 사유 등록은 같은 UI 스레드에서 자체 창에 수행한다.

준비는 큐 대기와 실행을 합쳐 하나만 유지한다. 반복 종료 요청은 새 저장 작업을 쌓지 않는다. 큐를 실행할 때 상태를 다시 읽고, 완료 때도 최신 저장 상태인지 확인한다. 저장 성공은 알림만 제공하며 앱 종료를 호출하지 않는다. **사용자는 Windows 종료를 다시 시도한다.** 그 전에 새 편집이 생기면 다시 미저장 상태로 판단한다. 준비 실패는 최신 내용을 유지하고 다음 요청에서 재시도한다. Windows 종료 화면에서 돌아오지 않으면 알림이 즉시 보인다고 보장하지 않는다.

저장이 지연돼도 질의 스레드에서 기다리지 않는다. 앱이 임의의 제한 시간이 지났다고 쓰기를 강제 중단하거나 성공으로 간주하지 않는다. OS·드라이버의 동기 I/O 완료 시간 상한은 보장하지 않는다. 강제 종료·정전에서는 마지막 저장 이후의 편집을 잃을 수 있다.

## 저장 순서와 수명

`AutosaveCoordinator`는 이제 모든 대기자가 같은 완료 작업을 공유한다. 진행 중 필드를 지운 뒤 완료를 통지하므로, 앞선 저장이 끝났지만 새 버전이 남은 상황에서 완료된 이전 작업을 계속 반환하는 순서 위험을 제거했다. 저장 중 새 편집도 마지막 버전까지 직렬 저장한다.

트레이 종료는 `SaveThenAsync(Application.Shutdown)`을 사용한다. 모든 메모 저장 성공 뒤에도 작업·편집이 차단된 콜백 구간에서 종료를 요청한다. 저장 실패는 종료 콜백을 실행하지 않는다. 폐기된 작업공간에서는 후속 종료 콜백·편집기 재활성화를 하지 않으며, 초기화가 폐기된 뒤 새 창을 만들지 않는다.

Run 종료 시 세션 이벤트를 해제하고 준비 객체를 먼저 폐기해 늦은 알림을 억제한다. 진행 중 쓰기는 안전하지 않은 강제 취소를 하지 않고 완료될 때까지 결과를 관찰한다. 네이티브 보류 사유는 성공/허용/폐기에 해제하고 자체 HWND를 폐기한다. 실패·작업 중 사유는 다음 허용 또는 폐기까지 유지한다. 타이머·표면·편집기 해제는 기존 소유권을 유지한다. 제품에 강제 GC·작업 집합 축소·자동 재시작을 추가하지 않았다.

## 로컬 검증

코드 커밋 `0a158d2`의 [Windows CI](https://github.com/DongminKang0919/windows-desktop-notes/actions/runs/39776147837) 작업 로그도 확인했다. Release 경고·오류 0, 자동 **137개**(기존 126 + 종료 11), 중단 복구 **10건**, 수명 smoke(잔존 창·문서 참조 0·원본 보존·최종 재시도 성공), 측정 로그 및 UI 렌더 artifact 업로드 모두 통과했다. 실제 로그아웃·재부팅 검증으로 해석하지 않는다. 문서 결과 추가 커밋의 최종 CI 상태는 [PR #13](https://github.com/DongminKang0919/windows-desktop-notes/pull/13)에 연결한다.

별도 `artifacts/session-ending-validation`에 Release 빌드: 경고·오류 0. 최종 하네스 **138개 PASS**(자동 137개 + 실제 비표시 바탕화면 호스트 1개). 이번 실행은 앱 제어 정책 차단 없이 성공했으며 보안 설정·서명·화이트리스트·우회 실행을 변경하지 않았다. 이전 버전의 차단 기록은 해당 결과 문서에 그대로 유지한다.

새 종료 검사 11개는 다음을 확인한다.

- 저장 완료·미저장·진행·실패·준비·작업·폐기 상태의 구분과 질의에서 저장 미실행.
- 취소 응답 후 큐잉, 중복 요청 100회 병합, 완료 후 앱 자동 종료 없음, 새 편집 후 재판단.
- 실제 파일 잠금 실패·최신 편집기 유지·잠금 해제 후 재요청 성공.
- 지연 작업 중 1,000회 판단이 저장 완료를 기다리지 않음. 짧은 제한 검사는 응답 성능 보장이 아니다.
- 큐·저장·알림 예외 관찰, 취소 이후 재시도, 큐 대기/실행 중 폐기 후 추가 저장·알림 없음.
- 실제 저장 경계 게이트에서 대기자 3개와 새 편집: 두 직렬 저장으로 최신 본문 확정, 예약 타이머 정지.
- 여러 메모의 저장 파일을 읽어 확인한 뒤, 편집·작업 차단 구간에서만 일반 종료 콜백 실행.
- 준비 중 폐기 뒤 표면 미생성, 준비 객체의 완료·폐기 뒤 참조 회수.
- 자체 비표시 HWND의 사유 등록·조회·해제·20회 폐기. Windows 종료 메시지를 방송하지 않음.

기존 격리 자식 중단 복구 **10건 PASS**(기존 8 + 고정 복구 ID의 저장 직전/직후 2). 원본·백업·중복 방지 보호를 유지했다. 로그: `artifacts/session-ending-validation/bin/WindowsMemo.Tests/release/crash-probe/8fb44d6d212344ca9df5dddcd8f5bc65/results.jsonl`.

합성 메모 10개의 편집·저장·숨김·재표시·보관·복원 10회와 폐기·재생성 복원 PASS. 폐기 편집기 잔존 참조 0개, 최종 활성 표면 0개, 본체 10개·백업 10개·임시 0개. 반복 후 GDI 54, USER 51, 최종 폐기/복원 확인 후 GDI 54, USER 30, 핸들 545개였다. 로그: `artifacts/session-ending-validation/bin/WindowsMemo.Tests/release/workspace-probe/572892a2f9954cb084c21ac0135ab08d/metrics.jsonl`. 대기 0초이며 중단 복구와 동시에 실행했으므로 유휴 CPU·장시간 누수 판단에 사용하지 않는다.

```powershell
dotnet build WindowsMemo.slnx --configuration Release -p:RestoreLockedMode=true --artifacts-path artifacts/session-ending-validation
dotnet artifacts/session-ending-validation/bin/WindowsMemo.Tests/release/WindowsMemo.Tests.dll --shell-probe
dotnet artifacts/session-ending-validation/bin/WindowsMemo.Tests/release/WindowsMemo.Tests.dll --crash-probe
dotnet artifacts/session-ending-validation/bin/WindowsMemo.Tests/release/WindowsMemo.Tests.dll --workspace-probe --cycles 10 --idle-seconds 0
```

로그인된 Windows 세션이 없는 CI에서는 `--shell-probe`를 제외한 실행형 하네스를 사용한다. `dotnet test`로 실행하는 테스트가 아니다. 모든 파일은 출력 폴더의 GUID별 합성 데이터이며 실제 사용자 메모와 실행 중 앱은 사용하지 않았다.

## 수동 확인 — 임의 실행하지 않음

사용자가 동의한 시험 시점에 별도로 확인해야 한다.

1. 저장 완료 후 실제 로그아웃·종료에서 불필요한 차단이 없는지 확인.
2. 미저장·저장 중·잠금 실패, 앱 초기화·다른 작업 중 보류 사유와 Windows 종료 취소/복귀 확인.
3. 저장 완료 알림 후 재요청 성공, 재요청 전 새 편집·실패 재시도, 다른 프로그램이 종료를 취소한 경우의 WPF 동작 확인.
4. 트레이 종료와 OS 종료 요청의 경합·중복 요청·강제 종료·느린 매체 확인. 강제 종료의 미저장 보존은 보장하지 않음.
5. 실제 IME·Win+D·Explorer 재시작·혼합 DPI·절전 복귀, 업데이트 게시자 인증·서명·설치본 두 버전 업데이트, 8/24시간 전체 앱 상주.

실제 로그아웃·재부팅·Explorer 재시작이나 사용자 앱 종료를 이번 작업에서 실행하지 않았다. 짧은 시험으로 무누수·무취약점이라고 판정하지 않는다. 저장 스키마 4·DPAPI CurrentUser·고정 복구 ID·원본/백업/임시 원본·숨김/보관/휴지통/영구 삭제 확인·데스크톱 부착 방식과 TopMost 구분을 유지한다.
