# Paint.NET MCP

> Claude가 내 PC의 Paint.NET을 직접 조작하게 해주는 2-프로세스 MCP 브릿지 (.NET 9)

- [x] Contracts / Server(통역사) / Bridge(플러그인) 3-프로젝트 구조 구축
- [x] stdio MCP 서버 ↔ Named Pipe 브릿지 연결
- [x] 그리기 도구 (fill·draw_*·flood_fill·gradient·paste_image)
- [x] 읽기/저장 도구 (get_canvas_png·save_png·extract_region)
- [x] 누끼 remove_background (color_key·auto_corners·ai/rembg)
- [x] 객체 검출 (detect_objects·extract_objects)
- [x] 레이어/문서/효과 도구 (reflection 기반, v0.5)
- [x] Selection·OCR(Tesseract)·AI 매팅 (v0.6)
- [x] auto-commit (Ctrl+F 시뮬레이션) 구현
- [x] README에 비전공자용 스택 설명 + Claude Code 스코프 함정 기록
- [ ] apply_effect에 효과별 속성(property bag) 전달 지원
- [ ] reflection 도구들 Paint.NET 업데이트 호환성 검증/방어
- [ ] 매팅 품질 개선 (chroma + edge refine)
