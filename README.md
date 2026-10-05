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

저장소 루트의 일반 PowerShell에서 실행합니다. 먼저 작업을 저장하고 Paint.NET을 종료하세요.

```powershell
.\install.ps1
# 다른 설치 경로
.\install.ps1 -PaintDotNetDir 'D:\Apps\paint.net'
```

[install.ps1](install.ps1)은 서버와 Bridge를 빌드하고 플러그인 DLL 5개를 설치한 뒤 파일 해시를 확인합니다. 복사 권한이 부족하면 Windows UAC 창에서 관리자 권한을 요청합니다. Paint.NET이 실행 중이면 종료 안내와 함께 중단하며, 자동으로 종료하지 않습니다. 현재 저장소에서 빌드한 MCP 서버가 실행 중이면 빌드를 위해 종료합니다.

설치 후 Paint.NET에서 캔버스를 열고 MCP 클라이언트를 다시 연결하세요. Bridge는 플러그인 검색 중 시작되며, 연결 후 초기 스냅샷을 준비합니다. PowerShell 창은 닫아도 됩니다. 스크립트는 최초 설치와 코드 업데이트 때만 실행합니다.

실행 정책으로 스크립트가 차단되는 환경에서는 이번 실행에만 다음 명령을 사용할 수 있습니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
```

### 빌드 및 배포

직접 빌드와 배포를 수행하는 경우의 명령입니다. 기본 설치 경로에 배포할 때는 관리자 PowerShell이 필요합니다.

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

관리자 PowerShell에서 직접 배포하거나 기존 빌드 결과만 복사하려면 [deploy.ps1](deploy.ps1)을 사용할 수 있습니다. 자동 권한 요청을 포함한 설치는 위의 `install.ps1`을 사용하세요.

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
2. MCP 클라이언트를 연결합니다. Tools 메뉴를 누를 필요 없이 Bridge가 초기 스냅샷을 준비합니다.
3. `ping`에서 `ConnectionStatus=ready`, `SnapshotReady=true`인지 확인합니다. 다른 상태이면 `RecoveryAction`의 안내를 따릅니다.
4. 그리기 도구를 호출합니다. 기본 설정에서는 MCP Bridge가 자동으로 실행됩니다.
5. `wait_for_idle`로 완료를 확인한 뒤 이미지를 읽거나 저장합니다.

여러 작업을 한 번에 적용하려면 다음 순서를 사용합니다.

```text
begin_batch()
fill(r=230, g=40, b=15)
draw_rectangle(x=100, y=100, width=100, height=100, r=10, g=100, b=240, fill=true)
end_batch()
wait_for_idle(timeoutMs=5000)
save_png(path="C:\out\canvas.png")
```

위 예시는 도구 호출 순서를 나타냅니다. PowerShell 명령이 아닙니다. 좌표와 크기는 픽셀 단위이며 그리기는 활성 레이어에 적용됩니다.

`begin_batch`는 현재 문서와 레이어에 작업 묶음을 시작하고 자동 실행을 잠시 끕니다. `end_batch`는 그리기를 한 번에 실행하고 Paint.NET의 Undo 한 단계로 기록된 것을 확인한 뒤 이전 자동 실행 설정을 복원합니다. 빈 배치는 이력을 만들지 않습니다. 배치 도중에는 탭·레이어·선택 영역을 바꾸거나 수동 편집을 하지 마세요. 실행 실패나 취소 시 배치는 유지되어 `end_batch`로 재시도할 수 있습니다.

`undo`와 `redo`는 활성 문서의 Paint.NET 이력을 한 단계씩 이동합니다. 수동 편집 이력도 대상입니다. 이후 활성 레이어의 스냅샷을 직접 갱신하므로 읽기·저장에 즉시 반영되고, 스냅샷 갱신이 새 Undo 이력을 만들지는 않습니다. 이동할 이력이 없으면 `changed=false`를 반환합니다. 배치 또는 미적용 그리기가 남아 있으면 먼저 적용해야 합니다.

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
| `begin_batch`, `end_batch` | 여러 그리기를 하나의 Undo 단계로 적용 |
| `undo`, `redo` | 활성 문서의 이력 이동 및 읽기 스냅샷 갱신 |
| `diagnose_services` | 내부 서비스 연결 진단 |

### 그리기

| 도구 | 기능 |
| --- | --- |
| `fill` | 전체 또는 지정 영역 단색 채우기 |
| `draw_rectangle`, `draw_ellipse`, `draw_polygon` | 도형의 외곽선 또는 내부 그리기 |
| `draw_line` | 선 그리기 |
| `draw_text` | 시스템 폰트로 텍스트 그리기 |
| `create_text_layer` | 원문·폰트·위치·색을 보존하는 편집 가능한 텍스트 레이어 생성 |
| `update_text_layer` | 텍스트 속성 변경 후 해당 레이어 다시 렌더링 |
| `get_text_layer`, `list_text_layers` | 텍스트 속성 및 픽셀 변경 여부 조회 |
| `flood_fill` | 지정 픽셀에서 허용 오차에 따라 채우기 |
| `gradient_fill` | 선형 또는 방사형 그라디언트 |
| `paste_image` | base64 PNG를 지정 좌표에 합성 또는 덮어쓰기 |

### 이미지 읽기 및 처리

| 도구 | 기능 |
| --- | --- |
| `get_canvas_png` | 활성 레이어 전체 또는 영역을 base64 이미지로 반환 |
| `save_png` | 활성 레이어 전체 또는 영역을 파일로 저장 |
| `get_document_image` | 표시된 모든 레이어를 합성해 MCP 이미지 응답으로 미리보기 |
| `export_document` | 표시된 모든 레이어의 합성 결과를 이미지 파일로 내보내기 |
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
| `open_image` | 절대 파일 경로로 이미지를 열고 활성 문서로 전환 |
| `new_canvas` | 지정한 픽셀 크기의 새 흰색 캔버스 생성 |
| `copy_selection_to_layer` | 선택한 픽셀을 원래 위치의 새 투명 레이어로 복사 |
| `resize_canvas` | 픽셀 배율을 유지하며 모든 레이어의 캔버스 크기 변경 |
| `crop_to_selection` | 기본 선택 영역으로 모든 레이어 자르기 |
| `transform_layer` | 활성 레이어 픽셀의 이동·확대/축소·회전 |
| `list_layers` | 레이어 목록 조회 |
| `add_layer`, `delete_layer`, `select_layer` | 레이어 추가·삭제·활성화 |
| `save_pdn` | 문서를 Paint.NET 형식으로 저장 |
| `list_effects`, `apply_effect` | 효과 목록 조회 및 실행 요청 |
| `set_selection_rect`, `set_selection_polygon`, `clear_selection` | 선택 영역 설정 및 해제 |
| `get_selection` | 현재 기본 선택 영역의 표시 여부와 캔버스 내 범위 조회 |

레이어·문서·효과 실행과 기본 선택 연동은 Paint.NET 내부 API에 의존합니다. 사각형·다각형 선택은 Paint.NET의 기본 점선 테두리로 표시되며 이미지 픽셀은 변경하지 않습니다. 내부 API를 사용할 수 없으면 오류를 반환합니다.

## 작업 예시

### 문서 준비

```text
new_canvas(width=1200, height=800)
# 또는 기존 파일 열기
open_image(path="C:\images\source.png")
```

두 도구는 Paint.NET의 기본 문서 생성·파일 로더를 사용하며 새 문서를 활성화하고 읽기·저장용 스냅샷을 바로 준비합니다. 수정한 기존 문서는 닫지 않습니다. `open_image`는 절대 경로의 기존 파일을 받으며 Paint.NET에서 지원하는 형식을 엽니다. 형식에 따라 로딩 옵션이나 오류 창이 표시될 수 있습니다.

`new_canvas`의 기본 크기는 800×600이며 해상도는 96 DPI입니다. 각 변은 1~16384픽셀, 전체는 최대 6400만 픽셀로 제한합니다. 배치 진행 중이거나 미적용 그리기가 있으면 문서를 전환할 수 없습니다. 먼저 `end_batch` 또는 `commit`과 `wait_for_idle`로 기존 작업을 적용하세요.

### 레이어 배치

```text
clear_selection()
transform_layer(offsetX=100, offsetY=50)
wait_for_idle()
transform_layer(scaleX=0.5, scaleY=0.5, angleDegrees=30)
wait_for_idle()
```

`transform_layer`는 활성 레이어의 픽셀을 변환합니다. 배율은 1이 원래 크기, 0.5가 절반, 2가 두 배입니다. 피벗 주변에서 확대/축소한 다음 시계 방향으로 회전하고 마지막에 픽셀 단위로 이동합니다. 피벗은 기본적으로 캔버스 중심이며 `pivotX`, `pivotY`로 지정할 수 있습니다. 좌표의 원점은 캔버스 왼쪽 위 모서리입니다.

캔버스 크기는 유지되므로 밖으로 나간 픽셀은 잘리고 빈 영역은 투명해집니다. 현재 선택 영역은 변환 결과가 적용되는 영역을 제한합니다. 전체 레이어를 변환하려면 먼저 `clear_selection`을 호출하세요. 기본 `interpolation="bilinear"`는 투명도를 고려해 부드럽게 보간하며, 픽셀 아트에는 `"nearest"`를 사용할 수 있습니다. Undo/Redo와 `begin_batch`/`end_batch`를 지원합니다.

### 선택 영역 미리보기

```text
set_selection_rect(x=40, y=30, width=100, height=80)
get_selection()
# 다각형으로 교체
set_selection_polygon(pointsJson='[{"x":60,"y":40},{"x":260,"y":40},{"x":60,"y":240}]')
```

선택 테두리는 Paint.NET 화면에서 확인하고 기본 선택 도구로 직접 조정할 수 있습니다. `get_selection`은 수동 조정한 현재 선택도 조회합니다. `NativeVisible`은 캔버스와 겹치는 선택의 표시 여부, `X`, `Y`, `Width`, `Height`는 캔버스 안의 선택 경계 상자입니다. 경계 상자는 다각형 자체의 모양을 나타내지는 않습니다. `IsEmpty=true`는 선택이 없는 상태이며 이후 그리기는 전체 캔버스에 적용됩니다. 이때 반환 좌표와 크기는 0입니다.

설정·해제는 Undo/Redo를 지원하며 `HistorySteps`로 추가된 단계 수를 반환합니다. 이미 해제된 선택을 다시 해제하면 0입니다. 캔버스와 전혀 겹치지 않는 선택 요청은 거부하고 기존 선택을 복원합니다. 배치나 미적용 그리기가 있으면 먼저 해당 작업을 끝내세요.

### 선택한 부분을 새 레이어로 분리

```text
set_selection_rect(x=40, y=30, width=100, height=80)
copy_selection_to_layer(name="Cutout")
clear_selection()
transform_layer(offsetX=100, offsetY=50)
wait_for_idle()
```

활성 레이어에서 선택된 픽셀을 원본 바로 위의 새 레이어로 복사하고 그 레이어를 활성화합니다. 원본 픽셀과 캔버스 좌표는 유지되고 선택 밖은 투명합니다. 다각형도 선택의 픽셀 커버리지에 따라 복사하며 경계 페더링은 적용하지 않습니다. 복사는 Undo 한 단계입니다. 이어서 레이어 전체를 이동하려면 선택을 해제하세요.

### 캔버스 크기 변경과 자르기

```text
resize_canvas(width=1600, height=1000, anchor="center")
set_selection_rect(x=100, y=100, width=800, height=600)
crop_to_selection()
```

`resize_canvas`는 이미지 배율을 바꾸지 않고 모든 레이어의 크기를 변경합니다. `anchor`는 `top_left`, `top`, `top_right`, `left`, `center`, `right`, `bottom_left`, `bottom`, `bottom_right` 중 하나입니다. 추가 공간은 기본적으로 투명하며 `r`, `g`, `b`, `a`로 채울 색을 지정할 수 있습니다. 크기를 줄이면 앵커를 기준으로 밖의 픽셀이 잘립니다. 크기 제한은 `new_canvas`와 같습니다.

`crop_to_selection`은 Paint.NET의 기본 자르기를 사용합니다. 다각형 선택은 경계 상자 크기로 자르고 모양 밖을 투명하게 만듭니다. 두 도구 모두 선택을 해제하고 Undo 한 단계로 픽셀·크기·선택을 복원합니다. 같은 크기로 변경하면 이력을 추가하지 않습니다. 복사·크기 변경·자르기는 배치나 미적용 그리기가 있으면 먼저 해당 작업을 끝내야 합니다.

### 편집 가능한 텍스트

```text
create_text_layer(
  name="Heading", text="Paint.NET MCP\n한글 텍스트",
  x=30, y=20, fontFamily="Malgun Gothic", fontSize=32,
  bold=true, r=30, g=80, b=180
)
update_text_layer(text="수정한 제목", fontSize=40, x=60, y=40)
get_text_layer()
save_pdn(path="C:\out\banner.pdn")
```

활성 레이어 위에 텍스트 전용 레이어를 만들고 원문·폰트·스타일·위치·색을 함께 저장합니다. `update_text_layer`에서 생략한 속성은 유지됩니다. 기본 대상은 활성 레이어이며 `layerIndex`로 다른 텍스트 레이어를 지정할 수 있습니다. 수정 시 레이어의 전체 픽셀을 원문에서 다시 그리므로 기존 글자가 남지 않고 폰트 크기를 바꿔도 새 크기로 렌더링합니다. 레이어 표시 여부·불투명도·혼합 모드는 유지하며 대상 레이어를 활성화합니다.

Paint.NET에서는 일반 비트맵 레이어로 보이고 텍스트 수정은 MCP 도구로 합니다. `.pdn`에는 텍스트 속성이 남아 다시 열어도 수정할 수 있습니다. PNG·WebP·JPEG로 내보낸 이미지에는 편집 속성이 남지 않습니다. 생성·수정은 각각 Undo 한 단계이며 속성과 픽셀을 함께 복원합니다. 같은 속성으로 수정하면 이력을 추가하지 않습니다. 선택 영역은 텍스트 레이어 생성·수정을 제한하지 않으며 배치나 미적용 그리기가 있으면 먼저 끝내세요.

레이어에 직접 그림을 추가하거나 `transform_layer`, 캔버스 크기 변경·자르기로 픽셀 또는 크기를 바꾼 경우 `PixelsModified=true`로 표시하고 텍스트 수정은 거부합니다. 해당 변경을 Undo하거나, 저장된 텍스트로 레이어 전체를 교체하려는 경우에만 `replaceModifiedPixels=true`를 지정하세요. 텍스트 이동·크기 조정에는 `update_text_layer`의 `x`, `y`, `fontSize`를 사용하는 편이 좋습니다. 기존 `draw_text`로 그린 픽셀에는 텍스트 속성이 없으므로 이 도구로 수정할 수 없습니다.

### 전체 레이어 미리보기와 내보내기

```text
get_document_image()
export_document(path="C:\out\banner.png")
export_document(path="C:\out\banner.webp", quality=90)
# 합성 결과의 일부만 내보내기
export_document(path="C:\out\crop.png", x=100, y=50, width=400, height=200)
```

Paint.NET의 합성 엔진으로 레이어 순서·표시 여부·불투명도·혼합 모드를 반영합니다. 미리보기는 MCP 이미지 콘텐츠로 반환하며 내보내기는 절대 경로에 PNG·WebP·JPEG로 저장합니다. PNG는 투명도를 보존하고 `quality`는 WebP·JPEG에 적용됩니다. 영역 지정은 캔버스와 겹치는 부분으로 잘리고 반환 크기는 실제 이미지 크기입니다.

원본 레이어를 병합하지 않고 선택 영역과 Undo 이력도 유지합니다. 진행 중인 배치는 먼저 `end_batch`로 끝내세요. Paint.NET의 수동 도구에서 아직 확정하지 않은 편집 오버레이는 합성에 포함되지 않습니다. 활성 레이어만 읽거나 저장하려면 기존 `get_canvas_png`, `save_png`를 사용하세요.

### 픽셀 텍스트 그리기

```text
draw_text(
  x=30, y=20, text="Paint.NET MCP\n한글 텍스트",
  fontFamily="Malgun Gothic", fontSize=32, bold=true,
  r=30, g=80, b=180
)
wait_for_idle()
```

`draw_text`는 활성 레이어의 픽셀에 직접 그리며 편집 속성을 보존하지 않습니다. 설치된 폰트와 지원하는 스타일을 지정하며, 없는 폰트를 요청하면 오류를 반환합니다. `draw_text`와 편집 가능한 텍스트 레이어의 `fontSize`는 픽셀 단위로 0보다 크고 512 이하, 텍스트는 공백만 있는 문자열을 제외한 최대 4096자입니다. 렌더링할 텍스트 비트맵은 각 변 최대 16384픽셀, 전체 최대 1600만 픽셀입니다. 줄바꿈, 굵게·기울임, RGBA 색상을 지원합니다. `draw_text`는 선택 영역 제한과 Undo/Redo를 지원하며 여러 요청을 `begin_batch`·`end_batch`로 묶을 수 있습니다.

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

Bridge의 파이프 서버는 플러그인 검색 중 생성자가 호출될 때 시작하며 Paint.NET 프로세스가 종료될 때까지 유지됩니다. 초기 스냅샷은 UI 스레드에서 활성 레이어를 읽어 준비하며 효과 실행이나 Undo 항목을 만들지 않습니다. 기본 파이프 이름은 `PaintDotNetMcp.Bridge.v1`입니다.

## 검증

0.5.16은 Paint.NET 5.1.12의 1400×1050 캔버스에서 다음을 확인했습니다.

- Ctrl+F 없이 자동으로 전체 채우기와 우측 하단 사각형 반영
- 자동 실행을 끈 상태에서 명시적 `commit`으로 배치 적용
- `wait_for_idle` 성공, 대기 작업 0, 렌더링 오류 없음
- 저장 PNG를 다시 열어 전체 색상 픽셀 수와 좌표 검증

회귀 검증은 병렬 타일 렌더링, 호스트의 재사용 ROI 배열, 취소 후 재시도, 선택 영역, MCP stdio·파이프 연결, 저장 이미지, 오류 처리와 UI 실행 예약을 포함한 9개 항목입니다. 레이어·문서·OCR·AI 배경 제거 등 전체 도구의 실제 앱 동작을 모두 검증한 결과는 아닙니다.

0.5.17은 Undo/Redo와 명시적 작업 묶음을 추가합니다. 배치 상태·문서 바인딩·빈 배치·빈 이력의 스냅샷 갱신을 포함한 회귀 검증 10개를 통과했습니다. 실제 Paint.NET 5.1.12의 1400×1050 캔버스에서는 그리기 3개가 Undo 한 단계로 기록되는 것을 확인했습니다. Undo 후 저장 PNG는 작업 전 이미지와, Redo 후 저장 PNG는 적용 결과와 전체 픽셀이 일치했습니다. 새 편집 후 Redo 이력 초기화와 빈 배치의 이력 미생성도 확인했습니다.

0.5.18은 Paint.NET 5.1.12의 1400×1050 캔버스에서 Tools 메뉴를 실행하지 않고 `ConnectionStatus=ready`, `SnapshotReady=true`로 연결되는 것을 확인했습니다. 초기 스냅샷 저장·재개방과 자동 그리기 후 PNG의 전체 픽셀 검증도 통과했습니다. Paint.NET 미실행 안내가 실제 MCP 오류 응답에 전달되는 것을 확인했으며, 버전 불일치 시 수정 명령 차단과 문서 없음·재연결 상태를 포함한 회귀 검증 11개가 통과했습니다. 다른 Paint.NET 버전의 자동 시작은 별도 확인이 필요합니다.

0.5.19는 실제 Paint.NET 5.1.12에서 320×240 및 96×64 흰색 캔버스 생성·즉시 저장을 확인했습니다. 새 캔버스에서 그린 PNG를 `open_image`로 다시 열고 저장한 결과는 원본과 전체 픽셀이 일치했습니다. 잘못된 크기·상대 경로 요청 후에도 활성 문서가 유지됐으며, 입력 검증과 문서 전환 제한을 포함한 회귀 검증 12개가 통과했습니다. 실제 파일 열기 검증은 PNG로 수행했습니다.

0.5.20은 실제 Paint.NET 5.1.12의 64×64 캔버스에서 레이어 이동·90도 회전·피벗 기준 2배 확대를 확인했습니다. 저장 PNG의 위치·색상·투명 픽셀 수와 Undo/Redo의 전체 픽셀 복원을 검증했습니다. 변환 두 개를 묶은 배치는 Undo 한 단계로 기록됐으며 반 픽셀 이동의 투명도 보간도 확인했습니다. 렌더 직후 저장 시 효과 처리 완료를 기다리도록 수정했고, 회귀 검증 13개가 통과했습니다.

0.5.21은 실제 Paint.NET 5.1.12에서 사각형·삼각형 기본 선택을 설정하고 점선 테두리가 화면에 표시되는 것을 확인했습니다. 선택 미리보기 전후 이미지 픽셀은 동일했으며, 선택 안쪽만 그리기·선택 Undo/Redo·빈 선택 해제·캔버스 바깥 요청 시 복원을 검증했습니다. UI에서 직접 바꾼 선택 범위도 실제 MCP `get_selection` 호출에 반영됐습니다. 회귀 검증 14개가 통과했습니다.

0.5.22는 실제 Paint.NET 5.1.12에서 사각형·삼각형 선택의 새 레이어 복사, 원본과 반투명 픽셀 유지, 복사 후 이동을 확인했습니다. 세 레이어의 크기 변경, 투명·단색 확장, 중앙·왼쪽 위·오른쪽 아래 앵커, 축소와 사각형·다각형 자르기를 검증했습니다. 저장 PNG를 다시 열어 예상 이미지 및 Undo/Redo 결과와 픽셀을 비교했습니다. 한글 두 줄 텍스트·선택 제한·없는 폰트의 오류 처리와 텍스트 두 개의 단일 Undo 배치도 확인했으며, 회귀 검증 15개가 통과했습니다.

0.5.23은 실제 Paint.NET 5.1.12에서 텍스트 레이어 생성, 내용·폰트 크기·위치·색·이름 수정, 속성·픽셀의 Undo/Redo와 무변경 요청의 이력 미생성을 확인했습니다. 수동 픽셀 편집과 캔버스 크기 변경을 감지하고 덮어쓰기를 차단하며 명시적 재생성과 그 Undo/Redo도 검증했습니다. `.pdn` 저장·재개방 후 텍스트 속성과 픽셀이 유지됐고 재수정도 통과했습니다. 합성 미리보기의 MCP 이미지 응답과 PNG 내보내기는 전체 픽셀이 일치했으며 WebP·JPEG의 재개방, 영역 잘림과 선택·레이어 보존을 확인했습니다. 불투명도·숨긴 레이어·Multiply 혼합 모드를 포함한 회귀 검증 18개가 통과했습니다.

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
| `paintdotnet_not_running` | Paint.NET과 캔버스를 열고 재시도 |
| `bridge_unavailable` | 시작이 끝난 뒤 재시도. 계속 실패하면 Tools 메뉴의 MCP Bridge 실행 또는 설치·플러그인 오류 확인 |
| `bridge_version_mismatch` | Paint.NET 종료 → `install.ps1` → Paint.NET 실행 및 MCP 클라이언트 재연결 |
| `ping.ConnectionStatus=no_document` / `no_active_layer` | 캔버스를 열거나 비트맵 레이어를 선택한 뒤 재시도 |
| `ping.ConnectionStatus=host_not_ready` / `render_failed` | `RecoveryAction`에 표시된 시작·렌더 완료 대기 또는 재실행 안내 확인 |
| 플러그인이 메뉴에 없음 | Effects 폴더의 배포 파일과 Paint.NET의 플러그인 오류 확인 |
| 배포 시 Access denied | 관리자 PowerShell에서 실행 |
| 빌드 시 PaintDotNet DLL을 찾지 못함 | `PaintDotNetDir`이 실제 설치 경로인지 확인 |
| 자동 실행 또는 대기 실패 | `commit_note`, `ping.RenderError` 확인 후 메뉴에서 MCP Bridge 재실행 |
| 이미지 스냅샷이 없음 | 캔버스를 열고 `ping`의 `ConnectionStatus`와 `RecoveryAction` 확인 |
| 클라이언트에서 도구가 보이지 않음 | 실행 파일 경로, 설정 적용 범위, 클라이언트 재시작 여부 확인 |

자동 실행과 레이어·문서 조작은 내부 API를 reflection으로 호출하므로 Paint.NET 업데이트 시 호환성 확인이 필요합니다. `AutoCommitAvailable`은 MainForm 발견 여부이며 실행 성공을 보장하지 않습니다.

렌더링이 취소되면 작업은 큐에 남아 다음 실행에서 재시도됩니다. 잘못된 이미지 데이터처럼 작업 자체가 렌더링을 실패시키는 경우 해당 작업도 큐에 남으므로, 원인을 수정한 뒤 Paint.NET을 재시작해야 합니다.

읽기 도구는 대기 중인 렌더링 완료 후 활성 레이어의 스냅샷을 갱신합니다. 수동 편집 후에도 MCP Bridge를 다시 실행할 필요가 없습니다. 텍스트 렌더링은 GDI+ 기반으로 Paint.NET 텍스트 도구와 결과가 다를 수 있습니다.
