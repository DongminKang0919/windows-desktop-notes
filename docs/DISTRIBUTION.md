# 패키징·설치·로컬 시험 업데이트

[프로젝트 소개와 빠른 시작](../README.md)으로 돌아갑니다. 공개 배포·설치본 간 업데이트 검증은 아직 완료하지 않았습니다.

## ZIP 패키지 만들기

프로젝트 루트 PowerShell에서 실행합니다. 앱 버전은 공통 설정에서 읽으며, 현재 앱 버전은 0.9.8입니다.

```powershell
.\scripts\package.ps1
# 로그인된 Windows 세션에서 바탕화면 호스트 검증까지 포함하려면:
.\scripts\package.ps1 -ShellProbe
```

Release 빌드와 검증 후 Windows x64 publish 결과를 `artifacts/package-GUID` 아래 ZIP·SHA256 파일로 만듭니다. 매번 별도 폴더를 사용하며 기존 패키지를 덮어쓰지 않습니다. ZIP에는 실행 파일, 런타임 설정, 시작 안내와 파일별 해시·버전·소스 상태 manifest만 포함합니다. 사용자 메모와 테스트 데이터는 포함하지 않습니다.

ZIP 전체를 풀고 `WindowsMemo.exe`를 실행합니다. **.NET 10 Desktop Runtime x64가 필요**하며 런타임을 포함한 패키지는 아닙니다. 자동 실행 등록 후 폴더를 옮기면 다시 등록해야 합니다. 실제 압축 해제본의 UI 실행은 검토 대기입니다.

`scripts/verify-package.ps1 -ZipPath <ZIP 경로>`로 구성과 해시를 확인하고, `scripts/test-package.ps1 -ZipPath <ZIP 경로>`로 변조·위험 경로 거부를 확인할 수 있습니다. 해시는 손상 확인용이며 게시자 서명을 대신하지 않습니다.

[Build prototype ZIP](../.github/workflows/package.yml)은 GitHub Actions 수동 실행으로 다운로드용 ZIP과 체크섬을 14일 보관하도록 구성했습니다. GitHub Release 게시·설치·자동 업데이트는 수행하지 않습니다. 상세는 [패키징 검증 기록](results/PACKAGE-10A.md)을 참고해 주세요.

## prototype 설치 파일 만들기

```powershell
.\scripts\package-installer.ps1 -ShellProbe
```

ZIP 검증 후 Velopack으로 설치 EXE, full 업데이트 패키지와 피드를 `artifacts/package-GUID/installer`에 만듭니다. 설치 루트는 `%LOCALAPPDATA%\WindowsMemoApp`, 메모는 기존 `%LOCALAPPDATA%\WindowsMemo\Notes`로 분리합니다. .NET 10 Desktop Runtime x64가 없으면 설치기가 다운로드하도록 설정했습니다. 설치기는 완료 후 앱을 실행합니다.

`scripts/verify-installer.ps1 -ReleaseDirectory <폴더> -ExpectedVersion 0.9.8`으로 파일·피드·payload 해시를 검사하고, `scripts/test-installer.ps1 -ReleaseDirectory <폴더>`로 변조 거부를 확인합니다. [수동 installer CI](../.github/workflows/installer.yml)는 artifact만 생성합니다. **서명되지 않은 prototype이며 실제 설치·제거·버전 간 업데이트 검증은 남아 있습니다.** 기존 0.7.0 산출물은 [검증 기록](results/INSTALLER-10B1.md)을 참고해 주세요.

## 로컬 시험 업데이트

설치본을 종료한 후 설치된 실행 파일에 시험 폴더를 지정합니다. 폴더에는 신뢰하는 로컬 빌드의 `releases.win.json`과 더 높은 정식 버전의 full nupkg를 둡니다. 0.7.0에는 업데이트 UI가 없으므로 이 경로의 실제 시험에는 0.8.0 이상 설치본 두 버전이 필요합니다.

```powershell
& "$env:LOCALAPPDATA\WindowsMemoApp\current\WindowsMemo.exe" --update-test-source 'C:\UpdateTest'
```

트레이의 **업데이트 확인 (로컬 시험)**으로 다운로드하고, **저장 후 업데이트**를 선택해 확인하면 모든 메모를 저장한 뒤 재시작합니다. 저장 또는 적용 요청에 실패하면 편집기를 유지합니다. 다운로드만으로 적용하거나 앱 시작 때 자동 적용하지 않습니다. 일반 실행·로그인 실행에서는 시험 메뉴가 없으며 ZIP·진단 모드의 업데이트 시험은 지원하지 않습니다. 시험 폴더는 실행 인수로만 지정하며 로그인 옵션에 저장하지 않습니다.

서명되지 않은 로컬 시험용입니다. 원격 주소·다른 제품·시험판·delta 패키지는 받지 않습니다. 피드 1MiB·100항목, 패키지 256MiB 제한과 경로·크기·SHA256 검사를 적용합니다. 해시는 게시자 인증을 대신하지 않습니다. 공개 자동 업데이트와 설치본 간 데이터 보존 검증은 후속입니다. [구현·검증 기록](results/UPDATE-10B2A.md).
