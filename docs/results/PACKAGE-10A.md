# WM-010A Windows x64 ZIP 패키징 검증

- 날짜: 2026-10-13
- 앱 버전: 0.6.0 prototype 유지 / SDK 10.0.401 / Windows x64
- 상태: Review — 로컬 생성·내용 검증 완료, 압축 해제본 실행·원격 작업 검토 대기

## 구현

scripts/package.ps1에서 빌드·테스트·publish·버전 및 아키텍처 검사·manifest·ZIP·SHA256·최종 패키지 검증을 수행한다. 런타임 비포함 Windows x64 출력이며 .NET 10 Desktop Runtime x64가 필요하다. 출력은 프로젝트 artifacts 안의 실행별 GUID 폴더로 제한하고 링크된 출력 조상을 거부한다. 기존 결과를 삭제하거나 덮어쓰지 않는다.

verify-package.ps1은 추출 없이 manifest와 모든 파일의 크기·해시, 필수 파일, 안전한 상대 경로, 파일 수/크기 한도와 ZIP 체크섬을 검사한다. test-package.ps1은 정상 원본을 유지하고 artifacts 내 복사본으로 거부 경로를 확인한다. package.yml은 수동 실행·읽기 전용 저장소 권한으로 패키징·거부 검증 후 ZIP과 체크섬을 14일간 artifact로 보관하도록 구성했다. Release 게시나 배포 API를 호출하지 않는다.

## 실행 결과

```powershell
.\scripts\package.ps1 -ShellProbe
.\scripts\test-package.ps1 -ZipPath <생성된 ZIP 경로>
```

- 최종 Release 빌드: 경고 0, 오류 0
- 앱 검증: 53개 PASS, 종료 코드 0
- Windows x64 publish: 성공
- EXE·DLL 파일 버전: 0.6.0.0, 공통 설정 일치
- apphost PE machine: 0x8664 (Windows x64)
- ZIP 검증: PASS, manifest를 제외한 필수 파일 5개
- 패키지 검증 6개: 정상, ZIP 체크섬 불일치, ZIP 체크섬을 다시 계산한 동일 크기 내용 변조, ../ 경로, 잘못된 버전, artifacts 외 출력 거부 — 모두 PASS
- git diff --check: 통과

검증 ZIP: artifacts/package-272a76df5bf14e619b4daf7716fcd511/WindowsMemo-0.6.0-win-x64-framework-dependent.zip
- 크기: 118,489 bytes
- SHA256: 197ee3d6ccbd717d7abf4f7ec34caeb6d85b7587816532f52078ada011261657
- manifest: sourceCommit=a13ff7001c5410a134e0e902bde328f837b7590f, sourceDirty=true. 패키징 변경 작업 중 만든 로컬 검토 결과물이며 정식 릴리스가 아니다. 소스 상태를 숨기지 않는다.

## 검증 중 수정

어셈블리를 로드하는 GetAssemblyName 버전 조회는 앱 제어 정책 0x800711C7에 막혔다. 코드 실행이 필요 없는 파일 버전 정보 및 PE 헤더 검사로 바꿨다. 실제 패키지 앱 실행 성공으로 해석하지 않는다. 보안 설정이나 정책을 변경하지 않았다. Windows PowerShell 5.1에서는 UTF-8 BOM으로 한글 스크립트 읽기를 맞추고, ZIP 항목 경로를 /로 직접 생성하며 압축 어셈블리를 명시적으로 로드했다. 수정 후 전체 패키징과 거부 검증을 통과했다.

## 남은 검토

압축 해제본의 실제 실행·입력·저장·트레이·로그인, SDK 없는 Desktop Runtime 환경, 원격 수동 작업 및 artifact 다운로드는 미검증이다. 초기 앱 제어 정책 차단 기록이 있어 서명과 허용된 실행 환경 검토가 필요하다. ZIP의 해시는 무결성 확인용이며 게시자 인증이 아니다.

설치 프로그램·자동 업데이트·런타임 포함 배포·서명·릴리스 게시는 아직 구현하지 않았다. WM-010B에서 패키징 도구와 설치/데이터 경로 분리, 배포 저장소·게시자 신뢰 방식, 실제 설치본 두 버전 업데이트를 검증한다.
