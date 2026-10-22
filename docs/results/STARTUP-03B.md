# WM-003B — 초기 바탕화면 부재 시 편집·저장·복원

- 날짜: 2026-10-22, 0.9.2 prototype, Windows x64 26300 / SDK 10.0.401.
- 검토: 기존 [PR #3](https://github.com/DongminKang0919/windows-desktop-notes/pull/3)의 바탕화면 복구 개선에 통합한다. 기본 main은 6e6d01a이며 이전 0.9.2 복구 수정 위에 이어 작업했다.

## 문제와 수정

DesktopNoteHost.Open은 시작할 때 바탕화면 HWND를 찾지 못하면 즉시 예외를 던졌다. 새 메모 생성은 앱 시작 실패로, 기존 메모 열기는 복원 실패 목록으로 이어졌다. 실행 중 연결 손실에 쓰던 임시 창·복구 관찰 기능이 첫 실행에는 생성되지 않는 문제였다.

처음부터 바탕화면이 없거나 반환된 HWND가 유효하지 않으면 동일 편집기를 임시 일반 창에 표시하고 준비 대기 안내·재연결 기능을 활성화한다. 기존 2초 간격/최대 10회 재시도, 소진 후 셸/절전 복귀 알림 및 수동 재연결을 사용한다. 바탕화면이 준비된 정상 시작은 기존처럼 바로 자식 HWND를 만든다.

부모 HWND가 없는 동안에도 CaptureBounds가 유효하도록 저장된 배율(없으면 1)과 DIP 크기로 화면 좌표를 초기화한다. 따라서 기존 NoteSession·자동 저장 경로를 그대로 사용해 임시 창의 본문·서식을 암호화 저장할 수 있다. 실제 바탕화면에 붙일 때 현재 배율과 가용 영역에 맞춘다. 후속 [WM-003C](FALLBACK-03C.md)에서 임시 창 이동·크기 변경도 저장 좌표에 반영하도록 확장했다.

`--startup`의 최대 10초 준비 유예는 유지한다. 유예 이후에도 셸이 없으면 임시 창에서 작업할 수 있다. 연결 중에는 TopMost를 설정하지 않으며, 임시 일반 창은 Win+D에서 바탕화면에 남는다고 보장하지 않는다. 연결이 완료되면 기존 바탕화면 자식 창 동작을 사용한다.

## 확인 결과

- Release 빌드 경고 0 / 오류 0. 전체 75개 PASS(자동 74개 + 로컬 실제 셸 비표시 probe 1개).
- 새 호스트의 locator가 0을 반환할 때 예외 없이 임시 편집, 저장 배율 1.5/320×300 DIP의 초기 좌표 확인, 10회 제한, 이후 수동 재부착과 내용·DIP 크기 보존.
- 별도 GUID DPAPI 저장소에서 실제 NoteWorkspace/NoteSession/자동 저장/호스트로 첫 메모 생성·본문/서식/종이색 저장, 작업공간 폐기 후 동일 ID 복원, 셸 준비 전 최신 편집, 늦은 부모 연결 뒤 저장 본문·좌표 일치 확인. 종료 후 모든 호스트/임시 창 해제.
- 이전 복구 알림 시험도 포함해 통과했다. 이전 실행에서 있었던 Windows 앱 제어 차단은 이번 실행에서는 발생하지 않았다. 실행 정책·보안 설정을 변경하지 않았다.

```powershell
dotnet build WindowsMemo.slnx --configuration Release --nologo -p:RestoreLockedMode=true --artifacts-path artifacts/startup-validation
dotnet artifacts/startup-validation/bin/WindowsMemo.Tests/release/WindowsMemo.Tests.dll --shell-probe
```

모든 시험 데이터는 빌드 출력 아래 생성하며 사용자 메모 저장소를 사용하지 않는다. 시험 부모 창은 비표시 HWND다. 실제 셸 probe는 사용자 메모 없이 실제 부모 자식 연결 생성/해제를 확인한다. 사용자 앱 종료나 탐색기 종료·PC 절전은 수행하지 않았다.

## 남은 수동 확인

실제 로그인에서 셸 준비 지연, 보이는 임시 창의 편집/닫기/저장 실패 안내, Explorer 재시작과 절전 복귀, 연결 후 Win+D·한국어 IME·혼합 DPI·다중 모니터 동작, 8/24시간 상주는 별도 검증이다. 본 결과로 해당 실환경 검증이나 무누수를 보증하지 않는다.
