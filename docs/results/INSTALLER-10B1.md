# WM-010B1 — 0.7.0 prototype 설치 패키징

- 날짜: 2026-10-13
- 환경: Windows 11 x64 26200.9457, SDK 10.0.401, Windows PowerShell 5.1
- 상태: 설치 파일 생성·정적 검증 완료, 실제 설치·제거·업데이트 실행은 미검증

## 변경 내용

- Velopack NuGet와 vpk 도구 1.2.161을 고정했다. 앱/테스트 NuGet lock 파일과 CI/ZIP 패키징의 RestoreLockedMode를 적용했다. Windows x64 RID를 프로젝트에 지정해 일반 빌드와 publish의 잠금 복원 대상을 일치시켰다.
- Main의 첫 동작으로 VelopackApp.Build().Run()을 배치했다. WPF 생성, 명령행 검증, Mutex와 메모 로딩보다 앞서 설치 훅을 처리한다.
- 설치 ID WindowsMemoApp의 기본 루트와 기존 WindowsMemo\\Notes 데이터 루트를 분리했다. 저장 스키마 3과 메모 경로는 유지한다.
- 검증된 ZIP payload에서 설치 EXE, full nupkg, win 피드, 설치 manifest를 생성한다. 매번 artifacts의 별도 폴더를 사용하고 기존 산출물을 덮어쓰지 않는다.
- .NET 10 x64 Desktop 런타임 bootstrap, 시작 메뉴 바로가기와 prototype 안내를 설정했다. 자동 업데이트 확인·적용 서비스는 추가하지 않았다.
- 수동 installer workflow는 빌드·검증 후 artifact만 보관하고 릴리스 게시를 수행하지 않는다.

## 검증

최종 `scripts/package-installer.ps1 -ShellProbe`:

- Release 빌드 경고 0 / 오류 0
- 기존 앱 검증 **53개 PASS** (자동 52 + 보이지 않는 실제 바탕화면 호스트 1)
- locked restore와 win-x64 publish PASS
- vpk가 Main의 VelopackApp.Run 시작 훅을 확인
- net10-x64-desktop 런타임 요구사항 확인
- 설치 manifest의 전체 산출물 크기·SHA256, 피드의 ID·버전·파일·크기·SHA1/SHA256, nupkg의 경로·nuspec ID/버전·앱 payload 파일별 해시 PASS

추가 검증:

- ZIP 거부 검증 **6개 PASS**: 정상본, ZIP 체크섬 오류, 파일 변경, 경로 traversal, 제품 버전 오류, artifacts 밖 출력
- 설치 거부 검증 **6개 PASS**: 정상본, 설치 EXE 변경, 외부 manifest 재계산 후 피드 버전 오류, 피드/외부 해시 재계산 후 payload 변경, 기대 버전 오류, 미등록 파일
- NuGet 알려진 취약점 조회: 앱·테스트 직접/전이 의존성에서 보고된 취약점 없음 (조회 시점 기준, 안전성 보증과 구분)
- 시험은 별도 artifacts 복사본에서 수행했다. 설치 EXE 실행, 사용자 메모 접근, 실제 로그인 Run 값 변경은 수행하지 않았다.

## 로컬 산출물

폴더: `artifacts/package-03d039a0613f4064af469b72acefde92/installer`

| 파일 | 크기 | SHA256 |
| --- | ---: | --- |
| WindowsMemoApp-win-Setup.exe | 10,169,582 bytes | 5c839d49a2fe7697361956ddd415aef84f257165c9d8fdd3f25a0517f8457015 |
| WindowsMemoApp-0.7.0-full.nupkg | 2,655,470 bytes | 49496f8591c6c37d12a386d09f229eb2917e862e1095d49927fcb67497d4537e |

원본 ZIP SHA256: `aa6a3e7ca312d764f957b1ffdcc6cb4403b67c04ec54041b8c5fd60d39c7427b`

payload manifest의 소스는 생성 시점 HEAD ffe3fab이며 sourceDirty=true다. 이 파일들은 커밋 전 변경사항으로 만든 로컬 prototype 증거이며 정식 릴리스 산출물이 아니다. artifacts는 Git에서 제외한다.

## 남은 범위

실제 설치·제거, 런타임 없는 PC bootstrap, 설치본의 한글·Win+D·저장/복원·휴지통·로그인 옵션, 설치한 두 버전 간 업데이트 및 실패 경로는 확인하지 않았다. 시작 훅의 구조와 vpk 검사 통과는 실제 설치/업데이트 성공을 뜻하지 않는다.

WM-010B2에서는 설치본 두 버전과 업데이트 서비스, 저장 완료 후 적용·저장 실패 시 중단·오프라인·잘못된 패키지·데이터 보존을 검증한다. 코드 서명과 게시자/배포 메타데이터 신뢰, 공개/비공개 배포 경로는 정식 자동 배포 전 결정한다. 현재 해시는 무결성 확인용이며 게시자 신뢰를 제공하지 않는다.
