# WM-004B — 저장 경계의 강제 종료와 새 프로세스 복구

- 날짜: 2026-10-22, 0.9.3 prototype, Windows x64 26300 / SDK 10.0.401.
- 기존 PR #4의 저장 실패·데이터 보호 검증을 이어간다. 버전·제품 동작·스키마 4·DPAPI·데이터 경로·창 구조는 바꾸지 않는다.

## 방식

저장 코드는 임시 파일을 Flush(flushToDisk: true)한 뒤 원본을 교체한다. 손상된 원본에서 백업 복구한 경우에는 원본을 .damaged-*로 보존하고 새 원본을 이동한다. FileNoteRepository에 인스턴스 단위 internal 관찰 지점 3개를 추가해 임시 기록 완료·손상 원본 이동·교체 완료 직후를 확인한다. 공개 생성자는 관찰 콜백이 항상 null이며, 제품 CLI/환경 변수에는 시험 모드를 추가하지 않았다.

선택 실행 `--crash-probe`는 각 시나리오마다 빌드 출력 아래 별도 GUID 저장소를 만든다. 쓰기 자식이 관찰 지점에 도달했다고 표준 출력으로 알리면 부모가 직접 생성한 Process 객체만 Kill한다. 프로세스 목록·이름·외부 PID로 대상을 찾지 않는다. finally/Dispose를 실행하지 않은 중단 상태를 별개의 읽기 자식 프로세스에서 검증한다. 8개 쓰기 자식과 8개 읽기 자식을 사용한다.

읽기 자식은 DPAPI 복호화, ID·스키마·종이색·접힘·표시/보관/삭제 상태, 한글/체크 기호 본문·굵기·글자색·화면 좌표·DIP 크기·배율을 확인한다. 백업 존재 여부와 이전 정상본 바이트, 손상 원본의 보존 바이트, 남은 임시 파일 개수도 부모가 확인한다. 자동 저장 완료 응답 시나리오는 실제 NoteView/AutosaveCoordinator.FlushAsync 성공 후 종료한다.

자식의 시작/읽기에는 30초 제한, 종료 대기에는 10초 제한이 있다. 부모가 사라져도 대기 자식이 계속 남지 않도록 45초 후 자체 종료한다. 경로 입력은 GUID로 제한하고 링크된 출력 경로를 거부한다. 시험 기록·합성 파일은 삭제하지 않고 artifacts 아래 남긴다.

## 로컬 결과: 8개 PASS

| 중단 지점 | 새 프로세스의 읽기 결과 | 남은 .tmp | 손상 보존본 |
| --- | --- | ---: | ---: |
| 최초 저장: 임시 기록 후, 교체 전 | 확정된 메모 없음, 임시 파일 자동 승격 안 함 | 1 | 0 |
| 최초 저장: 교체 후 | 새 저장본 | 0 | 0 |
| 정상 교체: 임시 기록 후 | 이전 원본 | 1 | 0 |
| 정상 교체: 교체 후 | 새 저장본, 이전 정상 백업 유지 | 0 | 0 |
| 손상 복구: 임시 기록 후 | 이전 정상 백업 | 1 | 0 |
| 손상 복구: 손상 원본 이동 후 | 원본 부재 상태에서 이전 정상 백업 | 1 | 1 |
| 손상 복구: 새 원본 교체 후 | 새 저장본, 정상 백업·손상 보존본 유지 | 0 | 1 |
| 자동 저장 FlushAsync 성공 응답 후 | 새 저장본·본문/서식/배치/상태 | 0 | 0 |

이 작업 시점의 Release 빌드는 경고/오류 0, 기존 전체 79개(자동 78개 + 실제 셸 비표시 probe 1개)도 PASS. 관찰 경계에서의 결과가 기존 저장 설계와 일치했다. 후속 [WM-004C](TEMPORARY-04C.md)에서 고유한 중단 스냅샷은 보존하되 더 최근 정상 원본과 동일한 오래된 중복만 정리하도록 확장했다. 확장된 probe는 고유 잔여 파일을 보존하는 8개 시나리오와 실제 잔여 파일이 중복이 된 뒤에만 정리되는 추가 검증을 포함한다.

```powershell
dotnet build WindowsMemo.slnx --configuration Release --nologo -p:RestoreLockedMode=true --artifacts-path artifacts/crash-validation
dotnet artifacts/crash-validation/bin/WindowsMemo.Tests/release/WindowsMemo.Tests.dll --crash-probe
dotnet artifacts/crash-validation/bin/WindowsMemo.Tests/release/WindowsMemo.Tests.dll --shell-probe
```

시작 JSON의 report 폴더에 results.jsonl, 각 통과 행의 folder에 격리 Notes가 남는다. 로컬 원본(artifacts는 Git 제외):

`artifacts/crash-validation/bin/WindowsMemo.Tests/release/crash-probe/0a9776fb5e7e4161827d74d50c3a8132/results.jsonl`

Windows CI는 기존 자동 검증 뒤 `Run isolated crash recovery probe` 단계로 동일 8개 시나리오를 실행한다. 실제 셸 probe는 로컬에서만 실행한다.

## 확인하지 않은 범위

이는 메모 저장 구성요소의 별도 프로세스 중단 시험이다. 사용자 앱 프로세스·트레이·실제 Explorer·PC 전원은 종료하지 않았다. 교체 이전의 새 편집, 실제 전원 차단/OS 종료, 저장 장치의 캐시·컨트롤러·물리 장애에서 마지막 입력까지 보존한다는 보증이 아니다. 본문 손상은 합성 원본에 미리 주입했으며 실제 디스크 손상을 유발하지 않았다. 실제 Win+D·IME·혼합 DPI와 8/24시간 상주 시험도 남아 있다.
