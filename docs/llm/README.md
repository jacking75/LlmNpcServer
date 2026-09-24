# LLM 연결과 개발 에이전트

서버가 NPC 플랜을 만들 때 부르는 **런타임 LLM**과, 사람이 콘텐츠를 편집하도록 돕는 **개발 에이전트**는 역할이 다르다. 런타임 프롬프트와 검증은 `Npc.Llm`이 담당한다. 개발 에이전트에게는 [스킬](SKILL.md)과 [압축 컨텍스트](CONTEXT.md)를 제공한다.

## OpenAI API 키로 연결

OpenAI 공식 [GPT-4.1 mini 모델 페이지](https://developers.openai.com/api/docs/models/gpt-4.1-mini)의 2026-09-24 확인 단가를 설정에 넣었다. 실제 결제 전 최신 가격을 다시 확인한다. API 키를 저장소 파일에 쓰지 않는다.

```powershell
$env:OPENAI_API_KEY = '<본인 키>'
dotnet run --project src/Npc.Host -- doctor --engine openai-gpt-4.1-mini
dotnet run --project src/Npc.Host -- --tier t2 --t2-engine openai-gpt-4.1-mini --billing-cap-usd 1
```

키가 없으면 온라인 진단과 외부 호출은 하지 않는다. 위 명령은 비용이 발생한다.

## Ollama로 연결

```powershell
ollama pull qwen3:8b
dotnet run --project src/Npc.Host -- doctor --engine ollama-qwen3-8b
dotnet run --project src/Npc.Host -- --tier t1 --t1-engine ollama-qwen3-8b
```

Ollama의 OpenAI 호환 포트는 `11434`다. 로컬 모델의 속도와 품질은 실행 기계에서 측정한다.

## LM Studio와 다른 로컬 서버

LM Studio의 OpenAI 호환 서버를 포트 `1234`에서 켠 뒤, 설치한 모델 ID를 `appsettings.Llm.local.json`에 설정한다. 저장소의 `llamacpp-qwen3-8b`는 같은 포트를 사용하는 시작 예제다. 로컬 덮어쓰기 파일은 gitignore되며 `engines` 항목을 ID로 교체하거나 추가하고 `preferred`·`chains`를 덮어쓴다. 키의 **값**을 JSON에 쓰지 않는다.

```json
{
  "preferred": ["ollama-qwen3-8b"],
  "chains": { "t1": ["ollama-qwen3-8b"] }
}
```

dotLLM은 별도 프로세스와 별도 라이선스다. 실행 파일을 이 저장소나 배포 zip에 포함하지 않는다. 전체 엔진 설정의 구조는 [`appsettings.Llm.json`](../../appsettings.Llm.json), 비밀값 운영은 [시크릿 안내](../security/secrets.md)를 본다.

## 개발 에이전트에게 콘텐츠 작업을 맡길 때

`npc card archetype <id>`로 현 상태를 읽고, `CODEMAP.md`에서 편집할 파일을 찾는다. 코드를 새로 쓰기 전에 데이터 변경으로 가능한지 확인한다. 번호는 `npc next-code`로 받고, 수정 뒤 `npc validate --json`, `npc diff`, `npc regen --check` 결과를 사람에게 보고한다. 플랜 품질은 `npc plan validate`의 종료 코드로 판단한다. [작업별 절차](RECIPES/), [안티패턴](ANTIPATTERNS.md), [검증 코드 사전](VALIDATION.md), [요청 템플릿](PROMPTS.md)이 있다.
