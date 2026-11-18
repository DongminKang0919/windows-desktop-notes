# WM-007D: 목록 작업 안내와 읽기 중 닫기

2026-10-22. PR #14 병합 후 main `582045d` / 0.9.12를 확인했고 깨끗한 작업 트리에서 독립 브랜치로 시작했다. 앱 버전은 0.9.13 prototype이다.

## 변경

기존 `NoteListWindow`는 열기·복원·삭제 작업을 기다린 뒤 창 수명 확인 없이 실패 MessageBox를 열었다. 읽기 중 창을 닫아도 뒤늦은 대화상자가 뜰 수 있었고, 성공은 별도 결과 안내가 없었다.

- 진행·완료·실패를 목록 안에 줄바꿈하는 안내로 표시한다. 실패 시 기존 열기/복원/이동 버튼으로 다시 시도한다. 선택과 최신 편집 보존은 기존 작업공간의 정책을 따른다.
- 안내가 나타나면 상단 도움말을 접어 작은 창의 목록 공간을 확보한다. 색·모서리·여백은 기존 NoteTheme 값을 재사용한다. 색상 외에 문구로 결과를 구분한다.
- 닫힌 창 또는 폐기된 작업공간은 새 작업이나 결과 UI를 표시하지 않는다. 닫을 때 이벤트 구독·목록·안내 텍스트와 접근성 이름을 비운다. 진행 중 I/O는 강제 중단하지 않는다.
- 안내에 자동화 ID·문구와 같은 접근성 이름·Polite live 설정을 부여한다. 실제 화면 읽기 프로그램의 읽기 순서/알림은 수동 확인이 필요하다.
- 영구 삭제에는 기존 소유 창 지정·기본값 ‘아니요’ 확인을 유지한다. 확인 대화상자가 끝난 뒤 창 수명과 작업 중 여부를 다시 확인한다. 일반 결과를 모달 창으로 표시하지 않는 변경이며, 저장 실패 시 메모의 최신 내용 보존과 기존 전역 중요 알림은 유지한다.
- 정해진 안내 문자열만 사용한다. 예외 원문·저장 경로·메모 제목/본문을 안내나 로그에 넣지 않는다.

WPF + WinForms 트레이 구조, DPAPI CurrentUser·스키마 4·기존 데이터 호환성, 원본/백업/미완료 보호·고정 복구 ID, 숨김/보관/휴지통/복원/영구 삭제 확인, Win+D 부착과 TopMost 구분은 변경하지 않았다.

## 자동 검사

기존 실행형 `NoteListTests`에 세 검사를 추가했다.

1. 일반 목록·보관함·휴지통의 실제 파일 잠금 실패: 새 창 0, 원본·파일 집합·해시·수정 시각 보존, 버튼 재활성화와 잠금 해제 후 성공. 안내의 접근성 속성, 경로/본문 비포함, 100/150/200% 렌더와 작은 창에서 목록 52 DIP·버튼 28 DIP 이상을 확인한다.
2. 표시 중 메모의 휴지통 이동 저장 실패: 최신 본문과 선택·편집 가능 상태 보존, 잠금 해제 후 재시도와 실제 저장 파일의 최신 내용 확인.
3. 파일 읽기 중 목록 닫기 20회: 뒤늦은 안내 없음, 목록 비움, 파일 불변·새 표면 0, 완료 후 창 WeakReference 잔존 0. 테스트에서만 강제 GC를 사용한다.

시험은 출력 아래 GUID별 합성 데이터로 수행하며 실제 사용자 메모·실행 중 앱·기존 빌드 출력은 사용하지 않는다. 영구 삭제 확인 창 자체를 자동 조작하지 않았으며 저장소의 확인/휴지통 제한은 기존 회귀 검사가 검증한다.

## 로컬 실행 결과

```powershell
dotnet build WindowsMemo.slnx --configuration Release --nologo --artifacts-path artifacts/list-feedback-validation -p:RestoreLockedMode=true
dotnet artifacts/list-feedback-validation/bin/WindowsMemo.Tests/release/WindowsMemo.Tests.dll --shell-probe
dotnet artifacts/list-feedback-validation/bin/WindowsMemo.Tests/release/WindowsMemo.Tests.dll --crash-probe
dotnet artifacts/list-feedback-validation/bin/WindowsMemo.Tests/release/WindowsMemo.Tests.dll --recovery-probe --cycles 5 --idle-seconds 1
```

- Release 빌드 경고 0·오류 0, **145개 PASS**(기본 자동 144개 + 실제 비표시 호스트 1개). 이번 버전 실행에는 앱 제어 정책 차단이 없었다. 보안 설정·화이트리스트·서명·우회 실행은 변경하지 않았다.
- 중단 복구 **10건 PASS**(저장 경계 8 + 고정 ID 2), 중복 정리 검사 통과. 보고서: `crash-probe/e7e163b4f3184182b14df02e4fc35882`.
- 복구 창 수명 smoke: 워밍업 10회 + 측정 5회·유휴 1초. 최종 잔존 참조 0·원본 불변·마지막 재시도 성공, Handles 492 / GDI 10 / USER 13으로 기준값과 같았다. `recovery-probe/b1f8380181e647649db093a8e8bb8318/metrics.jsonl`. 1초 유휴로 성능이나 장시간 안정성을 판정하지 않는다.
- 목록·보관함·휴지통의 150% 실패 렌더를 육안 확인했다. README에 `notes-feedback-150.png`를 변형 없이 복사했다. SHA256 `23E207AE4741F57654B835C7D5A440BAF7E69D733F00DD7CD4F7988EDED5E50D`가 원본과 같다. 바탕화면 실행 캡처가 아닌 실제 WPF 컨트롤 렌더이며 사용자 자료는 없다.

## Windows CI 결과

코드 커밋 `3c2304f`의 [Windows CI #45](https://github.com/DongminKang0919/windows-desktop-notes/actions/runs/39780778060), 작업 `119831161057`이 전체 성공했다. 작업 로그에서 Release 경고 0·오류 0, 자동 **144개 PASS**(새 안내 회귀 3개 포함), 중단 복구 **10건**(8 + 고정 ID 2), 수명 smoke의 최종 잔존 참조 0·원본 불변·재시도 성공을 확인했다. 측정 JSONL 및 UI artifact 업로드도 성공했다.

## 남은 확인

실제 표시된 창의 키보드/마우스 입력·화면 읽기 프로그램·DPI 변경·Windows 종료/취소 경합은 수동 확인이 필요하다. 로그아웃·재부팅·Explorer 재시작은 실행하지 않았다. 게시자 인증·서명·실제 설치본 업데이트와 8/24시간 전체 앱 상주는 미완료다. 이 검사는 무누수·무취약점을 보장하지 않는다.
