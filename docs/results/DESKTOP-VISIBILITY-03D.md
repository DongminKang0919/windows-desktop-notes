# WM-003D: 바탕화면 아이콘 표면 뒤에 가려진 메모

2026-11-18. 0.9.17의 목록 접근 복구에 이어 실제 바탕화면 메모가 보이지 않는 문제를 조사했다. 수정 버전은 0.9.18 prototype이다.

## 재현과 원인

사용자는 목록에서 메모를 열어도 바탕화면에 보이지 않는다고 보고했다. `--desktop-report`로 창 클래스·프로세스·가시성·화면 좌표·스타일만 조회했다. 메모 본문·창 제목·아이콘 이름은 출력하지 않는다.

실제 SHELLDLL_DefView 아래 자식 창이 위에서부터 SysListView32(전체 화면 아이콘 표면), 메모 창 2개 순으로 놓여 있었다. 메모 창은 WS_VISIBLE이고 화면 안에 있었지만 아이콘 표면에 가려졌다. 기존 ApplyBounds의 SWP_NOZORDER는 이 순서를 유지했다.

## 변경

- ApplyBounds에서 SWP_NOZORDER를 제거하여 HWND_TOP(0)으로 메모 자식 창을 같은 부모의 앞에 배치한다. SWP_NOACTIVATE는 유지한다. 다른 앱의 창·탐색기 창을 이동하지 않고 메모를 TopMost로 만들지 않는다.
- 초기 부착과 레이아웃 재확인 때 적용한다. 새로운 상주 작업·타이머·의존성을 추가하지 않는다.
- 격리된 부모와 전체 크기 형제 창으로 가림을 재현하고, 초기 배치·다시 가려진 후 레이아웃 복구·좌표 보존·동일 부모·비 TopMost를 검사한다.
- 실행형 테스트의 `--desktop-report`는 표시 문제를 진단하는 읽기 전용 경로다. 실제 사용자 메모를 변경하지 않는다.

## 검증

- 수정 전 회귀 검사: 초기 형제 창 순서 검증에서 실패했다.
- 수정 후 Release 빌드: 경고 0·오류 0. artifacts의 별도 출력과 기본 src/tests 출력 모두 빌드했다.
- `dotnet artifacts/desktop-visibility-validation/bin/WindowsMemo.Tests/release/WindowsMemo.Tests.dll --shell-probe`: 157개 통과(일반 자동 156개와 실제 바탕화면 호스트 1개).
- 저장 파일을 `artifacts/visibility-backup-20261118-151600`에 백업한 뒤 기존 앱을 종료하고 기본 Release 경로의 0.9.18을 실행했다.
- 재조회에서 메모 자식 창 2개가 SysListView32보다 앞에 있고 기존 화면 좌표를 유지함을 확인했다. 사용자가 실제로 메모가 보인다고 확인했다.
- 참고: [SetWindowPos 공식 규약](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos). HWND_TOP과 HWND_TOPMOST, SWP_NOZORDER의 의미를 구분한다.

## 남은 확인

현재 사용자 PC의 메모 표시 복구까지 확인했다. 이번 버전의 실제 Win+D·한글 조합·아이콘 조작·탐색기 재시작·다중 모니터/혼합 DPI·장시간 상주는 별도 확인이 필요하다. 임의의 Windows 셸에서 표시 순서가 계속 유지된다고 보장하지 않는다. 저장 형식·암호화·업데이트 적용은 변경하지 않았다.
