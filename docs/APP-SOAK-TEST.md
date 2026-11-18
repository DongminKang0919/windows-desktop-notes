# 실제 앱 8시간 상주 시험

이 시험은 일반 바탕화면 모드의 실제 앱 프로세스를 관찰한다. 격리된 `test-workspace.ps1`과 별개다. 장시간 무누수를 자동 판정하지 않는다. 앱과 측정 도구는 일반 사용자 권한으로 실행한다.

## 시작 전

1. 프로젝트의 `global.json`은 .NET SDK 10.0.401 및 호환되는 이후 패치를 요구한다. 작업 폴더에 설치한 SDK는 `./artifacts/dotnet-sdk10/dotnet.exe --version`으로 확인한다.
2. 중요한 메모를 백업하고 테스트용 메모를 준비한다. 일반 모드는 `%LOCALAPPDATA%\WindowsMemo\Notes`를 사용한다. 이 시험은 실제 사용자 데이터를 사용할 수 있다. 백업과 복원은 앱을 정상 종료한 후 수행한다.
3. 전원 연결을 확인하고 시험 중 절전·최대 절전·자동 재시작이 일어나지 않도록 Windows 설정을 확인한다. 화면이 꺼지는 것은 무방하다. 설정은 이 스크립트가 변경하지 않는다.
4. 저장소 루트에서 Release를 빌드하고 일반 모드로 실행한다. `--window`는 바탕화면 부착을 시험하지 않는 진단 모드다.

```powershell
& .\artifacts\dotnet-sdk10\dotnet.exe build WindowsMemo.slnx --configuration Release -p:RestoreLockedMode=true --artifacts-path artifacts/soak-build
.\artifacts\soak-build\bin\WindowsMemo\release\WindowsMemo.exe
```

빌드가 실패하면 시험을 시작하지 않는다. 이미 앱이 실행 중이면 먼저 해당 앱을 트레이의 **모든 메모 저장 후 종료**로 정상 종료하고 위 빌드의 앱을 실행한다. 실행 파일을 더블클릭해도 된다.

## 측정

테스트용 메모를 만들고 저장이 끝난 뒤 앱을 켜 둔다. 다른 PowerShell 창에서 다음 명령으로 PID를 확인한다. 여러 결과가 나오거나 경로가 예상한 Release 실행 파일과 다르면 대상부터 확인한다. 일반/진단 모드는 같은 이름이므로 **일반 모드로 실행했는지 직접 확인**한다.

```powershell
Get-Process WindowsMemo | Select-Object Id, ProcessName, Path, StartTime
```

아래 `1234`를 확인한 PID로 바꾼다. 이 명령은 해당 PowerShell 프로세스에만 실행 정책을 적용한다. 창을 열어 두고 8시간 동안 앱을 종료하거나 편집하지 않는다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\monitor-app.ps1 -ProcessId 1234
```

기본값은 8시간·60초 간격이다. 먼저 10초 동안 `-DurationSeconds 10 -IntervalSeconds 2`를 덧붙여 측정이 시작되고 완료되는지 확인할 수 있다. 짧은 측정은 8시간 결과가 아니다. 시험 중 PC 사용과 그 시간, 앱 오류·재부팅·절전 등을 기록한다. 엄격한 유휴 CPU 확인을 위해 최소 5분은 앱을 조작하지 않는다.

출력 경로는 화면에 표시되며 `artifacts/app-monitor-<ID>/`에 `samples.csv`와 `summary.json`이 생긴다. `completed`만 요청된 측정 시간이 완료된 상태다. `process_exited`는 중도 종료이며 원인을 확인해야 한다. Ctrl+C나 PC 종료 등으로 스크립트가 강제 중단되면 요약이 없거나 불완전할 수 있다. CSV의 마지막 시간도 확인한다. 측정값에는 프로세스 ID와 시각이 포함되므로 공유 전 검토한다.

CSV의 `cpu_machine_percent`는 **직전 샘플부터 현재 샘플까지**, 논리 프로세서 전체를 기준으로 한 앱 프로세스 평균이다. 첫 행은 비교 대상이 없어 비어 있다. 5분 평균은 해당 구간의 CPU 누적 시간 차이를 실제 경과 시간과 `summary.json`의 `logical_processors`로 나누어 구한다. Private Bytes·작업 집합·프로세스/GDI/USER 핸들의 추이를 함께 본다. 측정 도구는 강제 GC를 실행하지 않으며 관리 힙이나 네트워크 요청, 파일 변경의 원인을 측정하지 않는다. 유휴 중 저장이나 네트워크 반복은 별도로 확인해야 한다.

8시간 뒤 앱에서 테스트 메모의 본문·서식·위치·상태를 확인하고 트레이로 정상 종료한다. 앱 프로세스가 사라졌는지 확인한 후 재실행하여 보존 상태를 다시 확인한다. 필요하면 [안정성 기준](STABILITY.md)의 반복 조작·Explorer·절전 복귀 시험을 별도 수행한다.

## 결과 기록

아래를 복사해 로컬에 기록한다. 테스트 본문, 개인정보, 메모 파일이나 덤프를 공개 저장소에 올리지 않는다.

```text
날짜 / Windows 버전 / 앱 버전·커밋:
SDK / 실행 경로 / PID / 측정 결과 폴더:
시작·종료 시각 / 실제 측정 시간 / 완료 상태:
메모 개수와 표시·숨김 상태:
전원·절전·재시작 및 시험 중 PC 사용:
최소 5분 유휴 구간과 평균 CPU 산출값:
Private Bytes·작업 집합·프로세스/GDI/USER 핸들의 시작/종료/추세:
앱 멈춤·오류·예상 밖 저장 또는 네트워크 활동:
정상 종료 후 프로세스 종료 여부:
재실행 후 본문·서식·위치·상태 보존:
미완료 항목과 추가 조사:
```

유휴 5분 CPU가 전체 시스템 기준 1%를 넘거나, 자원이 지속적으로 증가하면 원인을 조사한다. 데이터 손실·크래시·멈춤·종료 실패는 별도로 기록하고 해결한다. 단일 측정이나 단순 증감만으로 누수 유무를 확정하지 않는다.
