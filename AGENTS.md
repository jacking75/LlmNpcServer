# AGENTS.md

**본문은 `CLAUDE.md` 다.** 이 파일은 `AGENTS.md` 를 먼저 여는 도구를 위한 포인터일 뿐이고,
규칙을 두 벌로 적지 않는다 — 두 벌이면 반드시 어긋나고, 어긋난 쪽을 읽는 것은 사람이 아니라 모델이다.

| 무엇을 알고 싶은가 | 어디 |
|---|---|
| **절대 규칙** — 틱 루프 · 계약 N1~N8 · 결정론 · 마스터데이터 · 프롬프트 · 의존 그래프 · 자주 하는 실수 | **`CLAUDE.md`** |
| **테스트를 쓸까 말까** | `CLAUDE.md` §5.1 |
| **무엇을 하려면 어디를 여는가** — 작업별 파일 지도 | `CODEMAP.md` |
| 3,000토큰 압축 컨텍스트 (첫 메시지에 그대로 넣는다) | `docs/llm/CONTEXT.md` |
| 작업별 절차 11종 · 안티패턴 · 용어 · 검증 코드 사전 | `docs/llm/` |

**MCP 가 붙어 있으면 파일을 뒤지기 전에 툴을 부른다** (`.mcp.json`). 같은 코어를 부르므로 답이 같고,
파생 정보가 같이 나온다.

---

## 시작하기 전에 · 끝내기 전에

`CLAUDE.md` §4 가 전문이다. 요약하면 — **`CODEMAP.md` 에서 작업 줄을 찾아 거기 적힌 파일만 열고**,
고칠 파일의 `tests/Npc.Tests/<같은이름>Tests.cs` 를 먼저 읽고, §2 절대 규칙에 걸리는지 본다.

```powershell
dotnet build -c Release                        # 경고 0 · 오류 0 (TreatWarningsAsErrors)
dotnet test "--filter Category!=Golden&Category!=Gate&Category!=Load"
dotnet format --verify-no-changes
dotnet run --project tools/Npc.Cli -- validate # 마스터데이터를 건드렸다면
```

- 문서와 어긋나는 변경이면 **같은 커밋에서** 해당 레퍼런스 HTML 도 고친다.
- `working_log.md` 에 항목을 더한다. 시각은 **실제 명령으로 확인한 KST** 다.
- 커밋 메시지는 한국어 `<scope>: <내용>`. **변경 하나 = 커밋 하나.**

## 모르겠으면

근거가 불충분하거나 문서와 코드가 어긋나면 **임의로 코드에 맞추지 말고 멈추고 보고한다.**
이 저장소는 어긋난 값을 어긋난 대로 적는 쪽을 택한다.
