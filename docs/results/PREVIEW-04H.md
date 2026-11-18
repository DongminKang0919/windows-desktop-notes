# WM-004H — 복구 미리보기 작업 수 제한

2026-10-22, 0.9.9 prototype, Windows x64 10.0.26300 / SDK 10.0.401. PR #9 병합본 `72ff92f`에서 분리했다. WPF·WinForms 트레이·스키마 4·DPAPI·복구 ID v1·원본 보존 및 재시도 정책은 유지한다.

## 문제와 변경

기존 `SelectionChanged`는 선택마다 `PreviewPendingAsync`를 호출해 별도의 `Task.Run` 읽기를 시작했다. 선택 버전 검사는 오래된 결과의 표시만 막았으며, 이미 시작된 여러 파일 읽기·복호화의 동시 실행은 제한하지 않았다. 코드로 확인한 경로이며, 수정 전 실행 재현은 초기 DLL의 Windows 앱 제어 정책 차단 때문에 완료하지 못했다.

창마다 읽기 하나만 실행한다. 그동안 선택이 바뀌면 대기 파일 정보를 마지막 하나로 교체한다. 진행 중 읽기가 끝나면 버전이 같은 결과만 RTF로 파싱해 표시하고, 오래된 결과·오류는 버린 뒤 마지막 선택을 읽는다. 선택 중 복구 버튼은 비활성 상태이며, 현재 읽기 실패는 기존 안내와 같은 행 재선택을 유지한다.

다음 페이지·처음부터 확인·창 닫기는 대기를 비우고 진행 중 결과도 무효화한다. 시작된 동기 읽기를 강제 중단하지 않으며 그 작업이 끝날 때까지는 창 참조가 잠시 남을 수 있다. 완료 후 닫힌 창을 회수할 수 있어야 한다. 스레드풀 작업을 선택마다 쌓거나 실패 시 자동 무한 재시도하지 않는다. 읽기 한 개는 파일 크기·DPAPI·스키마·RTF 제한을 그대로 적용한다.

변경은 복구 창의 선택 처리에 한정한다. 생성자의 선택적 내부 읽기 함수는 완료 순서를 통제하는 테스트용이며 실제 앱은 기존 `NoteWorkspace.PreviewPendingAsync`를 사용한다. 파일 경로나 임의 코드를 사용자 설정으로 받는 기능은 아니다. 사용자 메모·앱 프로세스·보안 설정은 변경하지 않았다.

## 검증 결과

- 최종 로컬 Release 경고·오류 0, **122개 PASS**(자동 121개 + 실제 비표시 셸 호스트 1개).
- 빠른 200회 선택: 첫 읽기를 멈춘 동안 시작 수 1개, 완료 후 마지막 선택만 시작해 총 2개, 최대 동시 읽기 1개. 이전 결과 대기 중에는 본문이 비어 있고 복구 버튼이 비활성이다.
- 이전 읽기 실패는 최신 선택을 비우거나 오류로 바꾸지 않는다. 현재 읽기 실패는 안내·선택 해제 후 같은 행 재선택으로 다시 읽는다.
- 65개 후보의 페이지 이동·재조회 중 이전 결과와 대기 선택을 버리고, 새 페이지에서 다시 정상 미리보기를 표시한다.
- 읽는 중 창을 닫으면 대기 읽기를 시작하지 않고 결과를 표시하지 않는다. 완료 후 닫힌 창의 약한 참조는 회수된다. 각 시험에서 원본 파일 목록·해시·수정 시각이 불변이다.
- 위 4개 순서 검사는 실제 복구 컨트롤·선택/버튼 이벤트를 사용하고 읽기 함수의 완료만 게이트로 통제한다. 디스크 지연이나 수백 건의 실제 동시 I/O를 일으켜 시험한 것은 아니다.
- 실제 파일 읽기·복호화·저장 실패·재시도와 비표시 네이티브 창 생성/해제는 별도 `--recovery-probe --cycles 100 --idle-seconds 1`로 PASS. 10회 워밍업 후 100회 반복의 매 구간 및 마지막 닫기 후 잔존 창·문서 참조 0개, 원본 보존, 최종 사본 1개 저장을 확인했다. 반복 기준/100회 핸들 496/496, GDI 10/10, USER 14/14, 마지막 닫기 후 핸들 498·GDI 10·USER 14였다.
- 격리 자식 프로세스 중단 복구 **10건 PASS**, 새 프로세스 중복 방지·원본 보존 유지.

초기 검증 DLL은 Windows 앱 제어 정책 `0x800711C7`로 실행 차단됐다. 수정 후 최종 빌드의 정상 재실행은 설정 변경·서명·화이트리스트 없이 성공했다. 차단된 초기 실행을 수정 전 재현 PASS로 취급하지 않는다.

100회 시험은 중단 복구 시험과 동시에 실행했고 대기는 1초뿐이므로 CPU 성능이나 5분 유휴 결과로 사용하지 않는다. 이전 0.9.8의 5분 측정은 [WM-012B](RECOVERY-LIFETIME-12B.md)의 별도 근거다. 제품에 강제 GC·작업 집합 축소를 추가하지 않았다.

## 재현과 증거

```powershell
dotnet build WindowsMemo.slnx --configuration Release -p:RestoreLockedMode=true --artifacts-path artifacts/preview-scheduling-validation

dotnet artifacts/preview-scheduling-validation/bin/WindowsMemo.Tests/release/WindowsMemo.Tests.dll --shell-probe

dotnet artifacts/preview-scheduling-validation/bin/WindowsMemo.Tests/release/WindowsMemo.Tests.dll --recovery-probe --cycles 100 --idle-seconds 1

dotnet artifacts/preview-scheduling-validation/bin/WindowsMemo.Tests/release/WindowsMemo.Tests.dll --crash-probe
```

로컬 측정 원본(artifacts는 Git 제외):

- `artifacts/preview-scheduling-validation/bin/WindowsMemo.Tests/release/recovery-probe/951048b205074446a2c737ef33b45481/metrics.jsonl`
- `artifacts/preview-scheduling-validation/bin/WindowsMemo.Tests/release/crash-probe/6855a455392e4f5faa662c7c70c037e6/results.jsonl`

Windows CI는 새 순서 검사 4개를 포함한 자동 검사와 기존 중단 복구·수명 smoke·UI 렌더를 수행한다. 화면 배치 변경이 없어 기존 README 이미지를 유지한다.

## 남은 확인

실제 보이는 창의 빠른 마우스·키보드 선택, 포커스·Loaded·한글 입력·혼합 DPI·Win+D, 느린/고장 난 매체와 최대 크기/복잡한 RTF, 8/24시간 전체 앱 상주는 별도다. 창별 제한이며 파일 I/O의 강제 취소·완료 시간 상한이나 앱 전체의 무누수·무취약점을 보장하지 않는다.
