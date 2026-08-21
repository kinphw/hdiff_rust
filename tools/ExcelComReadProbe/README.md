# Excel COM 읽기 PoC

이 프로젝트는 설치된 Excel의 정상적인 COM 자동화 경로가 현재 PC의 DRM 문서를
읽기 전용으로 열고 셀 값·수식을 반환하는지만 확인합니다. 실행 파일 이름 위장,
DRM 파일 복사·변환, `Save`/`SaveAs`, 임시 복호화 파일 생성은 하지 않습니다.

Hdiff 본체와 솔루션에는 연결하지 않은 독립 프로젝트입니다. Excel/DRM 오류나 정지가
본 앱에 영향을 주지 않도록 실제 COM 호출은 별도 워커 프로세스에서 실행됩니다.

## 실행

배포 ZIP은 .NET 8 Runtime이 설치된 64비트 Windows용 단일 FDD 실행 파일입니다.
명령 프롬프트나 PowerShell에서 다음과 같이 실행합니다.

```powershell
.\Hdiff.ExcelComReadProbe.exe "C:\문서\시험.xlsx"
```

소스 저장소에서 직접 실행할 때는 저장소 루트로 이동한 뒤 다음 명령을 사용합니다.

```powershell
dotnet run --project tools/ExcelComReadProbe -- "C:\문서\시험.xlsx"
```

로그 경로를 직접 정할 수도 있습니다.

```powershell
dotnet run --project tools/ExcelComReadProbe -- "C:\문서\시험.xlsx" `
  --log "C:\Temp\excel-com-probe.jsonl" `
  --timeout-seconds 90
```

기본 로그는 다음 폴더에 JSON Lines 형식으로 저장되며 실행 마지막에 정확한 경로가
출력됩니다.

```text
%LocalAppData%\Hdiff\ExcelComReadProbe\Logs
```

로그에는 Excel 버전과 PID, 파일 메타데이터, 열기 소요시간, 통합문서의 읽기 전용
상태, 시트별 `UsedRange`, 읽은 셀 수, 비어 있지 않은 셀·수식 수, 값 형식별 개수,
내용 확인용 SHA-256, 예외 형식·HRESULT·스택을 기록합니다. 기본값에서는 셀 내용
자체를 기록하지 않습니다.

실제 값이 반환되는지도 눈으로 확인해야 할 때만 아래 옵션을 사용합니다.

```powershell
dotnet run --project tools/ExcelComReadProbe -- "C:\문서\시험.xlsx" `
  --include-values --sample-limit 20
```

`--include-values` 로그에는 최대 20개의 셀 값과 수식 미리보기가 들어갑니다. 이 로그를
외부에 전달하기 전 반드시 민감정보를 확인하십시오. Codex에 분석을 요청할 때는 가능하면
먼저 기본 모드의 `.jsonl` 로그를 전달하는 것이 좋습니다.

## 결과 판정

- 종료 코드 `0`, `workbook_opened`, `worksheet_read`, `workbook_read_completed`가 있으면
  현재 DRM 정책에서 Excel COM을 통한 읽기가 가능하다고 볼 수 있습니다.
- 종료 코드 `20`은 주로 통합문서 열기 단계의 실패입니다. `probe_failed`의 `hresult`와
  메시지로 DRM 차단, 암호, 보호된 보기 또는 Excel 오류를 구분합니다.
- 종료 코드 `124`는 제한 시간 초과입니다. PoC가 생성한 Excel PID만 확인하여 정리하고
  실행 전에 열려 있던 사용자 Excel 프로세스는 종료하지 않습니다.
- `excel_instance_not_isolated`는 새 COM 객체가 기존 Excel 프로세스와 분리되지 않아
  사용자 문서 보호를 위해 파일을 열지 않고 중단한 경우입니다.

시트의 `UsedRange`가 기본 최대치인 1,000,000셀을 넘으면 모서리와 중앙 셀만 읽습니다.
필요하면 `--max-cells 5000000`처럼 늘릴 수 있습니다(허용 상한 20,000,000셀).
