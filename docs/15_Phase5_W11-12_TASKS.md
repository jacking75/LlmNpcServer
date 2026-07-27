# P5 (W11–12) 작업 지시서 — 검증 · 블라인드 평가 · 보고

사양: [`15_Phase5_W11-12_Verification.md`](15_Phase5_W11-12_Verification.md) · 규약: [`../TASKS.md`](../TASKS.md)

> **앞의 10주는 "만들 수 있는가"였고, 이 2주가 "만들 가치가 있는가"다.** 리스크 R1의 정산 시점이다.
>
> ⚠ **블라인드 평가의 예비 실시는 W8에 이미 했어야 한다.** 안 했다면 W11 시작 시점에 축약판(참가자 4명 × 20건)을 먼저 돌려서 방향을 확인한다. W12에 처음 알게 되면 대응할 시간이 없다.

---

## A. 골든 회귀 테스트 (W11)

**T5-01** 골든 픽스처 포맷 + 단언 종류 · `M` · 선행 T2-13
  파일 `tests/Npc.Tests/Golden/GoldenFixture.cs`, `Assertions.cs` (신규)
  사양 `docs/15 §2`
  내용 `kind` **9개**: `validates`/`contains_action`/`not_contains`/`produces_item_of`/`ends_with_any`/`step_count_between`/`acquires_before_use`/`avoids_flag_while`/`differs_from`. (§2 표는 `contains_action`과 `not_contains`를 한 행에 묶어 8행으로 적혀 있다 — 구현해야 할 `kind` 값은 9개다.) **속성 단언만. 정확한 문자열 비교 금지.**
  내용 `validates` 의 `stage: DryRun` 은 T2-13의 4단 검증기를 부른다 — 구현체가 `Npc.Sim`에 있으므로 테스트가 그것을 주입한다.
  완료 9개 각각 통과/실패 케이스 테스트

**T5-02** 단언 평가기 · `M` · 선행 T5-01
  파일 `tests/Npc.Tests/Golden/AssertionEvaluator.cs` (신규)
  완료 픽스처 JSON → 단언 실행 → 결과 리포트

**T5-03** 골든 픽스처 50건 작성 · `L` · 선행 T5-02
  파일 `tests/golden/*.json` (신규 50건)
  사양 `docs/15 §2`
  내용 아키타입·시간대·지역상태를 고르게 덮는 50건. **`differs_from` 단언(다양성)을 최소 10건에 포함.**
  완료 50건 · 아키타입 20종 이상 커버 · 4개 지역상태 전부 포함

**T5-04** 골든 러너 · `M` · 선행 T5-03
  파일 `tests/Npc.Tests/Golden/GoldenRunner.cs` (신규)
  사양 `docs/15 §2`
  내용 50건 × 3회 생성. **3회 중 2회 이상 통과를 합격**으로. 1회 판정은 산발적으로 깨져서 아무도 안 믿게 된다.
  내용 `[Trait("Category", "Golden")]` 을 단다 — CI 기본은 `--filter Category!=Golden` 이고 이 태스크가 그 필터에 걸리는 **첫 테스트**다. 필터가 실제로 먹는지 두 방향 다 확인한다.
  완료 `Category=Golden` 테스트가 합격률 ≥ 90% · 실행 비용 기록 · `dotnet test --filter Category!=Golden` 이 이 테스트를 건너뜀

---

## B. 결정론 (W11)

**T5-05** 난수 봉인 감사 · `M` · 선행 T1-62
  파일 `tests/Npc.Tests/Determinism/RandomAuditTests.cs` (신규)
  사양 `docs/15 §3` · `../CLAUDE.md §2.3`
  내용 `Npc.Core`/`Runtime`/`Planning` 어셈블리에서 `System.Random` 사용 검출. 시드 없는 사용 0. **IL 스캔으로 한다.** T1-03의 `ArchitectureTests`는 csproj XML 을 읽는 방식이라 여기엔 못 쓰고, 소스 문자열 검색은 주석·문서 문자열까지 걸린다(이 저장소는 주석이 한국어로 길다).
  내용 `Npc.Sim`은 대상에서 뺀다 — 게임서버 대역이고 `SimOptions.Seed`로 이미 시드가 고정돼 있다. `Npc.Llm`도 뺀다(LLM 응답 자체가 비결정적이고, 리플레이는 기록된 플랜을 쓴다).
  완료 `Determinism_NoUnseededRandom` 통과 · 발견된 위반 전부 `(npcId, tick)` 해시(`PlanHash`)로 교체

**T5-06** 시간 봉인 감사 · `S` · 선행 T5-05
  파일 `tests/Npc.Tests/Determinism/ClockAuditTests.cs` (신규)
  사양 `../CLAUDE.md §2.3`
  내용 게임 로직 어셈블리에서 `DateTime.Now`/`UtcNow`/`Stopwatch`/`Guid.NewGuid` 검출.
  내용 P1 시점의 현황: 게임 로직(`Core`/`Runtime`/`Planning`/`MasterData`/`Contracts`/`Sim`)에는 **하나도 없다.** 전부 `Npc.Host`에 있다 — `NpcMeter`(틱 지연 계측 3곳)와 `Program.PumpAsync`(10Hz 페이싱 1곳). **화이트리스트는 어셈블리 단위로 `Npc.Host` 하나만** 둔다. 개별 파일을 예외로 뚫기 시작하면 감사가 무의미해진다.
  완료 `Determinism_NoWallClock` 통과 · 화이트리스트가 `Npc.Host` 단 하나

**T5-07** 리플레이 일치 테스트 · `L` · 선행 T5-06, T1-43, T1-44
  파일 `tests/Npc.Tests/Determinism/ReplayTests.cs` (신규)
  사양 `docs/15 §3`
  내용 Recording으로 실행 → Replay로 재생 → 명령 jsonl **바이트 동일**. LLM은 재생 시 미호출(기록된 플랜 사용).
  완료 `Determinism_ReplayMatchesByteForByte` 통과 (시나리오 A 1게임일 기준) · 불일치 시 최초 divergence 틱을 리포트

---

## C. 장애 주입 · 링크 교체 (W11)

**T5-08** `KillSwitch` 이벤트 처리 · `S` · 선행 T1-52, T4-07
  파일 `src/Npc.Sim/ScenarioRunner.cs` (수정), `src/Npc.Llm/TieredPlanCompiler.cs` (수정), `src/Npc.Host/Program.cs` (수정)
  사양 `docs/02 §5` · `docs/15 §4`
  내용 T1-52가 이미 파싱·기록까지 한다 — `ScenarioStep.KillSwitchTarget` 과 `KillSwitchesFired` 목록이 있다. **없는 것은 그 신호를 실제 티어에 연결하는 배선이다.** P1 에는 끊을 LLM 이 없어서 발생 사실만 남겼다.
  내용 `ScenarioRunner`의 주석은 타깃을 `"T1"/"T2"` 로만 적고 있는데 시나리오 C 는 `PlanStore` 도 쓴다 — **3종을 처리**하고 문자열 타깃을 열거형으로 바꾼다.
  완료 3종 타깃 각각 비활성 확인 · 미지의 타깃 문자열은 로드 시점에 기동 실패

**T5-09** 시나리오 C 통과 · `M` · 선행 T5-08
  파일 `scenarios/blackout.jsonl` (신규), `tests/Npc.Tests/Scenarios/BlackoutTests.cs` (신규)
  사양 `docs/15 §4` · `docs/00 §2` 시나리오 C
  완료 3단계 전부

```
[ ] T2 차단 → T1 페일오버, 지연 상승하나 동작 유지
[ ] T1까지 차단 → 캐시 플랜만으로 5,000 NPC 정상 동작
[ ] PlanStore까지 차단 → 폴백 40개만으로 정상 동작
[ ] 세 단계 모두 크래시 0 · 틱 시간 예산 내
```

**T5-10** 링크 장애 주입 · `M` · 선행 T5-09
  파일 `tests/Npc.Tests/FaultInjection/LinkFaultTests.cs` (신규)
  사양 `docs/15 §4` 추가 장애 목록
  완료 5항목

```
[ ] 링크 단절(Faulted) → 명령 축적 → 복구 시 재개
[ ] 이벤트 시퀀스 갭 주입 → 경보 발생, 동작 유지
[ ] 명령 드롭율 5% → 타임아웃 합성으로 진행 유지
[ ] 외부 API 429 폭주 → AIMD 감속, 크래시 없음
[ ] 외부 API 타임아웃 30초 → 서킷 브레이커 개방
```

**T5-11** 링크 4종 교체 검증 · `M` · 선행 T5-07, T1-45
  파일 `tests/Npc.Tests/Gates/LinkSwapTests.cs` (신규)
  사양 `docs/15 §5` · `docs/02 §6`
  내용 Null/Loopback/Recording/Replay 4종으로 같은 시나리오 실행. **P1 게이트에서 이미 4종 기동은 확인됐다**(`P1_gate.md` #6) — 여기서 더 하는 것은 "같은 시나리오에서 결과가 같은가"다.
  완료 `Link_Swappable` 통과 · **`src/Npc.Runtime`, `src/Npc.Core`, `src/Npc.Planning` 변경 diff = 0** (`--link` 로만 갈아끼운다) · `TcpGameServerLink`가 컴파일됨

---

## D. 블라인드 평가 (W12)

**T5-12** `Npc.Narrate` — 로그 → 서술 · `M` · 선행 T5-07
  파일 `tools/Npc.Narrate/Program.cs`, `tools/Npc.Narrate/Npc.Narrate.csproj` (신규), `NpcServer.sln` (수정)
  사양 `docs/15 §6` 서술 변환 예시
  내용 명령 로그 → 사람이 읽는 하루 일지. **액션 시퀀스의 기계적 번역이므로 LLM을 쓰지 않는다.** `Npc.Contracts`·`Npc.MasterData` 참조가 필요하므로 파일 기반 앱이 아니라 프로젝트로 만든다(`tools/Npc.Prebake`와 같은 취급).
  내용 서술에 **틱 번호·상관 ID·플랜 id 를 남기지 않는다** — 그 자체가 A/B 를 가르는 단서가 된다.
  완료 §6 예시 형식으로 출력 · 어느 쪽(LLM/사람)이 만든 플랜인지 서술에서 드러나지 않음

**T5-13** 평가 자료 생성 · `M` · 선행 T5-12
  파일 `tools/gen_blind_eval.cs` (신규 — 파일 기반 앱), `artifacts/blind_eval/*.md` (산출 40건)
  사양 `docs/15 §6`
  내용 A군(LLM 플랜) 20 + B군(폴백=사람 플랜) 20. **아키타입·시간대·지역상태를 짝지어 매칭.** 제시 순서 무작위, 정답 키는 별도 파일.
  내용 **제시 순서 무작위화도 시드 고정이다** — 평가를 다시 돌리거나 참가자를 추가할 때 같은 배치를 재현해야 한다. `artifacts/`는 `.gitignore` 대상이므로 정답 키와 배치 시드는 `docs/measurements/`에 남긴다.
  완료 40건 · 짝 매칭 검증 · 정답 키가 자료에 노출되지 않음

**T5-14** 응답 수집 포맷 · `S` · 선행 T5-13
  파일 `artifacts/blind_eval/response_form.md`, `docs/measurements/blind_eval_raw.jsonl` (신규)
  사양 `docs/15 §6`
  내용 Q1(강제 선택) · Q2(1~5) · 쌍대 비교. **참가자에게 "구분 불가도 성공"이라는 프레이밍을 미리 알리지 않는다**(편향 방지).
  완료 양식 배포 가능 상태 · 12명 × 40건 수집 계획 수립

**T5-15** 통계 처리 · `M` · 선행 T5-14
  파일 `tools/analyze_blind_eval.cs` (신규 — 파일 기반 앱. 통계 라이브러리가 필요하면 `.py`도 허용하되 실행 방법을 파일 머리에 적는다), `docs/measurements/blind_eval_result.md` (산출)
  사양 `docs/15 §6` 판정 기준 · 통계 처리
  내용 Q1 이항검정(H0=0.5) · Q2 짝지은 t검정 + Cohen's d · 쌍대 부호검정. **95% 신뢰구간 반드시 병기.**
  완료 n ≥ 480 · §6 판정표의 4분면 중 어디인지 명시 · 참가자 6명 미만이면 "결론 보류"로 표기

---

## E. 정산 · 보고 (W12)

**T5-16** 오써링 공수 정산 · `M` · 선행 T1-54, T3-18
  파일 `tools/calc_authoring.cs` (신규 — 파일 기반 앱), `docs/measurements/authoring_result.md` (산출)
  사양 `docs/15 §7`
  내용 분모는 `docs/measurements/authoring_time.jsonl` — T1-54에서 사람이 폴백 40개를 짜며 이미 기록해 뒀다. 분자는 `manifest.json.wall_clock_s` + `review_W8.jsonl`.
  내용 **두 축 모두 산출한다.**
   · 축 A(절감률) — LLM 경로 총공수(프롬프트 설계 + 프리베이크 + 검수 + 수정) vs 수작성 외삽
   · 축 B(커버리지 배수) — 같은 비용으로 얻는 결과물 (40개 → 2,880개)
  완료 두 축 모두 산출 · **프롬프트 설계 공수(W5~W6 실소요)를 분자에 포함** · "사람은 애초에 2,880개를 만들지 않는다"는 사실 명시

**T5-17** 비용 실측 정산 · `S` · 선행 T4-15
  파일 `docs/measurements/cost_actual.md` (산출)
  사양 `docs/15 §8` · `docs/measurements/W1_env.md`
  내용 프리베이크 1회 · 런타임 7게임일 · 월 환산 · 캐시 절감액 · T1 전력비.
  내용 **측정 기기를 같이 적는다.** W1 실측은 RTX 4060 **VRAM 8GB** 에서 나왔고, 8B Q4_K_M 가중치만 4,789 MiB 라 KV 캐시를 q8_0 로 눌러야 겨우 올라간다(`W1_env.md`). T1 전력비와 처리량 수치는 이 제약과 묶어서 읽어야 하고, 상용 환경 외삽은 그 사실을 밝히고 한다.
  완료 상위계획 §10.2 추정표와 나란히 배치 · **오차율 제시** · 측정 기기·VRAM 제약 명시

**T5-18** 버킷 히트맵 리포트 · `S` · 선행 T4-22
  파일 `docs/measurements/bucket_usage.md` (산출)
  사양 `docs/15 §9` 6번
  내용 2,880개 중 실제로 쓰인 버킷 수와 비율. **"다음 버전은 1/N 비용으로 만들 수 있다"는 발견.**
  완료 실사용 버킷 수 · 상위 20 버킷 · 미사용 버킷의 공통 패턴 분석

**T5-19** R&D 보고서 · `L` · 선행 T5-15, T5-16, T5-17, T5-18
  파일 `docs/RnD_Report.md` (신규)
  사양 `docs/15 §9` 목차
  내용 §9의 8개 절. **6번 "발견"이 제일 가치 있을 가능성이 높다** — 버킷 히트맵, 4B vs 8B 차이, dotLLM prefill 병목의 실제 영향.
  내용 6번에 W1 에서 이미 나온 발견 3건을 넣는다.
   · **강제 디코딩이 프롬프트 지시보다 나빴다.** 유효 JSON 은 99/100 인데 어휘 검증은 0/100 이었다 — 스키마를 만족시키면서 인자를 지어낸다. "strict 모드면 안전하다"는 통념의 반례다
   · **로컬 지연이 계획 가정의 2~3배다** (1.75s → 4B 3.7s · 8B 5.1s). 원인의 큰 부분은 VRAM 8GB 제약이다
   · **프리픽스 캐시가 prefill 을 85~88% 줄였다** (1,531 → 237ms · 2,608 → 316ms). 이 설계 판단은 실측으로 옳았다
  완료 8개 절 전부 · 요약 1페이지가 판정과 권고를 담음 · 7번에 상용 전환 시 남는 과제 4항목

**T5-20** 최종 게이트 체크 · `M` · 선행 T5-19
  파일 `docs/00_Deliverables.md` (수정 — 수용 기준 체크)
  사양 `docs/15 §10` · `docs/00 §4`
  완료 아래 + `docs/00 §4`의 전 항목

```
[ ] 골든 50건 합격률 ≥ 90%
[ ] 결정론 리플레이 100% 일치
[ ] 시나리오 A / B / C 전부 통과
[ ] 링크 4종 교체 시 런타임 코드 diff = 0
[ ] 블라인드 평가 n ≥ 480, 신뢰구간 포함 결과 산출
[ ] 오써링 공수 축 A·축 B 양쪽 산출
[ ] 비용 실측과 추정의 오차율 제시
[ ] docs/00 §4 수용 기준 전 항목 체크 완료
[ ] 보고서 작성 완료
```

---

## 진행 체크리스트

```
W11 ── 골든 · 결정론 · 장애
[x] T5-01 픽스처포맷+단언8종 [ ] T5-02 단언평가기      [ ] T5-03 픽스처50건 ★
[ ] T5-04 골든러너(3중2)     [ ] T5-05 난수봉인감사    [ ] T5-06 시간봉인감사
[ ] T5-07 리플레이일치 ★     [ ] T5-08 KillSwitch      [ ] T5-09 시나리오C ★
[ ] T5-10 링크장애주입       [ ] T5-11 링크4종교체 ★

W12 ── 평가 · 정산 · 보고
[ ] T5-12 Narrate            [ ] T5-13 평가자료40건    [ ] T5-14 응답수집양식
[ ] T5-15 통계처리 ★         [ ] T5-16 오써링정산 ★    [ ] T5-17 비용정산
[ ] T5-18 버킷히트맵리포트   [ ] T5-19 R&D보고서 ★     [ ] T5-20 최종게이트
```

★ T5-15와 T5-16이 이 프로젝트의 결론이다. 나머지는 그 결론의 신뢰도를 담보하는 작업이다.

---

## 결과가 나쁘게 나왔을 때

`docs/15 §11`과 상위계획 §17을 참조한다. 요지는 하나다.

> **최악의 경우에도 버려지는 것은 LLM 부분뿐이고, 마스터데이터 스키마·결정론 NPC 런타임·인지 LOD 스케줄러·게임서버 링크·Sim은 그대로 남는다.**

블라인드에서 "구분 불가"가 나오면 **실패가 아니다.** 사람이 만든 것과 구분이 안 되는 것을 2,880개를 3분에 만들었다면, 보고서의 결론은 §7의 축 B(커버리지 배수)로 쓴다.
