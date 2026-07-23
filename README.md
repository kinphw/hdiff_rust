# Hdiff

폐쇄망에서 HWP/HWPX 두 버전의 변경 내용을 좌·우 원문 비교 화면으로 보여 주는
Windows 네이티브 도구입니다. 원본 파일은 수정하지 않습니다.

## 현재 구현 범위

- WinForms: 전/후 파일을 첨부 카드에 드래그하면 즉시 읽기 확인을 하고, 파일
  크기·확장자·추출 글자 수·문단 수·사용 파서를 표시
- 좌·우 문단 정렬 비교, 줄 번호 및 문자 단위 강조 (삭제는 빨강, 추가는 초록)
- 기본값으로 긴 줄 자동 줄바꿈. 두 패널을 최초 정확히 50:50으로 두고 더 좁은 쪽의
  공통 폭으로 줄을 나누어, 좌·우 표시 행 정렬을 유지. 필요하면 체크를 풀어 가로
  스크롤로 전환. 세로 이동은 하나의 공통 스크롤바가 두 원문을 같은 픽셀만큼 이동
- 각 비교창 오른쪽: 문서 전체의 줄 길이와 변경 위치를 압축한 미니맵. 미니맵을
  클릭하거나 끌면 해당 위치로 이동
- HWPX: ZIP/XML 직접 파싱 (한글 불필요)
- HWP5: OLE Compound File → `FileHeader`/`BodyText/Section*` → 압축 해제 →
  문단 text record 직접 파싱 (한글 불필요)
- DRM/DLP 안전 경계: 문서 본문은 UI가 아닌 자식 워커가 읽음
- COM 폴백: 직접 파서가 실패할 때만 한글 COM으로 평문을 읽음. 원본을 변환하거나
  임시 HWPX를 만들지 않고, 사용자가 열어 둔 Hwp.exe를 찾거나 종료하지 않음.

HWP5 직접 파서는 현재 문단 중심입니다. 표의 셀 격자·글상자·각주·서식 차이는
후속 단계이며, 현재 표 안의 텍스트는 문단으로만 보일 수 있습니다.

## 실행

```powershell
dotnet run --project src/Hdiff.UI
```

Windows 탐색기에서 [run-ui.cmd](/C:/projects/hdiff/run-ui.cmd:1)를 더블클릭해도 됩니다.
이 배치 파일은 항상 현재 소스를 `dotnet run`으로 빌드·실행합니다.

전/후 영역에 `.hwp` 또는 `.hwpx` 파일을 놓고 **비교**를 누릅니다.
기본값은 직접 파서 실패 시에만 COM 폴백을 허용합니다.

## 자동 검증 및 생성되는 테스트 문서

```powershell
# 합성 HWP5, HWPX, diff 로그 생성 및 직접 파서 검증
dotnet run --project tests/Hdiff.Tests

# 한글 COM이 설치된 PC에서는 실제 HWP 생성 후 직접 파서까지 검증
dotnet run --project tests/Hdiff.Tests -- --with-com
```

검증은 다음 산출물을 남깁니다.

- `artifacts/generated-fixtures/before-synthetic-hwp5.hwp`
- `artifacts/generated-fixtures/after-synthetic-hwp5.hwp`
- `artifacts/generated-fixtures/hancom-generated.hwp` (`--with-com` 시)
- `artifacts/generated-fixtures/before-after.diff.txt`

첫 두 파일은 HWP5 record-reader 회귀 테스트용 합성 fixture입니다. `hancom-generated.hwp`는
설치된 한글 COM이 실제 저장한 HWP5 파일이며, 이 파일을 다시 COM 없이 읽어 검증합니다.

## 배포

이 개발 PC에는 .NET 8 SDK만 있으므로 현재 프로젝트는 `net8.0`으로 빌드됩니다.
대상 폐쇄망의 .NET 9 SDK에서 반입 빌드를 할 때에는 `Directory.Build.props`의
`TargetFramework`을 `net9.0`으로, UI 프로젝트의 `net8.0-windows`를
`net9.0-windows`로 올리면 됩니다.

```powershell
pwsh -File publish.ps1 -SelfContained
```

반입용 ZIP은 [build.cmd](/C:/projects/hdiff/build.cmd:1)로 만듭니다. 기본 명령은
런타임을 함께 넣은 자체 포함(single-file) 패키지이며, 파일명은
`Directory.Build.props`의 `<Version>`에서 생성됩니다.

```cmd
build.cmd
REM publish\Hdiff-v0.2.5-win-x64-self-contained.zip

build.cmd fdd
REM publish\Hdiff-v0.2.5-win-x64-fdd.zip
REM .NET Desktop Runtime이 설치된 PC용의 작은 FDD ZIP
```

`publish\fdd\` 및 `publish\self-contained\`에는 압축 전 배포 파일이,
`publish\` 바로 아래에는 반입용 ZIP이 저장됩니다.

배포물의 공식 파일명은 `Hdiff.exe`입니다. 워커는 `Environment.ProcessPath`로
자기 자신을 다시 실행하므로, 앱과 워커는 항상 같은 실행 파일명으로 동작합니다.

## 설계상 금지한 동작

- 원본 HWP/HWPX의 변환·덮어쓰기·확장자 변경 복사
- 임시 복호화본 생성
- 사용자가 열어 둔 한글 인스턴스 attach/close/kill
- DRM/DLP 우회를 위한 별도 파일 변형
