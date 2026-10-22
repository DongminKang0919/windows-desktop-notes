# README 화면 자료

이미지는 실제 앱 WPF 컨트롤을 시험 데이터로 렌더한 PNG이며 바탕화면 실행 캡처는 아니다. notes.png는 0.9.5 Windows CI, pending-recovery.png는 0.9.7 로컬 회귀 검사의 렌더다. 사용자 메모·바탕화면·알림 영역은 캡처하지 않는다.

| 파일 | 생성 경로 | 내용 |
| --- | --- | --- |
| notes.png | NoteDesignTests.RenderPreviews, papers-100.png | 100% 배율의 세 종이색·체크리스트 |
| pending-recovery.png | PendingRecoveryUiTests, pending-recovery-150.png | 150% 배율의 복구 화면·다음 항목·처음부터 확인 |

후보 ID·시각은 실행마다 달라진다. 실제 모니터 배율 전환·입력 검증을 대신하지 않는다. PNG 원본을 그대로 복사하며 흰 배경 합성은 렌더 코드에서 수행한다.

프로젝트 루트에서 빌드 후 실행한다.

```powershell
dotnet tests/WindowsMemo.Tests/bin/Release/net10.0-windows/WindowsMemo.Tests.dll
dotnet tests/WindowsMemo.Tests/bin/Release/net10.0-windows/WindowsMemo.Tests.dll --render-ui-only
```

출력 폴더의 ui-previews에서 파일을 가져온다. Windows CI도 sample-ui-renders artifact를 만들어 14일 보관한다. 출처는 [복구 검증 기록](../results/PENDING-04D.md)과 [중복 복구 방지 검증](../results/IDENTITY-04F.md)에 기록한다. README에 커밋한 이미지는 artifact 만료 후에도 저장소에 남는다.
