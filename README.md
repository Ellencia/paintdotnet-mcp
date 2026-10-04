# Paint.NET MCP

Paint.NET을 MCP 클라이언트에서 제어하는 Windows용 서버와 효과 플러그인입니다. 현재 레이어에 그림을 그리거나 이미지를 붙이고, 영역 추출·배경 제거·파일 저장 작업을 수행할 수 있습니다.

그리기 명령은 Paint.NET에서 MCP Bridge 효과를 자동 실행해 반영합니다. 서버를 연결한 뒤에는 매번 메뉴나 Ctrl+F를 누를 필요가 없습니다.

[설치](#설치) · [클라이언트 연결](#클라이언트-연결) · [사용법](#사용법) · [도구 목록](#도구-목록) · [검증](#검증)

## 요구사항

| 항목 | 요구사항 |
| --- | --- |
| 운영체제 | Windows |
| Paint.NET | 5.x — 실제 앱 검증 버전: 5.1.12 |
| 빌드 환경 | .NET 9 SDK |
| 기본 설치 경로 | `C:\Program Files\paint.net` |
| 선택 의존성 | AI 배경 제거: rembg CLI / OCR: Tesseract CLI |

rembg와 Tesseract는 해당 기능을 사용할 때만 필요합니다. 한국어 OCR에는 Tesseract의 한국어 언어 데이터도 필요합니다.

## 설치

저장소 루트에서 실행합니다. 배포 전에 작업을 저장하고 Paint.NET을 종료하세요. 기본 설치 경로의 Effects 폴더에 쓰려면 관리자 PowerShell이 필요합니다.

### 빌드 및 배포

```powershell
dotnet build -c Release
dotnet build src\PaintDotNetMcp.Bridge\PaintDotNetMcp.Bridge.csproj -c Release -t:Deploy
```

Paint.NET이 다른 경로에 설치되어 있다면 두 명령에 같은 경로를 지정합니다.

```powershell
dotnet build -c Release -p:PaintDotNetDir="D:\Apps\paint.net"
dotnet build src\PaintDotNetMcp.Bridge\PaintDotNetMcp.Bridge.csproj -c Release -t:Deploy -p:PaintDotNetDir="D:\Apps\paint.net"
```

`Deploy` 대상은 Bridge 프로젝트에 정의되어 있습니다. 솔루션 전체에 `-t:Deploy`를 지정하지 마세요.

### 배포 스크립트

빌드와 복사를 한 번에 수행하려면 [deploy.ps1](deploy.ps1)을 사용할 수 있습니다.

```powershell
.\deploy.ps1
# 다른 설치 경로
.\deploy.ps1 -PaintDotNetDir 'D:\Apps\paint.net'
# 기존 빌드 결과만 배포
.\deploy.ps1 -SkipBuild
```

스크립트는 파일 잠금을 해제하기 위해 실행 중인 `PaintDotNetMcp.Server` 프로세스를 종료합니다. Paint.NET은 자동 종료하지 않습니다.

### 수동 설치

`src\PaintDotNetMcp.Bridge\bin\Release\net9.0-windows\`의 다음 파일을 Paint.NET의 `Effects` 폴더로 복사합니다.

- `PaintDotNetMcp.Bridge.dll`
- `PaintDotNetMcp.Contracts.dll`
- `SkiaSharp.dll`
- `libSkiaSharp.dll`
- `System.Drawing.Common.dll`

설치 또는 업데이트 후 Paint.NET과 연결된 MCP 클라이언트를 다시 시작하세요.

## 클라이언트 연결

서버는 stdio 방식으로 통신합니다. MCP 클라이언트가 다음 실행 파일을 시작하도록 설정합니다.

```text
<저장소 경로>\src\PaintDotNetMcp.Server\bin\Release\net9.0\PaintDotNetMcp.Server.exe
```

아래는 `mcpServers` 형식의 설정 예시입니다. 경로는 실제 저장소 위치로 바꾸세요. 클라이언트에 따라 설정 파일 위치와 형식은 다를 수 있습니다.

```json
{
  "mcpServers": {
    "paintdotnet": {
      "command": "C:\\Projects\\paintdotnet-mcp\\src\\PaintDotNetMcp.Server\\bin\\Release\\net9.0\\PaintDotNetMcp.Server.exe"
    }
  }
}
```

Claude Desktop에서는 `%APPDATA%\Claude\claude_desktop_config.json`에 설정합니다. 프로젝트별 설정을 사용하는 클라이언트에서는 서버가 현재 작업 폴더에 등록되어 있는지도 확인하세요.

## 사용법

1. Paint.NET에서 이미지 또는 새 캔버스를 엽니다.
2. **Effects > Tools > MCP Bridge**를 한 번 실행합니다. 백그라운드 연결과 초기 스냅샷이 준비됩니다.
3. MCP 클라이언트에서 `ping`으로 버전과 연결 상태를 확인합니다.
4. 그리기 도구를 호출합니다. 기본 설정에서는 MCP Bridge가 자동으로 실행됩니다.
5. `wait_for_idle`로 완료를 확인한 뒤 이미지를 읽거나 저장합니다.

여러 작업을 한 번에 적용하려면 다음 순서를 사용합니다.

```text
set_auto_commit(enabled=false)
fill(r=230, g=40, b=15)
draw_rectangle(x=100, y=100, width=100, height=100, r=10, g=100, b=240, fill=true)
commit()
wait_for_idle(timeoutMs=5000)
save_png(path="C:\out\canvas.png")
set_auto_commit(enabled=true)
```

위 예시는 도구 호출 순서를 나타냅니다. PowerShell 명령이 아닙니다. 좌표와 크기는 픽셀 단위이며 그리기는 활성 레이어에 적용됩니다.

### 완료 확인

`auto_triggered=true`는 실행 요청을 예약했다는 뜻입니다. 렌더링 완료를 뜻하지 않습니다.

`wait_for_idle`은 호출 시점까지 요청된 그리기의 모든 타일이 복사되고 읽기·저장용 스냅샷이 갱신될 때 성공합니다. `timeoutMs`의 기본값은 5000이며 허용 범위는 0–60000입니다. 이 도구 자체는 효과 실행을 요청하지 않습니다.

스냅샷 읽기·저장·이미지 처리 도구도 대기 중인 그리기가 있으면 최대 5초 기다립니다. 시간 초과나 렌더링 오류가 발생하면 이전 스냅샷으로 작업을 진행하지 않습니다.

진행 상태는 `ping`의 `PendingOpCount`, `QueuedRevision`, `CompletedRevision`, `RenderError`에서 확인할 수 있습니다. 완료 판정은 Paint.NET의 최종 효과 수락이나 Undo 이력 반영까지 관찰하지 않습니다.

## 도구 목록

### 연결 및 실행

| 도구 | 기능 |
| --- | --- |
| `ping` | 버전, 캔버스 크기, 대기 작업, 완료 리비전, 오류 조회 |
| `commit` | 자동 실행 설정과 관계없이 MCP Bridge 실행 요청 |
| `wait_for_idle` | 그리기와 스냅샷 갱신 완료 대기 |
| `set_auto_commit` | 그리기 후 자동 실행 켜기·끄기 |
| `diagnose_services` | 내부 서비스 연결 진단 |

### 그리기

| 도구 | 기능 |
| --- | --- |
| `fill` | 전체 또는 지정 영역 단색 채우기 |
| `draw_rectangle`, `draw_ellipse`, `draw_polygon` | 도형의 외곽선 또는 내부 그리기 |
| `draw_line` | 선 그리기 |
| `draw_text` | 시스템 폰트로 텍스트 그리기 |
| `flood_fill` | 지정 픽셀에서 허용 오차에 따라 채우기 |
| `gradient_fill` | 선형 또는 방사형 그라디언트 |
| `paste_image` | base64 PNG를 지정 좌표에 합성 또는 덮어쓰기 |

### 이미지 읽기 및 처리

| 도구 | 기능 |
| --- | --- |
| `get_canvas_png` | 스냅샷 전체 또는 영역을 base64 이미지로 반환 |
| `save_png` | 스냅샷 전체 또는 영역을 파일로 저장 |
| `extract_region` | 지정 영역 추출 및 선택적 저장 |
| `remove_background` | 색상 기반 또는 AI 배경 제거, 선택적 레이어 반영 |
| `detect_objects` | 균일한 배경의 객체 경계 검출 |
| `extract_objects` | 검출된 객체를 개별 파일로 저장 |
| `ocr_region` | Tesseract로 지정 영역의 텍스트 인식 |

이름이 `*_png`인 읽기·저장 도구도 `format` 옵션으로 PNG, WebP, JPEG를 지원합니다. `format=auto`이면 저장 경로의 확장자에서 형식을 추론하며, 경로가 없으면 PNG를 사용합니다. `quality`는 WebP·JPEG에 적용됩니다.

`savePath`가 있는 추출·배경 제거 요청은 기본적으로 base64를 반환하지 않습니다. 이미지가 필요한 경우 `includeBase64=true`를 지정하세요. 배경 제거 결과를 레이어에 붙일 때는 무손실 PNG를 사용합니다.

### 레이어, 문서, 효과 및 선택

| 도구 | 기능 |
| --- | --- |
| `list_layers` | 레이어 목록 조회 |
| `add_layer`, `delete_layer`, `select_layer` | 레이어 추가·삭제·활성화 |
| `save_pdn` | 문서를 Paint.NET 형식으로 저장 |
| `list_effects`, `apply_effect` | 효과 목록 조회 및 실행 요청 |
| `set_selection_rect`, `set_selection_polygon`, `clear_selection` | 선택 영역 설정 및 해제 |

레이어·문서·효과 실행과 기본 선택 연동은 Paint.NET 내부 API에 의존합니다. 다각형 선택은 브리지의 소프트웨어 마스크를 사용하며 Paint.NET UI에는 표시되지 않습니다. 사각형 선택도 내부 API 호출이 불가능하면 소프트웨어 마스크로 대체됩니다.

## 작업 예시

### 배경 제거

```text
remove_background(
  x=0, y=0, width=800, height=600,
  method="auto_corners", tolerance=40, feather=true,
  savePath="C:\out\cutout.webp", applyToLayer=true
)
wait_for_idle(timeoutMs=5000)
```

`color_key`는 지정 색상을, `auto_corners`는 영역 모서리에서 추정한 색상을 기준으로 배경을 제거합니다. `method=ai`는 별도로 설치한 rembg CLI를 사용합니다.

### 객체별 이미지 추출

```text
extract_objects(
  savePathTemplate="C:\out\icon_{i:000}.webp",
  tolerance=40, minSize=40, maxSize=200,
  padding=4, groupGap=8, maxAspectRatio=3.0,
  format="webp", quality=90
)
```

`padding`은 객체 주변 여백, `groupGap`은 분리된 조각을 합치는 거리입니다. 추출 전에 경계를 검토하려면 `detect_objects`를 먼저 호출하세요.

## 구조

```text
MCP 클라이언트
  └─ stdio → PaintDotNetMcp.Server
                └─ Named Pipe → Paint.NET / PaintDotNetMcp.Bridge
```

| 프로젝트 | 역할 |
| --- | --- |
| [Server](src/PaintDotNetMcp.Server) | MCP 도구 제공 및 요청 중계 |
| [Bridge](src/PaintDotNetMcp.Bridge) | Paint.NET 효과 실행, 렌더링 및 이미지 처리 |
| [Contracts](src/PaintDotNetMcp.Contracts) | 프로세스 간 공용 메시지 타입 |

Bridge의 파이프 서버는 최초 효과 실행 후 Paint.NET 프로세스가 종료될 때까지 유지됩니다. 기본 파이프 이름은 `PaintDotNetMcp.Bridge.v1`입니다.

## 검증

0.5.16은 Paint.NET 5.1.12의 1400×1050 캔버스에서 다음을 확인했습니다.

- Ctrl+F 없이 자동으로 전체 채우기와 우측 하단 사각형 반영
- 자동 실행을 끈 상태에서 명시적 `commit`으로 배치 적용
- `wait_for_idle` 성공, 대기 작업 0, 렌더링 오류 없음
- 저장 PNG를 다시 열어 전체 색상 픽셀 수와 좌표 검증

회귀 검증은 병렬 타일 렌더링, 호스트의 재사용 ROI 배열, 취소 후 재시도, 선택 영역, MCP stdio·파이프 연결, 저장 이미지, 오류 처리와 UI 실행 예약을 포함한 9개 항목입니다. 레이어·문서·OCR·AI 배경 제거 등 전체 도구의 실제 앱 동작을 모두 검증한 결과는 아닙니다.

Paint.NET과 .NET 9 SDK가 설치된 Windows에서 실행합니다. 테스트는 별도 파이프를 사용합니다. 설치된 Paint.NET DLL과 시스템 런타임의 사전 컴파일 코드 차이를 피하기 위해 ReadyToRun을 끕니다.

```powershell
$env:COMPlus_ReadyToRun = '0'
try {
    dotnet run --project tests\PaintDotNetMcp.Regression -c Release
} finally {
    Remove-Item Env:COMPlus_ReadyToRun
}
```

타일 사전 합성은 [PR #1](https://github.com/Ellencia/paintdotnet-mcp/pull/1)의 기여를 반영했습니다. 0.5.15에서는 선택 영역의 픽셀 커버리지로 완료 판정을 수정했으며, 0.5.16에서는 키 메시지 전송을 직접 효과 실행으로 대체했습니다.

## 문제 해결 및 한계

| 증상 | 확인할 사항 |
| --- | --- |
| Bridge 연결 실패 | Paint.NET에서 MCP Bridge를 한 번 실행했는지 확인 |
| 플러그인이 메뉴에 없음 | Effects 폴더의 배포 파일과 Paint.NET의 플러그인 오류 확인 |
| 배포 시 Access denied | 관리자 PowerShell에서 실행 |
| 빌드 시 PaintDotNet DLL을 찾지 못함 | `PaintDotNetDir`이 실제 설치 경로인지 확인 |
| 자동 실행 또는 대기 실패 | `commit_note`, `ping.RenderError` 확인 후 메뉴에서 MCP Bridge 재실행 |
| 이미지 스냅샷이 없음 | 문서를 열고 MCP Bridge를 실행해 초기 스냅샷 생성 |
| 클라이언트에서 도구가 보이지 않음 | 실행 파일 경로, 설정 적용 범위, 클라이언트 재시작 여부 확인 |

자동 실행과 레이어·문서 조작은 내부 API를 reflection으로 호출하므로 Paint.NET 업데이트 시 호환성 확인이 필요합니다. `AutoCommitAvailable`은 MainForm 발견 여부이며 실행 성공을 보장하지 않습니다.

렌더링이 취소되면 작업은 큐에 남아 다음 실행에서 재시도됩니다. 잘못된 이미지 데이터처럼 작업 자체가 렌더링을 실패시키는 경우 해당 작업도 큐에 남으므로, 원인을 수정한 뒤 Paint.NET을 재시작해야 합니다.

읽기 도구는 브리지의 렌더링 스냅샷을 사용합니다. Paint.NET에서 수동으로 편집한 결과를 읽으려면 MCP Bridge를 다시 실행해 스냅샷을 갱신하세요. 텍스트 렌더링은 GDI+ 기반으로 Paint.NET 텍스트 도구와 결과가 다를 수 있습니다.
