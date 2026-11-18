# README 화면 자료

0.9.13의 `list-feedback.png`는 로컬 실행형 검사 `NoteListTests`가 실제 파일 잠금을 유발해 만든 `notes-feedback-150.png` 원본이다. 목록·보관함·휴지통 실패 렌더를 직접 육안 확인했으며 100/150/200% 원본은 `ui-previews`와 CI artifact에 생성한다. 이 버전의 실행에는 정책 차단이 없었고 보안 설정을 바꾸지 않았다. 아래 0.9.10의 다운로드/육안 미검증 기록은 당시 이력이다.

0.9.10 목록·보관함·휴지통의 100/150/200% 렌더는 CI `sample-ui-renders`의 `notes-list-*.png`, `archive-list-*.png`, `trash-list-*.png`로 추가된다. 로컬 실행·다운로드 제한 때문에 저장소 PNG로 복사하거나 직접 육안 확인하지 못했으며, [목록 검증 기록](../results/LIST-07C.md)에 artifact 출처·보관 기간·확인 범위를 구분했다.

이미지는 실제 앱 WPF 컨트롤을 시험 데이터로 렌더한 PNG이며 바탕화면 실행 캡처는 아니다. notes.png는 0.9.5 Windows CI, 복구·재시도 PNG는 0.9.8 로컬 회귀 검사의 렌더다. 사용자 메모·바탕화면·알림 영역은 캡처하지 않는다.

| 파일 | 생성 경로 | 내용 |
| --- | --- | --- |
| notes.png | NoteDesignTests.RenderPreviews, papers-100.png | 100% 배율의 세 종이색·체크리스트 |
| pending-recovery.png | PendingRecoveryUiTests, pending-recovery-150.png | 150% 배율의 복구 화면·다음 항목·처음부터 확인 |
| recovery-retry.png | PendingRecoveryUiTests, recovery-retry-150.png | 실제 저장 실패를 주입한 뒤 미리보기 유지·재시도 버튼 |
| list-feedback.png | NoteListTests, notes-feedback-150.png | 0.9.13 파일 잠금 실패 안내·목록 선택·재시도 버튼, 150% |

후보 ID·시각은 실행마다 달라진다. 실제 모니터 배율 전환·입력 검증을 대신하지 않는다. PNG 원본을 그대로 복사하며 흰 배경 합성은 렌더 코드에서 수행한다.

프로젝트 루트에서 빌드 후 실행한다.

```powershell
dotnet tests/WindowsMemo.Tests/bin/Release/net10.0-windows/WindowsMemo.Tests.dll
dotnet tests/WindowsMemo.Tests/bin/Release/net10.0-windows/WindowsMemo.Tests.dll --render-ui-only
```

출력 폴더의 ui-previews에서 파일을 가져온다. Windows CI도 sample-ui-renders artifact를 만들어 14일 보관한다. 출처는 [복구 검증 기록](../results/PENDING-04D.md)과 [실패 안내·재시도 검증](../results/FEEDBACK-04G.md)에 기록한다. README에 커밋한 이미지는 artifact 만료 후에도 저장소에 남는다.
