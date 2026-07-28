# Phase 0 (W1) — 스파이크

> **목적: 상위 계획의 산술 가정이 실측과 맞는지 확인한다.** 여기서 가정이 크게 틀리면 W2 이후 설계를 갈아엎어야 하므로, 코드 품질은 전혀 신경 쓰지 않고 버릴 코드를 빠르게 쓴다.
>
> **산출물은 코드가 아니라 숫자 6개다.**

---

## 1. 게이트 (이걸 넘지 못하면 다음 주로 못 간다)

| 게이트 | 기준 | 실패 시 |
|---|---|---|
| G0-1 | **어휘 검증 통과율**(검증기 1·2단) 기준 최고 엔진이 **≥ 90/100** | 스키마를 단순화하거나 모델 상향. 실패하면 DSL을 더 단순한 형태로 재설계 |
| G0-2 | 요청당 지연이 상위 계획 가정(1.75s)의 **±50% 이내** | 계획서 §2.1, §10.1 수치 전면 갱신 |
| G0-3 | 프롬프트 캐시 적중 시 prefill 시간이 **70% 이상 감소** | 프리픽스 설계 재검토. 미적중이면 캐시 전제가 무너지므로 §10.5(a) 재검토 |
| G0-4 | 4B 모델이 사람 평가에서 **8B의 80% 이상 품질** | 4B 포기하고 8B 이상으로 확정. 로컬 처리량 가정 하향 |

> **G0-1 의 기준은 2026-07-28 에 바뀌었다** — 원래는 "강제 디코딩으로 유효 JSON 100/100" 이었다.
> W1 실측에서 `forced` 가 **유효 JSON 99/100 · 어휘 검증 0/100** 이 나왔다. 문법을 완벽히 지키면서
> 존재하지 않는 인자를 지어낸다(`V2.UNKNOWN_ARG` 99). 같은 모델의 `prompt` 모드는 93/100 이다.
>
> **유효 JSON 으로 재면 100% 실패를 통과로 읽는다.** 파서를 통과하는 것과 쓸 수 있는 플랜인 것은
> 다른 문제이고, 이 게이트가 물어야 하는 것은 뒤쪽이다. 그래서 판정 기준을 **어휘 검증 통과율**로
> 바꾸고 강제 디코딩 전제를 뺐다 (`measurements/W1_results.md` §2 · `TASKS.md §3`).
> 100/100 → 90/100 으로 낮춘 것은 강제 디코딩을 안 쓰기로 한 뒤(`docs/12 §4`)
> 문법 오류가 정상 분포로 섞이기 때문이다.

---

## 2. 작업 항목

### T0-1. dotLLM 기동 (0.5일)

```powershell
# 릴리스 바이너리 (Windows x64) 다운로드 후
.\dotllm.exe serve --model .\models\Qwen3-8B-Q4_K_M.gguf --port 8080 --gpu-layers 99
# 확인
curl http://localhost:8080/v1/models
```

체크:
- [ ] `/v1/chat/completions` 응답
- [ ] SSE 스트리밍 동작
- [ ] `--gpu-layers` 로 VRAM 점유 확인 (`nvidia-smi`)
- [ ] 하이브리드 오프로딩(VRAM 초과 모델) 동작 여부 — 12GB 카드라면 14B로 확인

### T0-2. `IChatClient` 배선 (0.5일)

```csharp
// spike/Program.cs
using Microsoft.Extensions.AI;
using OpenAI;

var local = new OpenAIClient(new("dummy"), new OpenAIClientOptions {
        Endpoint = new Uri("http://localhost:8080/v1") })
    .GetChatClient("qwen3-8b").AsIChatClient();

var external = new OpenAIClient(Environment.GetEnvironmentVariable("OPENAI_API_KEY"))
    .GetChatClient("gpt-5-nano").AsIChatClient();
```

체크:
- [ ] **동일한 코드 경로**로 로컬/외부가 모두 호출된다 (이게 §10.4 3-티어 라우팅의 전제)
- [ ] 응답 사용량(usage) 토큰 수를 읽을 수 있다 — 비용 계측에 필수
- [ ] 캐시 적중 토큰 수(`cached_tokens`)를 읽을 수 있다

### T0-3. 프롬프트 프리픽스 조립 (1일)

**§01 문서 §10의 규칙을 그대로 따른다.** 특히 토큰 수를 4,200~4,500으로 맞춘다.

이 시점에 `actions.json`은 완성본이 아니어도 된다. **액션 12개 정도의 축소판**으로 시작하고, 부족한 토큰은 설명을 늘려 채운다. 중요한 건 길이와 불변성이지 완성도가 아니다.

```csharp
var prefix = PromptPrefix.Build(actionsJson, dslSummary, fewShots);
Console.WriteLine($"prefix tokens = {prefix.TokenCount}, sha = {prefix.Sha256[..12]}");
// 목표: 4200 <= TokenCount <= 4500
```

체크:
- [ ] 1,000회 조립해도 SHA-256이 동일하다
- [ ] 토큰 수 ≥ 4,096

### T0-4. 강제 디코딩 100회 검증 (1일)

```csharp
var schema = File.ReadAllText("plan.schema.json");
var opts = new ChatOptions {
    ResponseFormat = ChatResponseFormat.ForJsonSchema(schema, "NpcPlan"),
    Temperature = 0.4f,
};

int valid = 0, parseFail = 0, schemaFail = 0;
for (int i = 0; i < 100; i++)
{
    var r = await client.GetResponseAsync([prefixMsg, MakeSuffix(i)], opts);
    if (TryValidate(r.Text, out var err)) valid++;
    else if (err.Stage == ValidationStage.Schema) schemaFail++;
    else parseFail++;
}
```

**서픽스 100종**은 (아키타입 4 × 시간대 6 × 지역상태 4) 조합에서 샘플링한다. 같은 서픽스를 100번 던지면 의미가 없다.

체크:
- [ ] 로컬 dotLLM: 유효 JSON 100/100
- [ ] 외부 API 3종: 각 100/100
- [ ] 미달 시 어떤 스키마 요소가 문제인지 기록 (`maxItems`? 중첩 `object`? `pattern`?)

### T0-5. 성능 실측 (1일) — **가장 중요한 작업**

```csharp
record Measurement(string Engine, string Model, int PrefixTok, int SuffixTok, int OutTok,
                   double PrefillMs, double DecodeMs, double TotalMs,
                   int CachedTok, double CostUsd);
```

측정 매트릭스:

| 엔진 | 모델 | 캐시 | 반복 |
|---|---|---|---|
| dotLLM | Qwen3 4B Q4_K | off / on | 30 |
| dotLLM | Qwen3 8B Q4_K | off / on | 30 |
| dotLLM | Phi-4-mini Q4_K | off / on | 30 |
| Ollama | Qwen3 8B Q4_K | off / on | 30 |
| 외부 | gpt-5-nano | off / on | 30 |
| 외부 | qwen3.5-flash | off / on | 30 |
| 외부 | claude-haiku-4.5 | off / on | 30 |

**캐시 on/off 구분법**: off는 프리픽스 끝에 난수 주석을 붙여 강제로 미적중을 만든다. on은 30회를 연속으로 던진다(TTL 내).

동시성 측정(외부만):

```csharp
foreach (int c in new[] { 1, 8, 16, 32, 64 })
{
    var sw = Stopwatch.StartNew();
    await Parallel.ForEachAsync(Enumerable.Range(0, 128),
        new ParallelOptions { MaxDegreeOfParallelism = c },
        async (i, ct) => await client.GetResponseAsync(...));
    Console.WriteLine($"concurrency={c} → {128 / sw.Elapsed.TotalSeconds:F1} req/s");
}
```

체크:
- [ ] 429(rate limit) 발생 지점 기록 → §14 문서의 AIMD 초기값으로 사용
- [ ] 로컬은 동시 요청을 늘려도 처리량이 안 오르는 것을 **직접 확인**한다 (배칭 부재 검증)

### T0-6. 품질 수동 평가 (1일)

같은 서픽스 20건을 엔진별로 던지고, **눈으로 채점**한다.

| 항목 | 배점 | 기준 |
|---|---|---|
| 상황 반영 | 3 | 시간대/지역상태가 플랜에 실제로 반영되었나 |
| 액션 적절성 | 3 | 아키타입에 맞는 액션을 골랐나 |
| 정합성 | 2 | 전제조건 순서가 맞나 (검증기 3단 통과) |
| 다양성 | 2 | 버킷이 다르면 플랜도 다른가 (전부 같으면 0점) |

**"다양성" 항목이 핵심이다.** 모든 버킷에서 똑같은 플랜이 나오면 이 프로젝트 전체가 무의미하다. 여기서 0점이 나오면 W1을 연장해서 프롬프트를 고쳐야 한다.

---

## 3. 산출물

### 숫자 6개 (`docs/measurements/W1_results.md`)

| # | 항목 | 목표 | 실측 |
|---|---|---|---|
| M1 | 로컬 요청당 지연 (프리픽스 캐시 적중, 8B) | ~1.75s | |
| M2 | 로컬 prefill / decode 분리 시간 | 0.25s / 1.5s | |
| M3 | 프롬프트 캐시 적중 시 prefill 감소율 | ≥ 70% | |
| M4 | 외부 API 동시성별 처리량 (1/8/16/32/64) | 32에서 16 req/s | |
| M5 | 요청당 실비용 (모델 5종) | §10.2 표 대조 | |
| M6 | 모델별 품질 점수 (10점 만점, 20건 평균) | 4B ≥ 8B의 80% | |

### 갱신되는 문서

- `LLM_NPC_Server_Plan.md` §2.1, §10.1, §10.2 — 실측치로 교체
- `docs/01_MasterData_Spec.md` §10.1 — 프리픽스 토큰 예산 확정
- `docs/03_PlanDSL_Spec.md` §2 — 강제 디코딩에서 문제된 스키마 요소 제거

### 확정되는 결정

- [ ] **로컬 모델 확정** (4B vs 8B)
- [ ] **외부 모델 확정** (프리베이크용 / 런타임용, 다를 수 있음)
- [ ] **프리픽스 토큰 수 확정**
- [ ] **아키타입 수 조정 여부** — 품질이 낮으면 40 → 25로 줄이고 파라미터로 흡수
- [ ] **드라이런 샘플링률 초기값**

---

## 4. 이 주에 하지 않는 것

- 마스터데이터 정식 작성 (축소판만)
- 검증기 3·4단 구현 (1·2단만)
- `Npc.*` 프로젝트 구조 생성 — **전부 `spike/` 한 폴더에 던진다**
- 어떤 형태의 추상화나 인터페이스

W1 코드는 W2에 전부 버린다. 남는 것은 측정값과 결정뿐이다.

---

## 5. 위험 신호와 대응

| 신호 | 의미 | 대응 |
|---|---|---|
| 강제 디코딩이 90% 미만 | 스키마가 너무 복잡 | `maxItems`/`pattern`/중첩 제거. `args`를 평탄화 |
| 캐시 적중률이 0 | 프리픽스가 매번 다르거나 임계 미달 | SHA 로그 확인. 토큰 수 확인 |
| 로컬 지연이 5초 초과 | GPU 오프로딩 실패 또는 모델 과대 | `--gpu-layers` 확인. 모델 축소 |
| 모든 버킷에서 동일 플랜 | 서픽스가 모델에 안 먹힘 | 서픽스를 프롬프트 **끝**에 두고, 명시적 지시문 추가. temperature 상향 |
| 외부 API가 로컬보다 느림 | 네트워크 또는 리전 문제 | 동시성으로 흡수. 단건 지연은 원래 로컬이 빠를 수 있다 |
