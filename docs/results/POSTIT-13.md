# WM-013 — 0.9.0 prototype 포스트잇 UI

- 날짜: 2026-10-22
- 환경: Windows x64 10.0.26300.0, .NET SDK 10.0.401
- 상태: 코드 구현·자동 검증·이미지 렌더링 확인 완료. 실제 입력과 Win+D·혼합 DPI는 수동 확인 대기.

## 기존 구조와 적용 범위

프로젝트는 net10.0-windows WPF다. NoteView.xaml/코드 비하인드의 RichTextBox가 편집하며, WinForms는 TrayController의 NotifyIcon·ContextMenuStrip에서 사용한다. WinForms Form/Designer 파일은 없다. 전달된 안내의 WinForms 전제와 실제 구조가 달라 기존 WPF를 보존했다. Figma 파일이나 첨부 이미지 자체는 제공되지 않아 사용자가 전달한 색상·치수·인터랙션을 기준으로 구현했다.

메모는 ID별 JSON 봉투 안에 DPAPI CurrentUser로 암호화한 RTF·좌표·상태를 저장한다. 기존 경로·암호화 봉투·정상 백업·손상 보존과 입력 제한을 유지했다. 스키마 4에 Paper/Collapsed/Archived를 추가하고 기존 1·2·3의 누락 필드는 버터/펼침/미보관으로 읽는다. 새 버전은 기존 내용을 읽지만 이전 앱은 스키마 4를 거부하므로 다운그레이드는 지원하지 않는다. 실제 사용자 메모를 열거나 변환하지 않았으며 검증에는 가짜 데이터만 사용했다.

## 구현

- NoteTheme.cs에 종이색 #F7E8A3/#DDE5CA/#F1D3C2, 글자색 #34342E, 맑은 고딕, 본문 15 DIP(11.25pt)·제목 17 DIP(12.75pt), 크기·여백·모서리·최소 도구 크기를 모았다.
- 새 메모 기본 320×300 DIP, 본문 여백 22 DIP, 모서리 5 DIP, 얇은 상단 띠와 약한 하단 윤곽을 적용했다. 기존 메모 크기는 보존한다. 이미지·질감·테이프·강한 그림자·새 UI 의존성은 없다.
- 호버 또는 키보드 포커스가 들어오면 +/이동 손잡이/더보기를 표시한다. 버튼은 28×28 DIP 이상이다. 본문은 RichTextBox 편집·선택, 상단 Thumb는 이동을 담당한다. 제목은 본문 첫 줄에서 파생하며 상단 더블클릭으로 접기·복원한다.
- 접으면 실제 호스트 높이를 52 DIP로 줄이고 펼친 크기는 저장값으로 유지한다. 저장·재연결 오류가 나면 펼쳐 안내와 재시도를 노출한다.
- 더보기에 새 메모·체크리스트·완료 전환·종이색·접기·보관·글자 서식·목록·보관함·기존 숨기기/휴지통을 연결했다. Alt+M으로 메뉴를 열고 Ctrl+Enter로 체크한다. 체크 기호 클릭과 Enter 후속 항목도 연결했다.
- 체크리스트는 허용된 RTF Run의 ☐/☑ 텍스트로 표현한다. 임베디드 개체나 외부 XAML을 읽지 않으며 기존 RTF 검사와 텍스트 전용 붙여넣기를 유지한다. 체크 전환·제거 시 본문의 굵게 등 서식을 보존한다.
- 보관은 숨김·휴지통과 구분하는 저장 상태다. 트레이 보관함/실행 취소와 메뉴의 보관함을 연결했다. 가장 최근 보관은 현재 실행 동안 되돌릴 수 있고 재실행 후에는 보관함에서 복원한다. 저장 실패 시 최신 편집기를 유지한다.

## 바탕화면 표시: 확인한 것과 확인하지 않은 것

기존 DesktopNoteHost는 탐색기 프로세스의 보이는 SHELLDLL_DefView를 찾고, 해당 부모 아래 WS_CHILD HwndSource를 생성한다. 일반 최상위 창을 TopMost로 올리는 방식이 아니다. 창을 생성할 때 부모/스타일을 지정하며 SetParent로 기존 WPF Window를 바꾸지 않는다. 이 부착·포커스·재연결 구조를 유지했고 새 UI의 크기·접힘·둥근 창 영역만 연결했다.

실제 바탕화면에 보이지 않는 시험 호스트를 생성해 다음을 확인했다: GetParent 일치, WS_CHILD 설정, WS_EX_TOPMOST 미설정, 접으면 실제 HWND 높이 감소, 펼친 높이 보존·복원, 호스트 폐기. 기존 가짜 부모 교체·복구 횟수·메모리 수집 및 화면 밖 좌표 보정 시험도 통과했다. 이것은 Win+D와 IME 입력을 실제 수행한 결과가 아니다. 탐색기 내부 창 구조는 Windows 전체에 보장된 공개 계약으로 간주하지 않는다.

PerMonitorV2 manifest, WPF DIP 레이아웃·벡터 컨트롤·레이아웃 반올림을 유지한다. 다른 DPI 인식 모드의 부모/자식 관계와 혼합 배율은 Windows의 제약을 받으므로 실제 검증이 필요하다. 참고: [Microsoft DPI 개발·시험 지침](https://learn.microsoft.com/en-us/windows/win32/hidpi/high-dpi-desktop-application-development-on-windows), [SetParent의 스타일·DPI 주의사항](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setparent).

## 검증 결과

```powershell
dotnet build WindowsMemo.slnx --configuration Release --nologo -p:RestoreLockedMode=true --artifacts-path artifacts/design-validation
dotnet artifacts/design-validation/bin/WindowsMemo.Tests/release/WindowsMemo.Tests.dll --shell-probe --render-ui
```

- Release 경고 0 / 오류 0, 잠금 복원 PASS. 최종 수정은 같은 폴더에서 --no-restore 빌드로 재검증했다.
- 전체 **66개 PASS**, 종료 코드 0: 기존 61개 + 저장 형식 호환성/검증 1 + 보관 왕복/실패 2 + 체크리스트/기본 레이아웃 2. 실제 바탕화면 호스트 시험은 부모·스타일·접힘 크기 검사로 확장했다.
- 100/150/200% 세 가지 종이색, 200% 접힘 및 도구 표시 이미지를 생성했다. 100/150% 전체 배치와 200% 접힘/도구 이미지를 시각 확인했다. 실제 배율 전환·마우스·키보드 포커스 동작의 대체 시험이 아니다.
- 이미지: `artifacts/design-validation/bin/WindowsMemo.Tests/release/ui-previews` (papers-100/150/200.png, collapsed-200.png, tools-200.png). 샘플 텍스트만 포함한다.
- 기존 반복 폐기·GC·숨김 20회·복구 검증 통과. 새 이벤트·메뉴·보관함 창은 기존 소유자의 Dispose/Closed에서 해제한다. 창 영역 생성 성공 시 Windows가 소유하며 실패한 영역은 DeleteObject로 해제한다. 8/24시간 무누수 측정은 미수행이다.
- 실행 중인 기존 앱을 종료하지 않고 별도 artifacts 출력으로 빌드했다. 설치 파일 생성·게시·원격 푸시·실제 자동 실행 Run 값 변경은 수행하지 않았다.

## 실제 Windows 수동 확인

1. 기존 앱에서 저장 후 종료한 뒤 새 빌드를 실행한다. Win+D·작업 표시줄 바탕화면 보기 후 메모가 남는지, 다른 앱 사용 중에는 해당 앱 뒤에 놓이는지 확인한다.
2. 한글 IME 조합, 본문 드래그 선택, 서식·체크 기호 클릭·Ctrl+Enter·Enter, 메뉴 열기·닫기와 선택 영역 유지, 일반 텍스트 Undo/Redo를 확인한다.
3. 호버/Tab 포커스로 상단 도구가 나타나는지, +/상단 이동/더보기 클릭 영역과 Alt+M이 동작하는지 확인한다.
4. 종이색·크기·위치·접힘을 바꾸고 저장 후 재실행한다. 접힌 메모의 상단 더블클릭으로 펼친 크기가 복원되는지 확인한다.
5. 보관→트레이 실행 취소, 재실행→보관함 복원, 보관함→휴지통→복원과 삭제 확인 UI를 확인한다. 파일 잠금 등 저장 실패에서는 창·최신 내용이 유지되는지 확인한다.
6. 100/150/200% 및 서로 다른 배율의 모니터 이동·배율 변경·모니터 분리에서 글자·아이콘·크기·포커스·화면 밖 복귀를 확인한다. 실제 탐색기 재시작·절전 복귀는 사용자 작업과 조율해 검증한다.

후속 설치본 업데이트 WM-010B2B와 원격 CI, 의존성 취약점 재조회, 장시간 안정성 검증도 계속 남아 있다.
