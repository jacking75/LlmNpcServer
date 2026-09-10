# 폴백 플랜 작성

**아키타입마다 하나씩 반드시 있다** (V7). LLM 이 전부 죽어도 NPC 가 사는 이유가 이것이다.

## 전제

- 그 아키타입의 `allowed_actions` 가 정해져 있다.
- 일터·집 POI 가 있다 (`$workplace`·`$home` 이 바인딩돼야 한다).

## 순서

1. `masterdata/fallback_plans.json` 에 넣는다 (`npc-fallback` 스니펫).
2. **규칙**
   - `loop: true` — 폴백은 하루가 돌아야 한다.
   - `on_step_fail: "skip"` — 최후 보루라 멈추면 갈 곳이 없다.
   - 스텝 **3~10개**.
   - **모든 스텝에 `timeout_s`.** 0 이면 안 된다.
   - **그 아키타입의 `allowed_actions` 만** 쓴다 (V8).
   - 장소를 요구하는 액션 앞에는 `MoveTo` 가 온다 — 그것이 `AtWorkplace` 를 세우는 유일한 수단이다.
   - **고리가 닫혀야 한다** — 마지막 상태가 첫 스텝의 전제를 만족해야 한다.
     안 그러면 두 바퀴째부터 매번 재계획이 걸린다.

## 확인

```
npc validate                              # V7 · V8
npc timeline archetype <id>               # 근무 시간과 스텝이 어긋나지 않는가
npc card archetype <id>                   # "폴백 하루" 절의 스텝 트레이스
```

카드의 스텝 표에서 **판정 열이 전부 ✓ 여야 한다.** ✗ 가 있으면 그 자리에 검증 코드와
이유가 적혀 있다 — 앞 스텝의 `grants` 로 전제가 충족되지 않았다는 뜻이다.

## 되돌리기

```
git restore masterdata/fallback_plans.json
```

## 파급

| 무엇 | 범위 |
|---|---|
| 플랜 | **없음** — 폴백은 별도 저장이다 |
| 프리픽스 | 안 바뀐다 |
| 재기동 | 필요하다 (기동 시 1회 로드) |
