# 블라인드 A/B 평가 — 정답 키

> `tools/gen_blind_eval.cs` 가 만든다. **참가자에게 주지 않는다.**
> 자료 자체는 `artifacts/blind_eval/` 에 있고 그쪽은 `.gitignore` 대상이다.

| 배치 시드 | `20260727` |
|---|---|
| 표본 | 20쌍 = 40건 |
| 모집단 | 200 (호스트 `--npcs`) |
| 배속 | 600 |
| A군 기록 | `run_a.jsonl` (프리베이크 플랜) |
| B군 기록 | `run_b.jsonl` (아키타입 폴백 = 사람이 짠 플랜) |

## 배치

| 제시번호 | 군 | NPC | 아키타입 | 지역상태 | 기후 |
|---|---|---|---|---|---|
| 01 | A | #2076 | innkeeper | Peace | Fair |
| 02 | B | #2151 | stablemaster | Peace | Fair |
| 03 | A | #1751 | shepherd | Peace | Cold |
| 04 | B | #2076 | innkeeper | Peace | Fair |
| 05 | A | #726 | farmer | Peace | Fair |
| 06 | A | #401 | mason | Peace | Fair |
| 07 | A | #1 | blacksmith | Peace | Fair |
| 08 | B | #1176 | fisher | Peace | Fair |
| 09 | B | #326 | jeweler | Peace | Fair |
| 10 | A | #1476 | woodcutter | Alert | Fair |
| 11 | B | #1476 | woodcutter | Alert | Fair |
| 12 | A | #2151 | stablemaster | Peace | Fair |
| 13 | B | #1326 | hunter | Peace | Cold |
| 14 | B | #351 | tanner | Peace | Fair |
| 15 | A | #351 | tanner | Peace | Fair |
| 16 | B | #201 | alchemist | Peace | Fair |
| 17 | A | #1651 | herbalist | Peace | Fair |
| 18 | A | #451 | scribe | Peace | Fair |
| 19 | A | #1326 | hunter | Peace | Cold |
| 20 | B | #476 | miner | Alert | Cold |
| 21 | B | #1876 | merchant | Peace | Fair |
| 22 | A | #201 | alchemist | Peace | Fair |
| 23 | A | #301 | brewer | Peace | Fair |
| 24 | A | #476 | miner | Alert | Cold |
| 25 | A | #1876 | merchant | Peace | Fair |
| 26 | A | #1176 | fisher | Peace | Fair |
| 27 | A | #76 | carpenter | Peace | Fair |
| 28 | B | #126 | tailor | Peace | Fair |
| 29 | A | #226 | baker | Peace | Fair |
| 30 | B | #226 | baker | Peace | Fair |
| 31 | B | #1651 | herbalist | Peace | Fair |
| 32 | B | #301 | brewer | Peace | Fair |
| 33 | B | #1751 | shepherd | Peace | Cold |
| 34 | B | #1 | blacksmith | Peace | Fair |
| 35 | B | #76 | carpenter | Peace | Fair |
| 36 | A | #126 | tailor | Peace | Fair |
| 37 | B | #451 | scribe | Peace | Fair |
| 38 | B | #726 | farmer | Peace | Fair |
| 39 | B | #401 | mason | Peace | Fair |
| 40 | A | #326 | jeweler | Peace | Fair |

## 짝 매칭

같은 NPC 가 두 번 나온다 — A군과 B군에서 **같은 세계·같은 시각**을 살았고 플랜만 다르다.
아키타입·시간대·지역상태를 따로 맞출 필요가 없는 것은 그 때문이다.

| NPC | 아키타입 | A 제시번호 | B 제시번호 |
|---|---|---|---|
| #1 | blacksmith | 07 | 34 |
| #76 | carpenter | 27 | 35 |
| #126 | tailor | 36 | 28 |
| #201 | alchemist | 22 | 16 |
| #226 | baker | 29 | 30 |
| #301 | brewer | 23 | 32 |
| #326 | jeweler | 40 | 09 |
| #351 | tanner | 15 | 14 |
| #401 | mason | 06 | 39 |
| #451 | scribe | 18 | 37 |
| #476 | miner | 24 | 20 |
| #726 | farmer | 05 | 38 |
| #1176 | fisher | 26 | 08 |
| #1326 | hunter | 19 | 13 |
| #1476 | woodcutter | 10 | 11 |
| #1651 | herbalist | 17 | 31 |
| #1751 | shepherd | 03 | 33 |
| #1876 | merchant | 25 | 21 |
| #2076 | innkeeper | 01 | 04 |
| #2151 | stablemaster | 12 | 02 |
