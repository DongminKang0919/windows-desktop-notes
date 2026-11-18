# WM-012A — 저장 수명 반복과 5분 유휴 측정

- 날짜: 2026-10-22, 0.9.1 prototype, Windows x64 10.0.26300 / .NET SDK 10.0.401, 논리 CPU 8개.
- 결과: 메모 10개 × 100회 반복, 300초 유휴, 작업공간 폐기·재생성 후 복원 PASS. Release 경고/오류 0, 기존 전체 71개 검증(실제 셸 비표시 probe 포함) PASS.
- 제품 코드·버전·저장 스키마 변경 없음. 기존 앱은 종료하지 않았고 사용자 저장소를 사용하지 않았다.

## 시험 범위

`--workspace-probe`는 별도 GUID 폴더와 DPAPI 저장소를 만들고 실제 NoteWorkspace, NoteView, 자동 저장, DesktopNoteHost를 사용한다. 부모는 비표시 시험 HWND이며 Explorer는 아니다. 복구 타이머와 변경 관찰은 활성화한다. 5회 워밍업 뒤 동일 ID 10개에 편집/서식/종이색/접힘 변경 → 저장 → 숨김 → 재표시 → 보관 → 보관 해제 → 재표시를 100회 수행한다. 매회 저장 본문·굵기·글자색·종이색·접힘·목록 수와 원래 위치/펼친 크기, 복원된 창 좌표를 검증한다. 10회 간격으로 폐기한 편집기의 약한 참조 잔존 수를 확인한다.

마지막 100회차의 접힌 메모 10개를 유지한 채 300초 대기한다. 저장 본체/백업 파일 목록, SHA-256, 수정 시각이 모두 불변인지 비교한다. 이후 작업공간을 폐기·재생성하여 같은 ID와 본문·서식·상태·좌표를 복원한다. 종료 시 본체 10개, 백업 10개, 임시 파일 0개, 활성 표면 0개를 확인했다.

## 관측 결과

| 시점 | GC 후 관리 메모리 MiB | Private Bytes MiB | 프로세스 핸들 | GDI / USER |
| --- | ---: | ---: | ---: | --- |
| 워밍업 후 | 4.905 | 148.184 | 551 | 54 / 48 |
| 50회 | 4.627 | 150.539 | 588 | 54 / 52 |
| 100회 | 4.660 | 153.109 | 591 | 54 / 52 |
| 5분 유휴 후 | 4.438 | 135.551 | 545 | 54 / 46 |
| 복원 확인·최종 폐기 후 | 2.841 | 128.520 | 512 | 54 / 26 |

100회 반복에 약 184.9초가 걸렸다. 매 측정 구간 폐기 편집기 잔존 참조는 0개였다. 60회차 관리 메모리 6.210 MiB/Private Bytes 159.650 MiB 상승 뒤 감소했다. 이 시험에서 지속적으로 누적되는 편집기 참조나 GDI/USER 자원 증가는 관찰되지 않았다.

유휴 300.014초 동안 프로세스 CPU 시간은 1,406.25ms였다. 한 코어 기준 평균 0.469%, 논리 CPU 8개 전체 기준 0.0586%다. 샘플링 비용도 포함한다. 유휴 중에는 강제 GC 없이 기록했으며 프로세스 핸들은 최대 623개였다가 종료 측정의 GC 뒤 545개로 감소했다. 이 변화의 모든 원인을 규명한 것은 아니다. 강제 GC는 시험 도구에만 있다.

## 재현과 증거

실행 정책상 스크립트가 허용된 PowerShell에서는 `./scripts/test-workspace.ps1`로 새 출력 폴더에 빌드·실행한다. 기본값은 100회/300초이며 `-Cycles`, `-IdleSeconds`로 조정한다. 실행 정책을 변경할 필요 없이 직접 실행할 수도 있다. 아래 출력 경로는 다른 시험이 사용하지 않는 새 경로를 선택한다.

```powershell
dotnet build WindowsMemo.slnx --configuration Release --nologo -p:RestoreLockedMode=true --artifacts-path artifacts/workspace-validation
dotnet artifacts/workspace-validation/bin/WindowsMemo.Tests/release/WindowsMemo.Tests.dll --workspace-probe --cycles 100 --idle-seconds 300
```

시작 JSON의 `run` 폴더에 합성 Notes와 `metrics.jsonl`이 남는다. 스크립트는 실행별 고유 빌드와 console.log도 남긴다. 측정에 쓴 로컬 원본(artifacts는 Git 제외):

`artifacts/workspace-validation/bin/WindowsMemo.Tests/release/workspace-probe/1df13ef9250f4cf2b1e288488d1d2cec/metrics.jsonl`

유휴 대기의 음수 시간 경계 방어를 추가한 최종 코드도 1회/1초 smoke PASS. 스크립트의 새 빌드/실행 경로는 별도로 1회/0초 smoke PASS. 짧은 smoke의 CPU 수치는 5분 유휴 측정으로 사용하지 않는다.

## 남은 확인

8/24시간 전체 앱 상주, 보이는 창의 실제 입력/IME/Win+D, 혼합 DPI, Explorer 재시작, 절전 복귀와 설치본 업데이트는 포함하지 않는다. 해당 수동 검증 및 장시간 관측은 WM-012/WM-003/WM-010 후속이다. 짧은 구성요소 시험으로 전체 앱의 무누수·무취약점을 보증하지 않는다.
