# Windows 데스크톱 메모

GitHub 저장소: [windows-desktop-notes](https://github.com/DongminKang0919/windows-desktop-notes)

다른 프로그램 뒤에 놓이고, **바탕화면 보기를 실행해도 남아 있는 메모 앱**을 개발한다. Windows 스티커 메모에서 바탕화면 보기와 함께 메모까지 사라지는 불편을 해결하는 것이 출발점이다.

현재 버전은 **0.2.0 기술 검증 시제품**이다. 0.1.0에서 바탕화면 부착·편집을 확인한 뒤 연결 복구와 모니터 작업 영역 처리를 추가했다. 저장 기능을 추가하기 전 단계다.

> 이 시제품은 입력 내용을 저장하지 않는다. 종료하면 내용이 사라지므로 중요한 메모를 작성하지 않는다.

## 구현된 기능

- 탐색기의 바탕화면 뷰에 WPF 편집기를 자식 창으로 배치
- 일반 창 진단 모드
- 한글 및 여러 줄 편집, 선택·복사·텍스트 붙여넣기
- 굵게, 검정·빨강·파랑 글자 색상, 글머리 및 번호 목록
- 바탕화면 메모의 제목 영역 드래그 이동 및 오른쪽 아래 크기 조절
- 모니터 작업 영역과 부모 바탕화면 영역을 기준으로 위치·크기 제한
- 연결 손실 시 본문·서식을 유지하는 임시 일반 창과 최대 10회 자동 재연결
- 연결 실패 후 수동 재연결 버튼, 디스플레이·DPI 변경 처리
- 텍스트 전용 붙여넣기, 드롭 비활성화, 입력 크기 및 Undo 한도
- 시제품 중복 실행 방지와 종료 전 확인

저장·계정 기반 암호화·여러 메모·숨김 및 트레이·휴지통·로그인 자동 실행·자동 업데이트는 아직 구현하지 않았다. 탐색기 재시작 복구는 구현했으나 실제 재시작 시험은 미완료다. 현재 닫기는 시제품 종료이며, 최종 제품의 닫기 시 숨김 동작과 다르다.

## 검증된 범위

2026-10-13 Windows 빌드 26200.9457, x64 환경에서 다음을 확인했다.

- Release 빌드: 경고 0개, 오류 0개
- 0.2.0 복구 구현 초기 빌드에서 자동 검증 18개와 실제 바탕화면 호스트 생성·해제 검증 1개 통과
- 0.1.0 사용자 확인: Win+D 및 작업 표시줄 바탕화면 보기 후 메모 유지, 한글 입력과 서식 편집
- 0.1.0 사용자 확인: 다른 프로그램 창 복원 후 메모가 그 뒤에 놓이며 바탕화면 아이콘 클릭 정상

입력 자동 검증은 문서에 문자열을 삽입하는 시험이며, 실제 한글 조합은 사용자 확인으로 구분한다. 10회/100회 전환, 장시간 상주, 여러 모니터, 가상 데스크톱, 탐색기 재시작과 절전 복귀는 미검증이다. 보안·누수 검증 전체를 완료한 상태는 아니다.

상세 증거와 제한사항은 [첫 시제품 검증 결과](docs/results/SPRINT-01.md)와 [복구 검증 결과](docs/results/RECOVERY-02.md)에 기록했다. 0.2.0의 실제 Win+D·입력 회귀 및 탐색기 재시작 시험은 별도로 남아 있다.

최종 창 소유권 확인을 추가한 빌드는 컴파일에는 성공했지만 Windows 앱 제어 정책의 서명 요건으로 DLL 로드가 차단됐다(0x800711C7). 보안 설정은 변경하지 않았다. 현재 최종 파일의 실행 재검증이 필요하며 이전 19개 통과를 최신 파일의 검증 완료로 표시하지 않는다.

## 아키텍처

`Program`이 WPF 애플리케이션과 단일 실행 Mutex의 수명을 관리한다. 기본 모드에서는 `DesktopNoteHost`가 현재 탐색기의 바탕화면 뷰를 찾고 `HwndSource`로 자식 창을 만든다. `NoteView`는 편집 및 서식 명령을 처리하고 이동·크기 조절 요청을 호스트에 전달한다. 일반 창 모드에서는 같은 편집기를 WPF Window에 넣는다.

본문은 현재 메모리에만 유지한다. 네트워크, 사용자 데이터 파일, 데이터베이스 및 외부 API를 사용하지 않는다. 저장소와 MVVM 구조 확장은 후속 작업이다.

부착 방식은 탐색기의 내부 창 구조에 의존하는 실험 구현이다. 모든 Windows 버전에서 보장된 방식으로 표시하지 않는다. 부착 대상이 없으면 오류를 알리고, 일반 창 진단은 별도로 실행한다. 탐색기 프로세스 주입이나 탐색기 창 파괴는 하지 않는다.

실행 중 연결이 사라지면 같은 NoteView를 임시 일반 창으로 옮겨 보존하고 재부착을 시도한다. 재시도 실패 시 임시 창에서 계속 편집할 수 있다. 타이머·알림용 창·메시지 훅은 DesktopNoteHost가 소유하며 종료 시 해제한다.

## 프로젝트 구조

```text
Windows_Memo_Project/
├── WindowsMemo.slnx                    Visual Studio 솔루션
├── global.json                         SDK 버전 선택
├── Directory.Build.props               공통 버전·빌드 설정
├── src/WindowsMemo/
│   ├── Program.cs                      진입점, 실행 모드 및 종료
│   ├── app.manifest                    일반 사용자 권한·DPI 설정
│   ├── Views/NoteView.xaml(.cs)         메모 화면·서식·입력 제한
│   └── Interop/                        바탕화면 복구·작업 영역 및 Win32 호출
├── tests/WindowsMemo.Tests/             STA 자동 검증 실행 프로그램
├── scripts/dev.ps1                     빌드·실행·검증 편의 명령
└── docs/                               요구사항·설계·검증 결과
```

## 기술 스택과 준비 환경

- C# + WPF, 대상 프레임워크 `net10.0-windows`
- .NET SDK 10.0.401: `global.json`에서 동일 기능 대역의 최신 패치 허용
- 설치 및 빌드 확인 런타임: Windows Desktop 10.0.12, x64
- `HwndSource` 및 Win32 API: 바탕화면 뷰 탐색, 입력 활성화, 창 위치 관리
- 외부 NuGet 패키지 없음. 검증 프로그램도 별도 테스트 프레임워크 없이 실행

지원 목표는 사용자 Windows 11 PC부터다. Visual Studio 2026에서 `WindowsMemo.slnx`를 열고 앱 프로젝트를 시작 프로젝트로 선택할 수 있다. 명령줄 개발에는 .NET 10 SDK와 Windows Desktop 타기팅 팩이 필요하다. WPF에 별도의 `dotnet workload install`은 사용하지 않는다.

## 빌드 및 실행

프로젝트 루트의 PowerShell에서 실행한다.

```powershell
dotnet build WindowsMemo.slnx --configuration Release
```

빌드 후 바탕화면 부착 모드:

```powershell
.\src\WindowsMemo\bin\Release\net10.0-windows\WindowsMemo.exe
```

일반 창 진단 모드:

```powershell
.\src\WindowsMemo\bin\Release\net10.0-windows\WindowsMemo.exe --window
```

현재 Codex의 셸은 설치 후 PATH 갱신이 반영되지 않아 `C:\Program Files\dotnet\dotnet.exe`의 절대 경로로 빌드했다. PowerShell에서 `dotnet`을 찾지 못하면 새 창을 열거나 해당 경로로 실행한다.

`scripts/dev.ps1`은 SDK의 기본 설치 경로도 확인하는 편의 스크립트다. Mode는 Desktop, Window, Test, ShellProbe다. PowerShell 실행 정책을 변경하지 않고 위 직접 명령으로 실행해도 된다.

## 사용

바탕화면의 메모에 내용을 입력한다. 글자를 선택한 뒤 굵게 또는 색상 버튼을 누르고 목록을 만들 수 있다. 제목 영역으로 이동하고 오른쪽 아래 손잡이로 크기를 바꾼다. 닫기를 누르면 내용이 저장되지 않는다는 확인 후 종료한다.

바탕화면 보기 후 메모를 확인하고, 다른 창을 복원했을 때 그 뒤에 놓이는지 확인한다. 실제 부착·포커스·아이콘 동작은 환경별 수동 검증 대상이다.

## 테스트

먼저 솔루션을 빌드한 뒤 실행한다.

```powershell
dotnet tests/WindowsMemo.Tests/bin/Release/net10.0-windows/WindowsMemo.Tests.dll
```

실제 탐색기 바탕화면에 보이지 않는 시험 창을 생성·해제하는 추가 검증:

```powershell
dotnet tests/WindowsMemo.Tests/bin/Release/net10.0-windows/WindowsMemo.Tests.dll --shell-probe
```

검증은 입력 및 초과 입력 거부, 서식 명령, 악성 서식 대신 텍스트 붙여넣기, 드롭·Undo 제한, 폐기된 편집기의 수집 가능성과 호스트 수명을 확인한다. Win+D·IME·표시·이동 동작을 자동 검사하지 않는다. `dotnet test`용 테스트 프로젝트가 아니며 위 실행 명령의 종료 코드와 PASS/FAIL을 사용한다.

복구 시험은 테스트가 소유한 가짜 부모 창을 폐기·교체해 내용과 서식 보존, 재시도 한도, 반복 복구 및 참조 해제를 확인한다. 실제 탐색기를 자동 재시작하지 않는다. 위치 시험은 좌표 계산을 확인하며 실제 여러 모니터 지원을 입증하지 않는다.

바탕화면 부착은 Codex 샌드박스 안에서 접근 거부될 수 있다. 실제 검증은 현재 Windows 사용자 권한의 로컬 세션에서 수행했다. 앱 자체의 관리자 권한 요구는 없다.

## 다음 개발과 문서

- [작업 목록](docs/BACKLOG.md): WM-001/002 완료 기록과 후속 작업
- [개발 방식](docs/DEVELOPMENT.md): 칸반, 인수 조건, 주간 회고
- [요구사항](docs/REQUIREMENTS.md): 첫 제품 범위와 완료 기준
- [설계](docs/DESIGN.md): 전체 목표 구조와 초기 구현 관계
- [검증 계획](docs/VALIDATION.md): 수동·자동 시험
- [보안 설계](docs/SECURITY.md): 암호화, 외부 입력, 업데이트 신뢰
- [안정성 설계](docs/STABILITY.md): 리소스 수명, 누수·상주 시험
- [릴리스 설계](docs/RELEASES.md): 버전, 배포 및 자동 업데이트

로컬 Git 저장소의 기본 브랜치는 `main`이며 원격은 위 GitHub 저장소다. GitHub Projects·CI·라이선스 파일은 아직 없다. `.gitignore`는 빌드 출력과 진단 덤프가 추적되지 않도록 구성했다. README는 실제 구현과 검증 결과에 맞춰 갱신하며 계획을 완료 기능으로 표시하지 않는다.
