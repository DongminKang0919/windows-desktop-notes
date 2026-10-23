# WM-010A2: 배포 아카이브 검증과 패키징 출력 격리

2026-10-22. PR #16 병합 main `5fbe678` / 0.9.14와 깨끗한 작업 트리를 확인한 뒤 별도 브랜치에서 0.9.15로 작업했다.

## 발견과 변경

`verify-package.ps1`은 디렉터리 헤더를 검사 대상에서 제외했고, ZIP/설치 검증 스크립트의 예약 이름·끝 점/공백·링크 속성·파일/폴더 충돌 검사가 충분하지 않았다. `package.ps1`은 빌드와 하네스에 기본 src/tests 출력을 사용하고, START-HERE에 실제 구현과 다른 스키마 3을 표시했다.

- `archive-validation.ps1`을 두 검증기에 연결했다. 모든 파일/디렉터리 경로에서 탈출·절대 경로·역슬래시·빈 구성요소·Windows 금지 문자·ADS·예약 장치명·끝 점/공백을 검사한다. COM/LPT 위 첨자 1/2/3과 예약 이름의 확장자도 거부한다.
- 대소문자를 무시하는 중복·파일/디렉터리 이름 충돌·파일을 부모로 쓰는 경로, Unix 링크/특수 파일·디렉터리 속성 불일치·DOS 재분석 속성을 거부한다.
- 디렉터리 포함 1,000항목, 개별 16MiB, 압축 해제 합계 256MiB 상한을 둔다. 이 검사는 추출 전에 ZIP 메타데이터를 검사한다. 기존 manifest·제품·버전·파일 해시·데이터 제외 검사는 유지한다.
- 패키징 빌드/하네스/publish 중간 출력은 `artifacts/package-GUID/build`로 분리한다. 생성 안내는 스키마 4와 이전 1/2/3 호환성으로 정정했다. 실제 메모 형식은 바꾸지 않았다.

Windows 예약 이름·확장자·위 첨자·끝 점/공백·대소문자 규칙은 [Microsoft 파일 이름 문서](https://learn.microsoft.com/en-us/windows/win32/fileio/naming-a-file)와 대조했다. 검사 범위는 배포 검증 스크립트이며 앱의 저장소·업데이트 파서 교체가 아니다.

## 검사

`test-archive-validation.ps1`은 GUID별 artifacts에 합성 ZIP과 설치 산출물을 만든다. 가짜 EXE/DLL 바이트는 실행하지 않으며 압축을 풀거나 설치하지 않는다. outer checksum/feed/manifest도 합성 데이터에 맞춰 생성해 경로 검사 자체의 오류 메시지를 확인한다. 정상 디렉터리 구조와 경로 탈출·예약 이름·대소문자/부모 충돌·링크/특수 속성·항목 수/합계 크기를 양쪽 검증기로 검사한다. 검증 전후 fixture 파일 집합과 해시도 확인한다.

기본 CI에 합성 검사를 추가했다. 격리 출력 변경을 실제로 확인하도록 `package.ps1`을 통한 ZIP 생성과 기존 `test-package.ps1` 6개 검사를 별도로 연결했다. 이 통합 검사는 패키징 자체의 빌드/하네스를 다시 실행한다. 기본 앱/하네스 DLL의 해시·수정 시각을 패키징 전후 비교하는 검사와 빌드 로그 출력도 추가했다. 서명·설치·릴리스 게시·실제 설치본 업데이트를 수행하지 않는다.

## 로컬 결과

```powershell
dotnet build WindowsMemo.slnx --configuration Release --nologo --artifacts-path artifacts/archive-validation -p:RestoreLockedMode=true
dotnet artifacts/archive-validation/bin/WindowsMemo.Tests/release/WindowsMemo.Tests.dll --shell-probe
.\scripts\test-archive-validation.ps1
```

- Release 빌드 경고 0·오류 0, 기존 실행형 하네스 **150개 PASS**(자동 149 + 실제 비표시 호스트 1).
- 새 PowerShell 스크립트는 실행 정책의 PSSecurityException/UnauthorizedAccess로 차단됐다. **로컬 아카이브 실행 검사와 실제 ZIP 생성은 미검증**이다. 보안 설정·실행 정책·화이트리스트를 변경하거나 우회 실행하지 않았다.
- PowerShell 5.1의 Parser.ParseFile을 통한 수정 스크립트 6개의 정적 구문 검사 통과. 이 검사는 스크립트를 실행하지 않으며 실행 검증을 대신하지 않는다.

## Windows CI 결과

코드 커밋 `0f07717`의 [Windows CI #51](https://github.com/DongminKang0919/windows-desktop-notes/actions/runs/39788351101), 작업 `119857406602`이 전체 성공했다. 실제 로그에서 다음을 확인했다.

- Release 경고 0·오류 0, 합성 ZIP/설치 검증 **50개 PASS**(정상 2 + 거부 48), 기존 자동 **149개 PASS**.
- 중단 복구 **10건**(저장 경계 8 + 고정 복구 ID 2), 복구 창 수명 smoke의 잔존 참조 0·원본 불변·마지막 저장 재시도 성공.
- 실제 prototype ZIP 생성과 기존 패키지 검사 **6개 PASS**: 정상 파일, checksum 오류, 외부 체크섬 재계산 후 본문 변조, 경로 탈출, 다른 버전, artifacts 밖 출력 거부.
- 측정 JSONL과 UI 렌더 artifact 업로드 성공. 실제 설치 EXE 생성/실행·설치본 업데이트를 수행한 결과는 아니다. 기본 출력 불변 assertion을 포함한 최종 실행 상태는 [PR 검사](https://github.com/DongminKang0919/windows-desktop-notes/pull/17/checks)에 기록한다.

## 남은 확인

게시자 인증·서명·실제 설치본 두 버전 업데이트·제거, 여러 추출기와 특수 파일시스템의 동작·같은 계정의 경로 교체 경쟁, OS 로그아웃/재부팅/Explorer 재시작과 8/24시간 상주는 미완료다. 기존 메모·실행 중 사용자 앱·기존 빌드 출력은 사용하지 않았으며, 짧은 검사로 무취약점·무누수를 보장하지 않는다.
