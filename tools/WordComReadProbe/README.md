# Word COM 읽기 PoC

설치된 Microsoft Word의 정상적인 COM 자동화 경로가 DRM `.docx`를 읽기 전용으로
열고 본문과 표를 반환하는지 확인하는 독립 PoC입니다. Hdiff 본체에는 아직 연결하지
않았습니다.

PoC는 새 Word 프로세스를 만들고 그 PID가 기존 사용자 Word와 분리됐는지 확인합니다.
매크로와 링크 갱신을 끈 뒤 `ReadOnly=true`로 열며 `Save`, `SaveAs`, 복사, 변환,
임시 복호화 파일 생성을 하지 않습니다. 종료 시 PoC가 만든 Word 프로세스만 정리합니다.

## 실행

대상 PC에는 64비트 Windows, .NET 8 Runtime, Microsoft Word가 필요합니다.

```powershell
.\Hdiff.WordComReadProbe.exe "C:\문서\시험.docx"
```

기본 로그에는 문서 내용 자체를 넣지 않습니다. 문단과 표 행이 실제로 읽히는지
확인해야 할 때만 아래 옵션을 사용하십시오.

```powershell
.\Hdiff.WordComReadProbe.exe "C:\문서\시험.docx" --include-text
```

`--include-text`에서는 기본 30개 비교 행만 기록합니다. 표는 셀 내부 개행을 공백으로
합치고, 같은 행의 셀을 ` | `로 연결하여 한 줄로 기록합니다.

기본 JSON Lines 로그 경로:

```text
%LocalAppData%\Hdiff\WordComReadProbe\Logs
```

## 결과 판정

- 종료 코드 `0`과 `document_opened`, `table_read`, `document_read_completed`가 있으면
  현재 DRM 정책에서 Word COM 읽기와 표 행 추출이 가능합니다.
- `document_read_completed`의 `bodyParagraphsRead`, `topLevelTablesRead`,
  `tableRowsRead`, `orderedComparisonLines`로 추출 규모를 확인합니다.
- `table_read`의 `rowsRead`가 실제 표 행 수와 맞는지 확인합니다.
- 종료 코드 `20`은 문서 열기 실패입니다. `probe_failed`의 메시지와 HRESULT를
  전달하면 DRM 차단, 암호, 보호된 보기 등의 원인을 검토할 수 있습니다.
- 종료 코드 `124`는 제한 시간 초과입니다.

민감한 문서에서 `--include-text`를 사용한 로그는 외부 전달 전에 본문 내용을 반드시
확인하십시오. 파일 반출이 어렵다면 콘솔과 로그를 사진으로 촬영해도 됩니다.
