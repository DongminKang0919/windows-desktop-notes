# Windows 데스크톱 메모

**Win+D를 눌러도 바탕화면에 남아 있는 Windows 메모 앱입니다.**

다른 프로그램을 사용할 때는 창 뒤에 놓이고, 바탕화면 보기에서는 메모를 바로 확인하고 편집할 수 있도록 만들고 있습니다. C#과 WPF로 개발하며 메모는 이 PC에 저장합니다.

> 현재 버전은 **0.3.0 개발 시제품**입니다. 메모 하나의 자동 저장·복원을 구현했으며, 설치 프로그램과 정식 릴리스는 아직 제공하지 않습니다. 바탕화면 부착은 Windows 탐색기의 내부 창 구조에 의존하므로 환경에 따라 동작이 달라질 수 있습니다.

## 주요 기능

- **바탕화면 메모:** 다른 앱 뒤에 표시되며 Win+D와 작업 표시줄의 바탕화면 보기에서도 유지
- **서식 편집:** 한글·여러 줄 입력, 굵게, 검정·빨강·파랑 글자 색상, 글머리·번호 목록
- **이동과 크기 조절:** 제목 영역을 드래그해 이동하고 오른쪽 아래 손잡이로 크기 변경
- **자동 저장과 복원:** 본문·서식·위치·크기를 저장하고 다음 실행에서 복원
- **로컬 암호화:** Windows 사용자 계정 범위의 DPAPI로 저장 파일·임시 파일·백업 보호
- **연결 복구:** 바탕화면 연결이 끊기면 임시 창에서 편집을 유지하고 최대 10회 재연결 시도

여러 메모, 트레이에서 숨김·복원, 휴지통, 로그인 자동 실행, 자동 업데이트는 후속 개발 항목입니다. 현재 **닫기 버튼은 저장 후 앱을 종료**합니다.

## 시작하기

### 준비 환경

- Windows 11 x64: 현재 검증 대상
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0): 프로젝트는 SDK 10.0.401을 기준으로 동일 기능 대역의 최신 패치를 허용
- C# + WPF, 대상 프레임워크 `net10.0-windows`

Visual Studio에서는 `WindowsMemo.slnx`를 열고 `WindowsMemo`를 시작 프로젝트로 선택합니다. 명령줄에서는 Windows Desktop 타기팅 팩을 포함한 SDK가 필요합니다.

### 빌드와 실행

저장소를 내려받은 뒤 프로젝트 루트의 PowerShell에서 실행합니다.

```powershell
dotnet build WindowsMemo.slnx --configuration Release
.\src\WindowsMemo\bin\Release\net10.0-windows\WindowsMemo.exe
```

바탕화면 부착을 제외하고 편집·저장을 확인하려면 일반 창 진단 모드를 사용합니다. 이 모드는 별도의 메모 파일을 사용합니다.

```powershell
.\src\WindowsMemo\bin\Release\net10.0-windows\WindowsMemo.exe --window
```

`dotnet`을 찾지 못하면 SDK 설치 후 PowerShell을 새로 열어 주세요. 기본 설치 경로는 `C:\Program Files\dotnet\dotnet.exe`입니다. 편의 명령은 [scripts/dev.ps1](scripts/dev.ps1)에서 확인할 수 있습니다.

## 사용 방법

1. 바탕화면의 메모를 클릭해 내용을 입력합니다.
2. 글자를 선택하고 서식 버튼으로 굵게·색상·목록을 적용합니다. 외부 붙여넣기는 텍스트만 허용합니다.
3. 제목 영역과 오른쪽 아래 손잡이로 위치·크기를 조절합니다.
4. 아래쪽의 **저장 완료** 표시를 확인합니다. 닫기를 누르면 마지막 변경을 저장한 뒤 종료합니다.
5. 다시 실행하면 저장된 메모를 복원합니다.

입력·서식·위치 변경 후 약 0.5초가 지나면 자동 저장합니다. 계속 입력하는 동안에도 최대 대기 시간 5초를 기준으로 저장을 시도합니다. 저장 실패 시 내용을 유지하고 재시도 버튼을 표시하며, 저장에 실패한 상태에서는 닫기로 종료하지 않습니다. 강제 종료나 정전 직전의 미저장 변경까지 보존하는 것은 보장하지 않습니다.

## 저장과 백업

데이터 폴더는 `%LOCALAPPDATA%\WindowsMemo\Notes`입니다. 일반 창 진단 모드는 그 아래 `Diagnostic` 폴더를 사용합니다. 파일명에는 메모 내용이 들어가지 않습니다.

- 본문·서식·위치·크기를 암호화해 저장합니다. 외부 서버나 API로 메모를 전송하지 않습니다.
- 저장 파일을 교체할 때 이전 정상본 하나를 `.bak` 파일로 보관합니다. 첫 저장에는 이전 파일이 없으므로 백업도 없습니다.
- 원본을 읽지 못하면 백업으로 복구하고 안내합니다. 이후 저장할 때 손상된 원본은 `.damaged-…` 파일로 보존합니다.
- 원본과 백업 모두 복원하지 못하면 파일을 덮어쓰지 않고 앱 시작을 중단합니다.

암호화 데이터는 저장한 Windows 사용자 계정과 PC에서 사용하는 것을 전제로 합니다. 다른 PC·계정으로 옮기는 내보내기·복구 기능은 아직 없습니다. 같은 계정으로 실행되는 다른 프로그램이나 화면·클립보드까지 보호하지는 않습니다. 자세한 기준은 [보안 설계](docs/SECURITY.md)를 참고해 주세요.

## 현재 검증 범위

Windows 빌드 26200.9457, x64 환경에서 0.3.0 Release 빌드와 자동 검증 **27개**를 통과했습니다. 입력 제한·서식, 연결 복구, RTF 왕복, 암호화 저장, 손상 파일 복구, 쓰기 실패와 재시도, 실제 바탕화면 시험 창 생성·해제를 포함합니다.

Win+D, 실제 한글 조합, 창 복원 후 표시 순서와 바탕화면 아이콘 클릭은 0.1.0에서 사용자 확인을 받았습니다. **0.3.0의 실제 앱 재실행·입력·Win+D 회귀, 탐색기 재시작, 여러 모니터·혼합 배율, 가상 데스크톱, 절전 복귀, 장시간 상주 검증은 남아 있습니다.**

상세 결과는 [초기 시제품](docs/results/SPRINT-01.md), [연결 복구](docs/results/RECOVERY-02.md), [저장·복원](docs/results/STORAGE-03.md) 기록을 참고해 주세요.

## 개발과 테스트

외부 NuGet 패키지와 별도 테스트 프레임워크 없이 구성했습니다. 테스트는 `dotnet test` 대신 다음 실행 프로그램의 PASS/FAIL과 종료 코드로 확인합니다.

```powershell
dotnet build WindowsMemo.slnx --configuration Release
dotnet tests/WindowsMemo.Tests/bin/Release/net10.0-windows/WindowsMemo.Tests.dll
```

실제 바탕화면에 보이지 않는 시험 창을 생성·해제하는 검증도 포함하려면:

```powershell
dotnet tests/WindowsMemo.Tests/bin/Release/net10.0-windows/WindowsMemo.Tests.dll --shell-probe
```

저장 시험 데이터는 테스트 빌드 출력 폴더 아래에 생성하며 사용자 메모 폴더를 사용하지 않습니다. 바탕화면 검증은 로그인된 Windows 세션에서 실행해야 합니다. 앱은 관리자 권한을 요구하지 않습니다.

### 프로젝트 구조

```text
src/WindowsMemo/
  Program.cs        앱 시작·복원·종료 및 단일 실행
  Views/            메모 편집기와 저장 상태 UI
  Models/           저장 가능한 메모와 창 위치
  Services/         제한된 RTF 직렬화·자동 저장
  Infrastructure/   계정 암호화·파일 교체·백업 복구
  Interop/          바탕화면 부착·복구 및 Win32 연동
tests/              WPF·저장·복구 검증 실행 프로그램
scripts/            개발 편의 명령
docs/               요구사항·설계·검증 결과
```

## 개발 문서

- [작업 목록](docs/BACKLOG.md) · [개발 방식](docs/DEVELOPMENT.md)
- [요구사항](docs/REQUIREMENTS.md) · [설계](docs/DESIGN.md)
- [검증 계획](docs/VALIDATION.md) · [안정성 설계](docs/STABILITY.md)
- [보안 설계](docs/SECURITY.md) · [릴리스 계획](docs/RELEASES.md)

현재 라이선스 파일과 CI는 아직 추가하지 않았습니다.
