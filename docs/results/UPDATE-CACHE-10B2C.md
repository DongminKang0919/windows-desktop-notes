# WM-010B2C: 로컬 시험 업데이트 캐시 보존

2026-10-22. PR #15 병합 main `b6bc6ef` / 0.9.13과 깨끗한 작업 트리를 확인한 뒤 별도 브랜치에서 0.9.14로 수정했다.

## 문제와 변경

`LocalTestUpdateSource.DownloadReleaseEntry`는 해시 검증 전에 `File.Create(localFile)`을 호출했다. 취소·검증 실패 시 기존 캐시를 비우거나 불완전한 신규 파일을 남겼다. 새 회귀 검사를 기존 코드에 실행하여 보존 단언 실패(exit 1)를 재현한 뒤 수정했다. 초기 테스트 예외 필터를 보정한 후 실제 보존 단언 실패를 확인했으며, 최종 검사는 Windows 잠금 시 반환된 접근 거부도 예상 I/O 실패로 처리한다.

1. 시작 전 취소와 패키지·대상 경로를 검증한다. 캐시는 로컬 절대 파일 경로만 받고 피드 원본 폴더와 하위 경로·UNC·대체 스트림·재분석 지점을 거부한다.
2. 대상과 같은 폴더의 `.windowsmemo-download-GUID.tmp`를 CreateNew·배타적 쓰기로 연다. 81,920바이트 버퍼와 증분 SHA256으로 복사하며 크기 상한과 취소를 확인한다.
3. 크기·해시 검증과 FlushAsync를 마치고 임시 핸들을 닫은 뒤 대상·임시 경로를 재확인한다. 교체 직전 취소를 확인하고 File.Move(overwrite: true)로 완성된 파일을 이동한다. 성공 100% 알림은 그 뒤에만 보낸다.
4. 실패·취소 시 이 호출이 생성한 임시 파일만 정리한다. 기존 캐시·피드·다른 호출의 임시 파일을 삭제하지 않는다. 경로가 링크로 바뀌어 안전하게 정리할 수 없으면 오류를 반환하고 따라 삭제하지 않는다.

[Microsoft File.Move 문서](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.move?view=net-10.0)의 overwrite 동작과 I/O/권한 실패를 대조했다. 같은 폴더에 준비하여 다른 볼륨 간 복사로 확정하지 않는다. 전원 차단의 내구성이나 모든 파일시스템의 원자성을 보장하는 변경은 아니다.

기존 Velopack의 ZIP/제품/버전 검증과 적용 직전 재검증을 유지한다. 이번 변경의 해시는 전송 무결성이며 게시자 인증을 대신하지 않는다. 메모 저장소·DPAPI CurrentUser·스키마 4·기존 호환성·고정 복구 ID·원본/백업/미완료 파일·삭제 확인·바탕화면 부착은 변경하지 않았다.

## 회귀 검사 5개

- 신규/기존 대상 × 해시 불일치·크기 불일치·사전 취소: 기존 캐시의 파일 집합·해시·수정 시각 불변, 신규 대상 부재, 완료 알림 0.
- 신규/기존 대상 × 복사 중·검증 후 교체 직전 취소: 기존 캐시 불변·임시 파일 제거·소스 배타적 재열기 성공. 인스턴스별 내부 체크포인트만 사용하며 제품 호출은 콜백을 설치하지 않는다.
- 성공: 모든 복사/교체 전 경계까지 기존 파일 유지 또는 대상 부재. 교체 후에만 100% 통지하며 정확한 내용·임시 파일 0·원본 불변 확인.
- 교체 대상 잠금: 검증 후 이동 실패 시 기존 파일 보존·임시 파일 정리·완료 통지 0, 잠금 해제 후 재시도 성공.
- 원본 파일·피드·하위/신규 경로·상대/UNC/ADS 대상 거부, 피드와 기존 캐시 보존.

테스트 데이터는 빌드 출력의 `update-download-tests/GUID` 및 기존 합성 폴더에만 만든다. 실제 사용자 메모·설치본·실행 중 앱은 사용하지 않는다. 재분석 지점 보호는 기존 경로 검사 호출을 재사용하며 이번 검사에서 실제 심볼릭 링크/접합점 교체 경쟁을 실행하지 않았다.

## 로컬 검증

```powershell
dotnet build WindowsMemo.slnx --configuration Release --nologo --artifacts-path artifacts/update-download-validation -p:RestoreLockedMode=true
dotnet artifacts/update-download-validation/bin/WindowsMemo.Tests/release/WindowsMemo.Tests.dll --shell-probe
dotnet artifacts/update-download-validation/bin/WindowsMemo.Tests/release/WindowsMemo.Tests.dll --crash-probe
dotnet artifacts/update-download-validation/bin/WindowsMemo.Tests/release/WindowsMemo.Tests.dll --recovery-probe --cycles 5 --idle-seconds 1
```

- Release 경고 0·오류 0. **150개 PASS**(기본 149 + 실제 비표시 호스트 1). 보안 정책·화이트리스트·서명 변경이나 우회 실행 없이 성공했다.
- 중단 복구 **10건 PASS**(저장 경계 8 + 복구 ID 2), 중복 정리 통과. `crash-probe/3f3ebc92fcd243a99988a77e259c5763`.
- 기존 복구 창 수명 smoke: 워밍업 10회 + 측정 5회, 유휴 1초. 잔존 참조 0·원본 불변·마지막 저장 재시도 성공, Handles 498 / GDI 10 / USER 15로 기준값 유지. `recovery-probe/c22435988bc540a08d341ddc8a041e77/metrics.jsonl`. 다운로드의 장시간 성능·무누수 판정으로 사용하지 않는다.

## Windows CI 결과

코드 커밋 `7cbe669`의 [Windows CI #48](https://github.com/DongminKang0919/windows-desktop-notes/actions/runs/39783471170), 작업 `119840398787` 전체 성공. 실제 로그에서 Release 경고 0·오류 0, 자동 **149개 PASS**(캐시 회귀 5개 포함), 중단 복구 **10건**, 수명 smoke의 잔존 참조 0·원본 불변·마지막 재시도 성공과 측정/렌더 artifact 업로드를 확인했다. Markdown 로컬 링크와 `git diff --check`도 통과했다.

## 남은 확인

실제 설치본 두 버전의 업데이트·제거·실패 복구, 게시자 인증·서명, 같은 계정의 경로 교체 경쟁, 느린 디스크·전원/프로세스 중단·남은 다운로드 임시 파일의 정리는 미검증/미구현이다. 강제 중단 뒤 임시 파일을 자동 삭제하는 기능을 추가하지 않았다. OS 로그아웃·재부팅·Explorer 재시작은 실행하지 않았다. 8/24시간 전체 앱 상주는 미완료이며 짧은 검사로 무누수·무취약점을 보장하지 않는다.
