# WM-003C — 임시 창의 이동·크기와 저장 수명

- 날짜: 2026-10-22, 0.9.2 prototype, Windows x64 26300 / SDK 10.0.401.
- 범위: 아직 열린 [PR #3](https://github.com/DongminKang0919/windows-desktop-notes/pull/3)의 바탕화면 복구 개선을 이어간다. main에 병합하거나 강제 푸시하지 않는다.

## 문제와 변경

기존 메모 손잡이는 바탕화면 자식 HWND가 없으면 즉시 반환해 임시 창에서 이동·크기 조절이 동작하지 않았다. 별도 OS 테두리로 바꾼 배치도 저장 상태에는 반영되지 않았다.

임시 창에도 기존 메모의 상단 손잡이와 오른쪽 아래 크기 손잡이를 연결했다. 별도 OS 프레임을 제거해 메모 내용 크기와 창 크기를 맞춘다. 이동·크기를 실제 HWND의 물리 화면 좌표와 배율로 관리하고 현재 모니터 작업 영역에 맞춘다. 위치/크기 이벤트를 실제 창 사각형으로 읽어 저장 변경을 발행한다. 초기 표시 시에는 WPF 기본 배치 이벤트를 무시하고 저장된 배치를 적용한다.

접힌 실제 창 높이를 저장된 펼친 높이와 구분한다. 반복 접기·펼치기나 같은 색상/배치 적용은 동일 좌표 저장 이벤트를 만들지 않는다. 최소화 등 비정상 창 상태의 좌표는 읽지 않고, 정상 상태 복귀 시 대기한 모양 변경과 작업 영역을 재확인한다. 최소화 복귀의 실환경 동작은 아래 수동 항목이다.

LocationChanged·SizeChanged·StateChanged 구독은 임시 창을 닫을 때 해제한다. 기존 닫기 요청 → 저장 성공 후 숨김, 바탕화면 재연결 시 편집기 이동·임시 창 폐기, TopMost 미사용과 DPAPI/스키마 4는 유지한다.

## 검증 결과

- Release 빌드 경고 0 / 오류 0. 로컬 전체 77개 PASS(자동 76개 + 실제 셸 비표시 probe 1개).
- 실제 Thumb DragDelta 경로로 비표시 임시 창 이동·크기 조절을 실행했다. 별도 HWND가 생성되고 크기 변화가 반영됨을 확인했다.
- 접기/펼치기 10회에서 펼친 크기가 유지되고 BoundsChanged 중복 발행이 0회임을 확인했다. 접힌 네이티브 창 높이도 확인했다.
- 시험 창에만 SetWindowPos를 적용해 물리 좌표와 크기를 바꾸고 실제 사각형이 저장 좌표가 되는지 검증했다. 바탕화면 재부착 후 같은 배치를 사용하며 이전 임시 HWND가 파괴됨을 확인했다.
- 별도 GUID DPAPI 저장소에서 변경 배치를 저장, 숨김 후 복원, 작업공간 폐기/재실행 및 늦은 바탕화면 부착 뒤 같은 ID·본문·배치를 확인했다. 저장 파일의 Bounds와 호스트 CaptureBounds를 비교했다.
- 복구 호스트/편집기 약한 참조 검사에 실제 임시 HWND·복구 타이머·관찰 창이 있는 경로를 추가했다. 폐기 후 강제 GC에서 참조 회수 PASS. 강제 GC는 시험에만 있다.

```powershell
dotnet build WindowsMemo.slnx --configuration Release --nologo -p:RestoreLockedMode=true --artifacts-path artifacts/fallback-validation
dotnet artifacts/fallback-validation/bin/WindowsMemo.Tests/release/WindowsMemo.Tests.dll --shell-probe
```

사용자 메모·마우스 위치·모니터 설정·실행 정책을 바꾸지 않았다. 시험 창은 표시하지 않고, 네이티브 좌표 변경은 시험 프로세스의 HWND에만 적용했다. 기존 사용자 앱은 종료하지 않았다.

## 남은 수동 확인

보이는 임시 창의 마우스 이동/크기 조절·모서리/텍스트 렌더링, 최소화/복원과 그동안의 접힘/저장 오류 안내, 혼합 DPI 모니터 사이 이동·모니터 제거, 실제 Explorer 재시작/절전/로그인, Win+D·IME 및 8/24시간 상주는 미검증이다. 비표시 시험은 실제 마우스 조작과 혼합 DPI 검증을 대신하지 않는다.

Microsoft의 [EnsureHandle](https://learn.microsoft.com/en-us/dotnet/api/system.windows.interop.windowinterophelper.ensurehandle) 및 [GetWindowRect의 DPI·테두리 처리](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowrect)를 참고했다. 제품의 PerMonitorV2 manifest는 유지하며 실제 배율 전환은 수동 확인 항목으로 남긴다.
