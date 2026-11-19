# WM-018: 실사용 화면 정리

2026-11-19 · 0.11.0 prototype · 저장 스키마 5 유지.

## 변경

- 메모·목록·휴지통·보관함·설정·제목 입력·미완료 복구에 공통 색상과 컨트롤 스타일을 적용했다. 밝은 중립 배경, 짙은 초록 강조색, 부드러운 세 종이색을 사용한다.
- 목록은 제목·안내·개수·메모 카드·작업 버튼 순으로 정리했다. 선택한 카드는 배경과 테두리로 구분하고, 새 메모·복원·설정 저장은 주요 버튼, 삭제는 별도의 붉은 버튼으로 구분한다.
- 헤더는 24 DIP 제목과 작은 탐색 버튼으로 구분했다. 목록 기본 창을 560×540으로 넓히고 기존 최소 창 크기와 스크롤을 유지했다.
- 버튼·입력창·선택 상자의 모서리, 선, 간격을 통일했다. 버튼의 hover/pressed/disabled와 키보드 포커스, 목록의 선택/포커스 상태를 적용했다. 설정은 흰 카드 안에 항목을 모으고 작은 창에서는 스크롤한다.
- 메모는 제목 크기와 종이색을 정리하고 새 메모·제목 변경에 벡터 아이콘을 사용한다. 도구는 평소에도 낮은 불투명도로 보이며 마우스/키보드 포커스가 들어오면 선명해진다. 저장 실패 안내·재시도와 접힘 크기 규약은 유지한다.
- 트레이 툴팁에서 prototype 접미사를 제거했다. 개발/릴리스 검증 상태 자체는 문서에서 계속 구분한다.

## 검증

- SDK 10.0.401, Release 빌드 경고·오류 0.
- `artifacts/ui-polish-validation`의 회귀 검사 **165개 PASS**. 휴지통 복원, 저장 실패 후 재시도, 창 폐기, 설정·제목 보존을 포함한다. 설정 UI 검사는 너비와 함께 선택한 종이색의 저장도 확인한다.
- 목록/휴지통/보관함의 100/150/200% 렌더, 최소 크기와 선택 상태 렌더를 생성했다. 메모 3종·도구·접힘과 설정/제목 화면도 렌더했다.
- 메모 3종, 메모 도구, 목록·휴지통, 선택된 목록, 최소 크기 목록, 실제 파일 잠금 실패 안내, 설정·제목 PNG를 육안 확인했다. 첫 설정 렌더에서 선택값이 비어 보여 문자열 항목과 전용 선택 상자 템플릿으로 수정하고 ‘버터’ 표시를 다시 확인했다.
- 이 자료는 합성 데이터의 실제 WPF 컨트롤 렌더다. 실제 Windows 창 테두리·IME·Win+D·운영체제 배율 전환·드롭다운 키보드 조작을 수동으로 검증한 결과는 아니다.
- 앱 사용자 메모를 변경하거나 Windows 설정을 바꾸지 않았다. 실행 중인 구버전은 그대로 두고 기본 실행 경로의 새 버전을 빌드한다. 트레이에서 저장 후 종료하고 루트 `메모 실행.cmd`로 다시 실행하면 적용된다.

## 재현

```powershell
dotnet build WindowsMemo.slnx -c Release -p:RestoreLockedMode=true --artifacts-path artifacts/ui-polish-validation
dotnet artifacts/ui-polish-validation/bin/WindowsMemo.Tests/release/WindowsMemo.Tests.dll --render-ui
dotnet artifacts/ui-polish-validation/bin/WindowsMemo.Tests/release/WindowsMemo.Tests.dll --settings-render
```

로그는 `artifacts/ui-polish-validation/regression.log`, 렌더는 테스트 실행 파일 폴더의 `ui-previews`와 `settings-title-renders`에 남는다.
