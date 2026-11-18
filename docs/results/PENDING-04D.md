# WM-004D — 미완료 메모 미리보기와 사본 복구

2026-10-22, 0.9.5 prototype. PR #5 병합본 c47bd17에서 분리했다. 기존 WPF·WinForms 트레이·스키마 4·DPAPI와 사용자 저장 경로를 유지한다.

## 동작과 보호 범위

트레이 **미완료 메모 복구**에서 최대 64개 GUID.tmp 후보를 표시한다. 목록은 메타데이터만 읽으며 없던 폴더를 만들지 않는다. 선택한 파일 하나만 최대 4MiB까지 읽고 기존 DPAPI·스키마·RTF 검증을 통과하면 읽기 전용으로 표시한다. 손상·지원하지 않는 형식·잠김은 안내하고 복구 버튼을 비활성화한다.

복구 시 다시 파일을 열어 암호화 파일 SHA256 지문을 비교한다. 미리보기 객체의 본문을 저장 입력으로 신뢰하지 않는다. 재검증한 스냅샷을 새 ID·새 생성/수정 시각의 숨김 메모로 저장한다. 본문·서식·체크리스트·색상·배치를 유지하고, 보관·휴지통·접힘은 해제한다. 기존 메모·백업·임시 원본은 덮어쓰거나 삭제하지 않는다. 디스크 저장 성공 후에만 목록에 추가한다.

같은 실행 중 동일 토큰·지문은 기존 사본 ID를 반환한다. 중복 방지 기록은 메모 영구 삭제와 작업공간 폐기 시 해제한다. 재실행 간 영수증은 없으므로 다시 복구하면 별도 사본이 생길 수 있다. 원본 자동 승격·고유 데이터 자동 삭제·보존 기간 정책·64개 이후 페이지 탐색은 제공하지 않는다.

읽기·복호화는 작업 스레드, WPF 문서 검증은 UI 스레드에서 수행한다. 읽는 동안 쓰기/삭제를 막는 핸들을 using으로 해제한다. 본문을 로그나 파일명에 쓰지 않는다. 창 닫기 시 이벤트 구독·문서·미리보기 참조를 해제하며, 늦게 끝난 선택 읽기는 세대 번호와 닫힘 상태로 무효화한다.

## 검증과 한계

- 로컬 SDK 10.0.401 Release 빌드: 경고·오류 0.
- 로컬 실행은 Windows 애플리케이션 제어 정책 `0x800711C7`로 DLL 로드가 차단되어 통과로 기록하지 않는다. 같은 파일 재실행에서도 지속됐다. 보안 정책·서명·허용 목록은 변경하지 않았다.
- [최초 Windows CI](https://github.com/DongminKang0919/windows-desktop-notes/actions/runs/39729819410): 자동 95개·강제 종료 복구 8건·실제 잔여 중복 정리 후 새 프로세스 검증 통과. 이후 화면·수명 보완의 최종 CI 결과는 아래에 기록한다.
- 검사 범위: 후보 조회·선택 읽기·핸들 해제, 변경/삭제/잠김/과대/손상 거부, 후보 한도, 새 ID 사본·서식·색상·재실행, 미리보기 데이터 변조 무시, 중복 요청·준비/폐기 상태 거부, UI 선택·읽기 전용·버튼 복구·실패 시 비활성.
- 렌더를 직접 확인한 뒤 투명 배경을 흰색으로 합성하고 시험 본문을 실제 문단으로 구성했다. 창 20회 열기/닫기 후 약한 참조 회수 검사를 추가했다. 이는 장시간 메모리·핸들 누수 부재를 보장하지 않는다.
- 실제 보이는 창의 키보드/마우스·한글 입력·혼합 DPI·Win+D·Explorer/절전 복귀·링크/정션 공격 조합·전원 차단·설치본·8/24시간 상주 확인은 별도다. 저장 장치 지연의 시간 상한도 보장하지 않는다.

## 재현

최종 구현 ec9b60e의 [Windows CI](https://github.com/DongminKang0919/windows-desktop-notes/actions/runs/39730371915)에서 **자동 검사 96개, 강제 종료 복구 8건, 실제 중복 정리 후 새 프로세스 검증, UI 렌더·업로드**가 모두 통과했다. 복구 창 20회 열기/닫기 후 약한 참조 잔존은 0개다. 실제 바탕화면 shell-probe는 이번 로컬 실행 차단으로 수행하지 못했다.

README의 PNG는 이 CI의 sample-ui-renders artifact 12404632002에서 가져왔다. ZIP SHA256은 `9cd837c705029549d104a3ceb19b30030cccaa85493e3e906b43c56c5a9b66e0`로 내려받은 파일과 일치한다. 두 PNG를 직접 열어 한글·문단·체크 항목·버튼·배경을 확인했다. 실제 실행 화면 캡처로 표기하지 않는다.

```powershell
dotnet build WindowsMemo.slnx --configuration Release -p:RestoreLockedMode=true --artifacts-path artifacts/pending-recovery-validation
dotnet artifacts/pending-recovery-validation/bin/WindowsMemo.Tests/release/WindowsMemo.Tests.dll
dotnet artifacts/pending-recovery-validation/bin/WindowsMemo.Tests/release/WindowsMemo.Tests.dll --crash-probe
dotnet artifacts/pending-recovery-validation/bin/WindowsMemo.Tests/release/WindowsMemo.Tests.dll --render-ui-only
```

시험 데이터는 출력 경로의 GUID별 디렉터리를 사용한다. 사용자 메모·기존 앱·Explorer는 변경하지 않는다. [이미지 출처·재생성](../images/README.md).
