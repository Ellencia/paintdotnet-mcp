# Paint.NET MCP

Paint.NET 5.x를 Model Context Protocol (MCP)으로 제어하는 2-프로세스 브릿지.

## 구조

```
[Claude] ──MCP(stdio)──▶ PaintDotNetMcp.Server.exe ──Named Pipe──▶ Paint.NET (PaintDotNetMcp.Bridge.dll Effect 플러그인)
```

- **PaintDotNetMcp.Bridge** — Paint.NET Effect 플러그인. `Effects > Tools > MCP Bridge` 메뉴에 등록되며 첫 호출 시 Named Pipe 서버(`PaintDotNetMcp.Bridge.v1`)를 띄운다. 서버는 정적 백그라운드 스레드라 Effect 인스턴스가 사라져도 Paint.NET이 종료될 때까지 살아있다.
- **PaintDotNetMcp.Server** — stdio MCP 서버. Claude Desktop 등의 MCP 클라이언트가 spawn 한다. 받은 도구 호출을 Named Pipe로 Bridge에 중계한다.
- **PaintDotNetMcp.Contracts** — 두 프로세스가 공유하는 IPC 메시지 타입.

## 빌드

```powershell
cd c:\Programming\paintdotnet-mcp
dotnet build -c Release
```

요구사항: .NET 9 SDK, Paint.NET 5.x가 `C:\Program Files\paint.net`에 설치되어 있어야 함. 다른 경로면:

```powershell
dotnet build -c Release -p:PaintDotNetDir="D:\Apps\paint.net"
```

## 설치 (자동)

빌드 + Effects 폴더 복사 + 충돌 프로세스 정리를 한 번에 하려면 PowerShell에서:

```powershell
# 일반 사용 (Program Files\paint.net\Effects 쓰려면 관리자 권한 PowerShell 필요)
.\deploy.ps1

# 다른 install 경로
.\deploy.ps1 -PaintDotNetDir 'D:\Apps\paint.net'

# 빌드 건너뛰고 복사만
.\deploy.ps1 -SkipBuild
```

스크립트가 하는 일:
1. `PaintDotNetMcp.Server.exe`(Claude Desktop spawn)가 살아있으면 종료 — DLL lock 해제
2. Paint.NET 실행 중이면 경고만 (자동 종료 X — 작업 중일 수 있음)
3. `dotnet build -c Release` 실행
4. `PaintDotNetMcp.Bridge.dll` / `Contracts` / `SkiaSharp` / `libSkiaSharp` / `System.Drawing.Common` Effects 폴더로 복사
5. Paint.NET·Claude Desktop 재시작 안내

또는 MSBuild 타겟 직접:

```powershell
dotnet build -c Release -t:Deploy
dotnet build -c Release -t:Deploy -p:EffectsDir="D:\Apps\paint.net\Effects"
dotnet build -c Release -p:DeployOnBuild=true   # 매 빌드 자동 배포
```

## 설치 (수동)

빌드 출력 폴더(`src\PaintDotNetMcp.Bridge\bin\Release\net9.0-windows\`)에서 다음을 Paint.NET Effects 폴더로 복사:

```
PaintDotNetMcp.Bridge.dll
PaintDotNetMcp.Contracts.dll
SkiaSharp.dll
libSkiaSharp.dll       (Skia 네이티브; AfterTargets로 평탄화됨)
System.Drawing.Common.dll   (Paint.NET이 이미 동일 버전 가지고 있으면 생략 가능)
```

복사 시 Paint.NET 실행 중이면 종료 후 재실행.

## Claude Desktop 등록

`%APPDATA%\Claude\claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "paintdotnet": {
      "command": "C:\\Programming\\paintdotnet-mcp\\src\\PaintDotNetMcp.Server\\bin\\Release\\net9.0\\PaintDotNetMcp.Server.exe"
    }
  }
}
```

## 사용 흐름

1. Paint.NET을 켠다.
2. 아무 이미지를 열고 (또는 `File > New`) **`Effects > Tools > MCP Bridge`** 를 한 번 실행한다. → 백그라운드 파이프 서버 시작 + 첫 캔버스 스냅샷.
3. Claude에서 `paintdotnet.ping` 같은 도구를 호출.
4. `fill` / `draw_*` / `paste_image` 같은 변형 명령은 큐에 들어간다. 브리지가 **Ctrl+F (Repeat last effect)** 를 자동으로 Paint.NET 메인 윈도우에 보내 적용을 시도한다 (auto-commit). 실패하면 사용자가 직접 `Effects > Tools > MCP Bridge`를 다시 누르거나 Ctrl+F를 누르면 됨.
5. `get_canvas_png` / `extract_region` / `remove_background` / `save_png` 는 마지막으로 렌더된 스냅샷을 읽어 응답한다.

## 도구 목록 (v0.5 / v0.6)

### 연결
| Tool | 설명 |
|---|---|
| `ping` | 브릿지 상태, 캔버스 크기, pending op 수, auto-commit 가용 여부 |
| `commit` | 큐에 쌓인 op들을 강제로 커밋 시도 (Ctrl+F 시뮬레이션 또는 reflection) |
| `set_auto_commit` | 자동 커밋 on/off 토글. 배치 작업할 때 끄고 마지막에 `commit` 호출 |

### 그리기 (큐, 자동 커밋 시도)
| Tool | 설명 |
|---|---|
| `fill` | 영역 또는 전체를 RGBA 단색으로 채움 |
| `draw_rectangle` | 사각형 (외곽선/채움, 두께) |
| `draw_line` | 선 (Bresenham + 두께) |
| `draw_ellipse` | 타원 (외곽선/채움) |
| `draw_polygon` | 다각형 (점 배열, 외곽선/채움, even-odd) |
| `draw_text` | 텍스트 (시스템 폰트, 굵게/기울임/AA) |
| `flood_fill` | 페인트 통 (시드 픽셀 + 톨러런스) |
| `gradient_fill` | 선형/방사형 그라디언트 |
| `paste_image` | base64 PNG를 (x,y)에 붙이기 (alpha-over 또는 replace) |

### 읽기 / 저장 (스냅샷 기반)
| Tool | 설명 |
|---|---|
| `get_canvas_png` | 현재 캔버스(또는 영역)를 base64로 반환. format='png'/'webp'/'jpeg' 선택. `maybe_stale` 플래그로 미커밋 상태 표시 |
| `save_png` | 캔버스(또는 영역)를 호스트 파일 경로로 저장. 확장자(`.png`/`.webp`/`.jpg`)로 포맷 자동 추론 또는 `format` 명시 |
| `extract_region` | 특정 영역만 추출. 옵션으로 디스크 저장. `savePath` 있으면 `includeBase64` 기본값 false (응답 폭발 방지) |
| `remove_background` | 누끼 따기 (color_key / auto_corners, 톨러런스, feather). 옵션으로 디스크 저장 + 옵션으로 원 좌표에 다시 붙여 캔버스를 투명화. WebP 저장 가능 |

### 객체 검출 (v0.4)
| Tool | 설명 |
|---|---|
| `detect_objects` | 균일 배경 위의 객체(아이콘 등)들의 bbox를 자동 검출. Connected-components + tolerance/minSize/maxSize/groupGap/maxAspectRatio 필터. 행 단위 정렬 |
| `extract_objects` | 검출 + 각 bbox별 PNG/WebP/JPEG 저장 일괄. `savePathTemplate`에 `{i:000}`, `{x}` 등 placeholder 지원 |

### 레이어 / 문서 (v0.5, reflection 기반)
| Tool | 설명 |
|---|---|
| `list_layers` | 활성 문서의 레이어 목록 (인덱스/이름/크기/가시성/활성 여부) |
| `add_layer` | 새 투명 BitmapLayer 추가 |
| `delete_layer` | 인덱스로 레이어 삭제 (마지막 1개는 삭제 불가) |
| `select_layer` | 활성 레이어 변경 |
| `save_pdn` | 현재 문서를 `.pdn` 파일로 저장 |
| `list_effects` | Paint.NET에 등록된 모든 내장 효과 enumerate |
| `apply_effect` | 이름으로 내장 효과 호출 (Gaussian Blur, Sharpen, Auto-Level 등) |

리플렉션이라 Paint.NET 5 마이너 업데이트마다 깨질 수 있음. `ping` 응답의 `Probe` 필드로 각 서비스가 해결됐는지 확인.

### Selection / OCR / AI 매팅 (v0.6)
| Tool | 설명 |
|---|---|
| `set_selection_rect` | 사각형 선택 영역. 이후 모든 drawing op이 이 영역 안에서만 작동 |
| `set_selection_polygon` | 다각형 선택 (소프트웨어 측만, Paint.NET UI에는 표시 안 됨) |
| `clear_selection` | 선택 해제 |
| `ocr_region` | Tesseract CLI로 영역 OCR. `lang=kor`로 한국어 인식. `winget install UB-Mannheim.TesseractOCR` 필요 |
| `remove_background method=ai` | rembg CLI (U²-Net) 호출. 머리카락/그라데이션 배경 OK. `pip install rembg[cli]` 필요 |

**포맷 옵션 (v0.3)**:
- `format`: `auto` (기본; 경로 확장자에서 추론, 없으면 PNG) | `png` | `webp` | `jpeg`
- `quality`: 1-100, 기본 85. PNG는 무시, WebP/JPEG에 적용
- `includeBase64`: `null`이면 자동 (savePath 있으면 false, 없으면 true). 응답에 base64 포함 여부 직접 제어 가능
- `applyToLayer=true`인 누끼 매팅은 캔버스에 다시 붙일 때 항상 PNG (무손실) 사용

## 누끼 따기 한 줄 워크플로

```
1. get_canvas_png format=webp quality=70   (선택: 영역 확인용 — 응답 가벼움)
2. remove_background x y w h method=auto_corners tolerance=40 feather=true
                    savePath="C:\out\nukki.webp" applyToLayer=true
                    (savePath 있으니 includeBase64는 자동으로 false → 응답 작음)
3. (자동 커밋되면) Paint.NET 캔버스도 해당 영역이 투명해짐
```

## 아이콘 시트 자르기 (v0.4)

흰 배경의 아이콘 모음에서 각 아이콘만 추출:

```
extract_objects savePathTemplate="C:\out\icon_{i:000}.webp"
                tolerance=40 minSize=40 maxSize=200 padding=4
                groupGap=8 maxAspectRatio=3.0
                format=webp quality=90
```

- `tolerance=40` — 흰색에 가까운 픽셀 (JPEG 압축 가장자리 포함) 모두 배경 처리
- `minSize=40` / `maxSize=200` — 노이즈·텍스트·전체 캔버스 영역 제거
- `padding=4` — bbox 주변 여유 픽셀
- `groupGap=8` — 분리된 조각 (i 위 점, 점선 등) 합치기
- `maxAspectRatio=3.0` — 폭/높이 3배 초과는 텍스트 행으로 보고 제거

라벨/캡션까지 포함하려면 `groupGap=40`, `maxAspectRatio=6.0`. 라벨 자체는 OCR 없으니 의미 있는 파일명을 원하면 LLM이 보고 후처리.

검출만 하고 좌표 보고 싶으면 `detect_objects`. 좌표 직접 검토 후 따로 `extract_region` 호출도 가능.

## 자동 커밋 (auto-commit)

큐에 op이 추가될 때마다 브릿지가 다음을 시도한다:

1. **Reflection**: Effect의 `Services` 컨테이너에서 "Repeat last effect" 명령을 찾아 호출.
2. **Win32 fallback**: `PostMessage(hwnd, Ctrl+F)`로 Paint.NET 메인 윈도우에 키 입력 전달.

둘 다 실패하면 사용자가 직접 메뉴/Ctrl+F를 눌러야 한다. `ping` 응답의 `AutoCommitAvailable` 필드로 가용성 확인 가능. 응답마다 `auto_committed` + `commit_note`가 함께 온다.

주의: Ctrl+F 단축키가 Paint.NET 설정에서 변경되어 있다면 fallback이 작동하지 않는다.

## 현재 한계

- Effect 플러그인은 "현재 활성 레이어의 픽셀"만 안전하게 변경할 수 있다. 레이어 추가/삭제, 문서 저장(.pdn), 다른 도구 동작은 Paint.NET 5의 공식 플러그인 API 범위 밖. 이 부분은 reflection으로 깊이 들어가야 하며 버전 업데이트마다 깨질 가능성이 있어 v0.2에는 미포함.
- 텍스트 렌더링은 GDI+(System.Drawing) 기반이라 폰트 힌팅이 Paint.NET 내부 텍스트 도구와 미묘하게 다를 수 있음.
- 누끼 알고리즘은 단순 color-key + euclidean tolerance. 복잡한 배경/머리카락 같은 건 ML 기반 도구가 필요.
- Auto-commit Ctrl+F는 Paint.NET 메인 윈도우가 활성화돼 있어야 안정적임. 다른 앱 위에 가려져 있어도 PostMessage 자체는 가는데, 일부 메뉴 구현이 입력 큐를 무시할 수 있음.

## 다음에 추가할 만한 것

- `apply_builtin_effect` — Gaussian Blur, Auto-Level 같은 내장 효과 트리거 (deeper reflection 필요)
- `save_pdn` / `export_*` — 파일 저장 (reflection 또는 메뉴 자동화)
- 레이어 추가/삭제/병합 (reflection on DocumentWorkspace)
- 더 정교한 매팅 (chroma + edge refine, 또는 외부 ML 모델 호출)

## 트러블슈팅

- **Bridge에 연결 못 함**: Paint.NET이 실행 중인지, `Effects > Tools > MCP Bridge`를 한 번 실행했는지 확인.
- **플러그인이 메뉴에 안 보임**: Effects 폴더에 `PaintDotNetMcp.Bridge.dll` + `PaintDotNetMcp.Contracts.dll` (+ `System.Drawing.Common.dll` 필요시) 있는지, .NET 버전이 9인지.
- **빌드 시 PaintDotNet.* 못 찾음**: `PaintDotNetDir` MSBuild 속성이 실제 설치 경로를 가리키는지.
- **`get_canvas_png`가 "no snapshot yet" 반환**: 아직 한 번도 effect가 렌더되지 않은 것. `Effects > Tools > MCP Bridge`를 한 번 실행하면 스냅샷이 채워짐.
- **자동 커밋이 안 됨**: `ping` 결과의 `AutoCommitAvailable`이 false면 reflection/HWND 둘 다 실패한 것. Ctrl+F 단축키가 살아있는지, Paint.NET이 포커스된 적 있는지 확인. 마지막 수단으로 `commit` 도구를 명시적으로 호출.
