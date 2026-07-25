# P0 (W1) 작업 지시서 — 스파이크

사양: [`10_Phase0_W1_Spike.md`](10_Phase0_W1_Spike.md) · 규약: [`../TASKS.md`](../TASKS.md)

> **이 Phase의 코드는 W2에 전부 버린다.** 남는 것은 측정값과 결정뿐이다.
> 따라서 `spike/` 한 폴더에 던지고, 추상화·인터페이스·테스트 커버리지를 신경 쓰지 않는다.
> 예외적으로 이 Phase만 "완료 조건에 테스트" 규칙을 면제한다 — 대신 **측정 결과 파일**이 완료 조건이다.

---

## A. 환경 구축

**T0-01** 스파이크 프로젝트 생성 · `S` · 선행 —
  파일 `spike/Spike.csproj` (신규), `spike/Program.cs` (신규)
  사양 `docs/10 §2 T0-2`
  내용 net10.0 콘솔. `Microsoft.Extensions.AI`, `Microsoft.Extensions.AI.OpenAI`, `OpenAI` 패키지 참조.
  완료 `dotnet run --project spike` 가 "spike ready" 출력

**T0-02** dotLLM 기동 + 스모크 · `S` · 선행 —
  파일 `spike/run-dotllm.ps1` (신규), `docs/measurements/W1_env.md` (신규)
  사양 `docs/10 §2 T0-1`
  내용 릴리스 바이너리 다운로드 경로, `serve` 실행 스크립트, `--gpu-layers` 설정. VRAM 점유를 `nvidia-smi`로 확인.
  완료 `/v1/models` 200 응답 · SSE 스트리밍 확인 · `W1_env.md`에 GPU 모델·VRAM·드라이버·dotLLM 버전 기록

**T0-03** `IChatClient` 이중 배선 · `S` · 선행 T0-01, T0-02
  파일 `spike/Clients.cs` (신규)
  사양 `docs/10 §2 T0-2`
  내용 로컬(`http://localhost:8080/v1`)과 외부(OpenAI 호환)를 **동일한 `IChatClient` 코드 경로**로 호출. usage 토큰과 `cached_tokens`를 읽을 수 있어야 한다.
  완료 같은 함수에 클라이언트만 바꿔 넣어 양쪽 응답 · 응답당 (prompt/cached/completion) 토큰 수 콘솔 출력

---

## B. 프롬프트 · 스키마

**T0-04** 축소 마스터데이터 · `M` · 선행 —
  파일 `spike/data/actions.min.json` (신규), `spike/data/flags.min.json` (신규)
  사양 `docs/01 §1, §2`
  내용 액션 **12개**, 플래그 **16개**로 축소한 초안. 정식판은 P1에서 만든다. 구조와 필드만 정식과 동일하게.
  완료 파일 존재 · 액션의 requires/grants가 전부 flags.min에 존재

**T0-05** 플랜 스키마 생성기 · `M` · 선행 T0-04
  파일 `spike/SchemaGen.cs` (신규), `spike/out/plan.schema.json` (산출)
  사양 `docs/03 §2`
  내용 `actions.min.json` → JSON Schema. `action` 열거값은 생성한다. `oneOf`/중첩 3단 이상 금지.
  완료 생성된 스키마가 `docs/03 §2` 예시 플랜을 통과시킴

**T0-06** 프롬프트 프리픽스 조립기 · `M` · 선행 T0-04, T0-05
  파일 `spike/PromptPrefix.cs` (신규), `spike/data/system_rules.md` (신규), `spike/data/fewshot/*.json` (신규 3건)
  사양 `docs/01 §10`
  내용 시스템 규칙 + 액션 카탈로그 렌더링 + DSL 요약 + few-shot 3건 → 단일 문자열. SHA-256과 토큰 수 계산. 토큰 수가 4,200 미만이면 액션 설명을 늘려 채운다.
  완료 1,000회 조립 시 SHA 동일 · 토큰 수 4,200~4,500 콘솔 출력

**T0-07** 서픽스 생성기 (버킷 샘플 100종) · `S` · 선행 T0-04
  파일 `spike/SuffixGen.cs` (신규)
  사양 `docs/12 §3`
  내용 (아키타입 4 × 시간대 6 × 지역상태 4)에서 100종 샘플링. **같은 서픽스를 반복하지 않는다** — 반복하면 측정이 무의미하다.
  완료 100종이 서로 다름 · 각 토큰 수 ≤ 300

---

## C. 검증 (게이트 G0-1)

**T0-08** 검증기 1·2단 (축소판) · `M` · 선행 T0-05
  파일 `spike/Validate.cs` (신규)
  사양 `docs/03 §3` 1·2단
  내용 파싱 → JSON Schema → 액션 존재/파라미터 타입/POI 심볼 확인. 실패 코드를 `V1.*`/`V2.*`로 반환.
  완료 정상 플랜 통과 · 액션명 오타·미정의 POI·필수 인자 누락 각각에 대해 올바른 코드 반환

**T0-09** 강제 디코딩 100회 러너 · `M` · 선행 T0-03, T0-06, T0-07, T0-08
  파일 `spike/RunSchemaCheck.cs` (신규), `docs/measurements/W1_schema.md` (산출)
  사양 `docs/10 §2 T0-4`
  내용 엔진 4종(dotLLM, Ollama, 외부 2종) × 서픽스 100종. 유효/파싱실패/스키마실패 집계. 실패 시 **어떤 스키마 요소가 문제였는지** 기록.
  완료 **게이트 G0-1** — 전 엔진 유효 JSON 100/100. 미달 시 문제 요소를 `W1_schema.md`에 명시하고 T0-05로 되돌아감

---

## D. 성능 측정 (게이트 G0-2, G0-3, G0-4)

**T0-10** 성능 측정 하네스 · `L` · 선행 T0-09
  파일 `spike/Bench.cs` (신규), `docs/measurements/W1_perf.csv` (산출)
  사양 `docs/10 §2 T0-5`
  내용 §T0-5 매트릭스(엔진 7종 × 캐시 on/off × 30회). prefill/decode 분리 계측. 캐시 off는 프리픽스 끝에 난수 주석을 붙여 강제 미적중.
  완료 **게이트 G0-2** 요청당 지연이 1.75s ±50% 이내 · **게이트 G0-3** 캐시 적중 시 prefill 70% 이상 감소 · CSV에 `Engine,Model,Cache,PrefixTok,SuffixTok,OutTok,PrefillMs,DecodeMs,TotalMs,CachedTok,CostUsd` 30행 × 14조합

**T0-11** 동시성 측정 (외부 API) · `M` · 선행 T0-10
  파일 `spike/BenchConcurrency.cs` (신규), `docs/measurements/W1_concurrency.md` (산출)
  사양 `docs/10 §2 T0-5`
  내용 동시 1/8/16/32/64로 각 128건. req/s와 429 발생 지점 기록. **로컬은 동시성을 늘려도 처리량이 안 오르는 것을 직접 확인**한다(배칭 부재 검증).
  완료 동시성별 req/s 표 · 429 최초 발생 동시성 기록(→ T3-12 AIMD 초기값) · 로컬 동시 1 대비 8의 처리량 비 기록

**T0-12** 품질 수동 평가 · `M` · 선행 T0-09
  파일 `docs/measurements/W1_quality.md` (신규)
  사양 `docs/10 §2 T0-6`
  내용 서픽스 20건 × 엔진 4종. 상황반영3/액션적절성3/정합성2/**다양성2** 채점.
  완료 **게이트 G0-4** 4B가 8B의 80% 이상 · **다양성 항목이 0이 아님**. 다양성 0이면 W1 연장하고 프롬프트 수정

---

## E. 결정 · 반영

**T0-13** W1 결과 정리 및 상위 문서 갱신 · `M` · 선행 T0-10, T0-11, T0-12
  파일 `docs/measurements/W1_results.md` (신규), `../LLM_NPC_Server_Plan.md` (수정), `../CLAUDE.md` (수정), `01_MasterData_Spec.md` (수정), `03_PlanDSL_Spec.md` (수정)
  사양 `docs/10 §3`
  내용 숫자 M1~M6 표를 실측으로 채운다. 상위 계획 §2.1·§10.1·§10.2, `CLAUDE.md` §9 배경 수치, `docs/01 §10.1` 프리픽스 예산, `docs/03 §2` 스키마를 실측에 맞춰 갱신. `../TASKS.md` §3 "사양 변경 이력"에 기록.
  완료 M1~M6 전부 실측값 · 아래 5개 결정이 문서에 명시됨

```
[ ] 로컬 모델 확정 (4B vs 8B)
[ ] 외부 모델 확정 (프리베이크용 / 런타임용)
[ ] 프리픽스 토큰 수 확정
[ ] 아키타입 수 유지(40) 또는 축소(25)
[ ] 드라이런 샘플링률 초기값
```

---

## 진행 체크리스트

```
[x] T0-01  스파이크 프로젝트 생성
[x] T0-02  dotLLM 기동 + 스모크
[x] T0-03  IChatClient 이중 배선
[x] T0-04  축소 마스터데이터
[ ] T0-05  플랜 스키마 생성기
[ ] T0-06  프롬프트 프리픽스 조립기
[ ] T0-07  서픽스 생성기
[ ] T0-08  검증기 1·2단 (축소판)
[ ] T0-09  강제 디코딩 100회 러너        ★ 게이트 G0-1
[ ] T0-10  성능 측정 하네스              ★ 게이트 G0-2, G0-3
[ ] T0-11  동시성 측정
[ ] T0-12  품질 수동 평가                ★ 게이트 G0-4
[ ] T0-13  결과 정리 및 상위 문서 갱신
```

**게이트 4개를 전부 통과해야 P1로 넘어간다.** 미통과 시 대응은 `docs/10 §5` 위험 신호 표를 따른다.
