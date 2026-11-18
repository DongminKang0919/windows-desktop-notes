# 0.10.0 최신 소스 재검증 — 2026-11-19

## 대상과 결과

- 원격 main `d4ad115`의 앱 코드, 버전 0.10.0 prototype, SDK 10.0.401. 문서 정리는 로컬 `5a549f0`에 기록했다.
- Release 빌드 성공, 경고 0·오류 0. 출력은 `artifacts/validation-2026-11-19`로 분리했다.
- 자동 회귀 검사 **164개 PASS**.
- `--crash-probe`: 저장 중단 경계 **8개 PASS**, 복구 ID 새 프로세스 확인 **2개 PASS**.
- `--shell-probe`: 회귀 검사와 실제 바탕화면 호스트 생성·부착·접기/펼치기·해제를 포함해 **165개 PASS**. Win+D·실제 IME 조합·화면 가시성의 수동 검증을 대신하지 않는다.

처음 샌드박스 빌드는 사용자 NuGet.Config 접근 제한으로, 샌드박스 회귀 검사는 DPAPI 사용자 프로필 접근 제한으로 중단됐다. 정상 사용자 환경에서 다시 실행한 위 검사는 모두 종료 코드 0이다. 앱 결함 수정이나 보안 설정 변경은 하지 않았다.

## 재현 명령

저장소 루트에서 SDK 실행 경로를 지정해 실행했다.

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' build WindowsMemo.slnx --configuration Release --nologo -p:RestoreLockedMode=true --artifacts-path artifacts/validation-2026-11-19
& 'C:\Program Files\dotnet\dotnet.exe' artifacts/validation-2026-11-19/bin/WindowsMemo.Tests/release/WindowsMemo.Tests.dll
& 'C:\Program Files\dotnet\dotnet.exe' artifacts/validation-2026-11-19/bin/WindowsMemo.Tests/release/WindowsMemo.Tests.dll --crash-probe
& 'C:\Program Files\dotnet\dotnet.exe' artifacts/validation-2026-11-19/bin/WindowsMemo.Tests/release/WindowsMemo.Tests.dll --shell-probe
```

로컬 로그는 같은 출력 폴더의 `regression.log`, `crash.log`, `shell-probe.log`다. 사용자 메모와 로컬 백업은 Git 추적 대상이 아니다.

## 실제 앱 접근 확인과 남은 검토

computer-use로 위 출력의 WindowsMemo.exe를 일반 바탕화면 모드로 실행했다. 실행 전 실행 중인 WindowsMemo 프로세스가 없음을 확인하고, 기존 Notes 폴더의 일반/Diagnostic 메모 총 8개 파일을 `artifacts/validation-2026-11-19/pre-ui-notes-backup`에 복사했다. DPAPI 암호문 백업은 같은 Windows 사용자 계정의 복구용이며 다른 PC 이전용 백업은 아니다.

- 접근성 트리에서 기존 일반 메모 2개와 메모 목록·설정 창을 확인했다.
- 설정 너비를 320→360으로 변경해 저장하고, 실제 settings.json의 `NoteWidth: 360`을 확인했다. 높이는 300, 종이색은 Butter, 직접 실행 시 목록 표시는 true다. 시험값 360을 남긴 상태이므로 수동 검토 후 원하는 값으로 조정한다.
- 새 메모를 생성하고 목록이 2→3개로 증가한 것과 생성 성공 안내를 확인했다.
- 실행 후 일반 메모의 본체/백업 4개 파일은 실행 전 백업과 달라졌다. Diagnostic 4개는 동일하다. 일반 메모의 변경 원인이나 내용 보존을 파일 해시만으로 판정하지 않는다. 원래 암호문 백업은 별도로 유지한다.
- 자동화 화면 캡처는 메모 UI 대신 다른 화면을 반환했고 바탕화면 자식 메모는 도구의 창 목록에 나타나지 않았다. 따라서 실제 메모의 색상·크기·본문·서식·F2·Win+D를 눈으로 검증한 것으로 기록하지 않는다.
- 사용자 직접 검토: 한글 입력, F2 제목 변경, 숨김/재표시, 정상 종료를 수행했고 “큰 문제 없어보인다”고 보고했다. 해당 항목은 사용자 확인 완료로 기록하며 반복 횟수나 세부 조건을 추가로 가정하지 않는다.
- Win+D, 서식의 세부 동작, 종료 후 재실행의 본문·제목·서식·위치·상태 및 설정 보존은 아직 확인되지 않았다. 자동 실행 등록, 보관/휴지통 UI, 실제 저장 실패 UI 검토도 미완료다.

WM-015는 Review로 유지하며 현재 버전 8/24시간 시험도 별도로 남긴다. 이번 작업에서 원격 푸시·릴리스·로그인/로그아웃·Explorer 재시작은 수행하지 않았다.
