# WM-004C — 오래된 중복 임시 파일만 정리

- 날짜: 2026-10-22, 0.9.4 prototype, Windows x64 26300 / SDK 10.0.401.
- 진행 중 PR #4가 병합되어, 파일 내용이 이전 검증 기준과 동일한 최신 main 5ec3ad7에서 독립 브랜치로 옮겼다. WPF·스키마 4·DPAPI·저장 경로와 메모 기능을 유지한다.

## 삭제 조건과 보존 범위

작업공간 초기화 중 메모 세션을 만들기 전에 Task.Run에서 한 번 실행한다. GUID.tmp 형식의 파일 중 파일 수정 시각이 24시간 이상 지난 것만 후보로 삼는다. 임시 파일과 현재 .json 원본을 같은 기존 DPAPI·스키마 검증기로 읽고, 다음을 모두 확인한다.

- 같은 ID·스키마·생성 시각·RTF 바이트·좌표/크기/배율·종이색·접힘·표시/보관/삭제 상태.
- 임시 스냅샷의 UpdatedAtUtc가 원본보다 새롭지 않음.
- 같은 RTF 바이트를 기존 문서 검증기로 확인함. 메타데이터만 정상인 잘못된 RTF도 보존함.
- 현재 정상 원본이 반드시 존재함. 원본이 손상되거나 없으면 백업이 있어도 삭제하지 않음.

임시 파일은 읽기+DELETE 권한의 배타적 SafeFileHandle로 열고, 원본은 다른 쓰기/삭제를 막는 읽기 핸들로 유지한다. 검사한 임시 핸들에 FILE_DISPOSITION_INFO를 설정해 닫힐 때 삭제한다. 판정 후 경로를 다시 열어 삭제하지 않는다. 읽기 전용 속성을 해제하거나 권한을 높이지 않는다. OPEN_REPARSE_POINT와 파일/루트 링크 검사를 사용한다.

읽지 못하거나 확신할 수 없는 파일은 보존한다. 고유한 미완료 본문을 자동 승격하거나 오래됐다는 이유만으로 삭제하지 않는다. 로그·새 파일에 복호화된 본문을 쓰지 않는다. 결과는 내용 없는 집계(검사/정리/보존/실패/한도)로만 작업공간에 보관한다.

## 자원 한도

한 번에 최대 64개 .tmp 항목과 합계 16MiB의 임시/원본 파일 읽기를 허용한다. 남은 예산으로 파일 전체를 검사할 수 없으면 중단한다. 경과 시각 확인이나 파일 열기 같은 메타데이터 호출은 읽기 바이트 예산과 구분한다. 네트워크/장치 자체의 지연 시간 상한을 보장하는 정책은 아니다.

파일 검사·복호화는 작업 스레드, RTF 검증은 WPF dispatcher에서 수행한다. 읽은 파일 핸들은 continue/break/예외에서도 using으로 해제한다. 새 타이머·상주 스캐너·외부 의존성을 추가하지 않았다. 한도 때문에 고유 파일이 앞에 많으면 뒤의 중복은 이번 시작에서 정리되지 않을 수 있다. 보존 데이터의 무한 누적을 막는 전체 보존/복구 정책은 별도 후속이다.

## 검증 결과

- Release 경고/오류 0, 로컬 전체 87개 PASS(자동 86개 + 실제 셸 비표시 probe 1개).
- 오래된 동일 스냅샷만 삭제하고 원본/백업 바이트를 보존했다. 검증 콜백 중 임시/원본에 쓰기 핸들을 열 수 없는지도 확인했다.
- 본문·좌표·종이색·접힘·숨김·보관·삭제 상태·ID·생성 시각·더 새로운 수정 시각·스키마 차이는 보존했다.
- 최근·잠긴 임시 파일/원본·읽기 전용·손상·미래 형식·과대 파일·알 수 없는 이름·잘못된 RTF·문서 검증 예외에서는 삭제하지 않았다.
- 67개 중복 중 첫 실행 64개, 다음 실행 3개만 정리했다. 읽기 예산 초과 시 남은 파일을 보존했다.
- 작업공간 시작에서 중복 하나만 제거하고 최근/고유 파일과 기존 메모 ID·본문을 보존했다. 고유한 미완료 본문은 메모로 추가되지 않았다.
- 별도 자식 프로세스를 중단한 기존 8개 시나리오에서 잔여 고유 파일을 이틀 전 시각으로 바꿔도 보존됐다. 그중 실제 잔여 파일을 같은 내용으로 정상 저장한 뒤에만 중복이 제거됐으며, 새 읽기 프로세스에서 정상 데이터를 재확인했다.

```powershell
dotnet build WindowsMemo.slnx --configuration Release --nologo -p:RestoreLockedMode=true --artifacts-path artifacts/temporary-validation
dotnet artifacts/temporary-validation/bin/WindowsMemo.Tests/release/WindowsMemo.Tests.dll --shell-probe
dotnet artifacts/temporary-validation/bin/WindowsMemo.Tests/release/WindowsMemo.Tests.dll --crash-probe
```

중단/정리 통합 실행 원본(artifacts는 Git 제외):

`artifacts/temporary-validation/bin/WindowsMemo.Tests/release/crash-probe/9ba37087f17340bd8fae589b9618f45e/results.jsonl`

시험에서 만든 GUID 저장소 파일만 정리했다. 사용자의 실제 메모 저장소·앱·실제 Explorer·시스템 설정은 변경하지 않았다. CI의 기존 자동 검증 및 중단 복구 단계에 포함된다.

## 남은 확인

실제 링크/정션과 권한 정책 조합, 네트워크/매체 장애·전원 차단, 대규모 파일 구성의 시작 지연, 보이는 앱의 시작과 8/24시간 상주는 별도 확인이다. 링크 거부는 코드로 확인했으며 이 작업에서 실제 링크 공격 시험을 수행한 것으로 표시하지 않는다. 고유 미완료 데이터를 사용자가 미리 보고 복구/정리하는 UI도 후속이다.

참고: Microsoft [SetFileInformationByHandle의 DELETE 권한·삭제 시점](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-setfileinformationbyhandle), [FILE_DISPOSITION_INFO의 BOOLEAN 필드](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_disposition_info).
