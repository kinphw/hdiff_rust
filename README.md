# Hdiff

폐쇄망에서 HWP/HWPX 두 버전의 변경 내용을 좌·우 원문 비교 화면으로 보여 주는
Windows 네이티브 도구입니다. 원본 파일은 수정하지 않습니다.

## 현재 구현 범위

- WinForms: 전/후 파일을 첨부 카드에 드래그하면 즉시 읽기 확인을 하고, 파일
  크기·확장자·추출 글자 수·문단 수·사용 파서를 표시
- 상단 **글자 크기**: 작게(12px)·보통(14px, 기본)·크게(16px). 선택값은
  `%LocalAppData%\Hdiff\settings.json`에 저장되어 새 배포본으로 바꿔도 유지
- 기본 활성화된 **빈 개행(엔터) 무시**: 내용 없는 문단을 문단 대응과 변경 요약에서
  제외. 체크를 풀면 원래 빈 문단도 비교 행으로 표시
- 좌·우 문단 정렬 비교, 줄 번호 및 문자 단위 강조 (삭제는 빨강, 추가는 초록)
- 기본 활성화된 Google Diff Match Patch semantic cleanup: 수정으로 대응된 문단 안의
  강조 범위를 읽기 좋게 정돈. 체크를 풀면 기존 DiffPlex 문자 강조와 직접 비교 가능
- 기본값으로 긴 줄 자동 줄바꿈. 비교 결과를 행별 데이터로 만든 뒤 하나의 가상
  캔버스에 좌·우를 함께 그리므로, 삽입·삭제와 줄바꿈 뒤에도 두 쪽이 같은 표시 행과
  같은 y좌표를 공유한다. 필요하면 체크를 풀어 가로 스크롤로 전환
- 각 비교창 오른쪽: 문서 전체의 줄 길이와 변경 위치를 압축한 미니맵. 수천 문단에서도
  변경 위치가 보이도록 2px 이상으로 그리는 고대비 변경 신호 막대가 있으며, 미니맵을
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

반입용 ZIP은 [build.cmd](/C:/projects/hdiff/build.cmd:1)로 만듭니다. Hdiff의 공식
배포 형식은 .NET Desktop Runtime을 쓰는 FDD ZIP 하나이며, 파일명은
`Directory.Build.props`의 `<Version>`에서 생성됩니다.

```cmd
build.cmd
REM publish\Hdiff-v0.2.9-win-x64-fdd.zip
REM .NET Desktop Runtime이 설치된 PC용의 작은 FDD ZIP
```

`publish\fdd\`에는 압축 전 배포 파일이, `publish\` 바로 아래에는 반입용 ZIP이
저장됩니다. `pwsh -File publish.ps1`도 동일한 FDD ZIP을 생성합니다.

배포물의 공식 파일명은 `Hdiff.exe`입니다. 워커는 `Environment.ProcessPath`로
자기 자신을 다시 실행하므로, 앱과 워커는 항상 같은 실행 파일명으로 동작합니다.

## 설계상 금지한 동작

- 원본 HWP/HWPX의 변환·덮어쓰기·확장자 변경 복사
- 임시 복호화본 생성
- 사용자가 열어 둔 한글 인스턴스 attach/close/kill
- DRM/DLP 우회를 위한 별도 파일 변형
