# 아키타입 추가

**F-05 이후 코드는 고치지 않는다.** 아키타입 수는 `archetypes.json` 이 정한다.

## 전제

- 일터가 될 POI 가 있거나, 같이 만든다 (`add-item-poi.md`).
- 인구를 어디서 뗄지 정해져 있다 — **세계관 결정이라 도구가 대신 정하지 않는다.**

## 순서

1. **초안과 파급을 먼저 본다.** 기본이 dry-run 이다.
   ```
   npc scaffold archetype beekeeper --from shepherd --weight 0.004
   ```
   재배분 3안(최대 인구·같은 계열·균등)과 결과 인구표, 파급표가 나온다.
2. 안을 고르고 적용한다. 기본은 첫 안(Largest)이다.
   ```
   npc scaffold archetype beekeeper --from shepherd --weight 0.004 --apply
   ```
3. **`desc` 를 쓴다.** 도구는 `TODO:` 를 넣어 둔다 —
   **프롬프트에 실리는 문장**이라 사람이 쓴다.
4. `allowed_actions` 를 정한다. 베낀 아키타입 것이 그대로 들어와 있다.
   - `Guard`·`Patrol` 을 넣으려면 **`duty_hours` 가 있어야 한다** (V12).
5. 일터 POI 를 만든다. **정원이 인구 이상**이어야 한다 (V10).
6. 폴백 플랜을 쓴다 (`write-fallback-plan.md`). 없으면 기동 실패다 (V7).
7. `context_buckets.json` 의 `total_keys` 를 아키타입 수 × 72 로 (V6).
8. 파생물을 다시 만든다.
   ```
   npc regen                       # 무엇을 돌려야 하는지 알려 준다
   dotnet run tools/gen_npcs.cs
   ```

## 확인

```
npc validate                       # V5(가중치 합) · V7(폴백) · V10(정원) · V12(근무)
npc card archetype beekeeper       # 정의 한 장 — 걸릴 인터럽트까지
npc timeline archetype beekeeper   # 근무 시간과 폴백이 어긋나지 않는가
.\build.ps1
```

> **`--npcs 200` 으로는 새 직업이 한 명도 안 뜬다.** `gen_npcs` 가 code 순서로 배치하므로
> 맨 뒤 아키타입은 첨자도 맨 뒤다. `npc card roster beekeeper` 가 구간을 알려 준다.

## 되돌리기

```
git restore masterdata/
```

## 파급

| 무엇 | 범위 |
|---|---|
| 플랜 | **전량 무효** — 프리픽스가 바뀐다 |
| 프리픽스 | **바뀐다** (아키타입 카탈로그가 실린다) → 프리베이크 필요 |
| 구조 해시 | **바뀐다** → 게임서버 재배포 (핸드셰이크 거절) |
| 코드 | **없음** (F-05) |
