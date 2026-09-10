# "NPC 가 왜 저기 있나"

## 순서

1. **지금 상태를 본다.**
   ```
   curl http://localhost:5080/npc/1247
   ```
   플랜 id · 출처 · 현재 스텝 · 스텝 상태 · 플래그가 나온다.

2. **그 NPC 가 누구인지 본다.**
   ```
   npc card npc 1247            # 아키타입 · 집 · 일터 · 거리 · 근무 허가
   ```

3. **어떤 플랜을 도는지 본다.** 출처가 넷이다.

   | 출처 | 뜻 | 어디 |
   |---|---|---|
   | `Prebaked` | 미리 만든 버킷 플랜 | `planstore/plans/<버킷>.json` |
   | `Pinned` | 사람이 고친 것 | `planstore/pinned/` |
   | `Fallback` | 버킷이 비어 폴백으로 | `masterdata/fallback_plans.json` |
   | `Runtime` | 런타임에 만든 개별 플랜 | 로그 |

4. **그 플랜을 읽는다.**
   ```
   npc plan explain planstore/plans/<버킷>.json
   ```
   스텝마다 **전제가 앞 스텝의 어느 `grants` 로 충족됐는지** 나온다.
   ✗ 가 있으면 그 자리가 원인이다.

5. **인터럽트에 걸린 것은 아닌가.**
   ```
   npc card archetype <id>       # "걸릴 수 있는 인터럽트" 절
   npc explain interrupt         # 우선순위 순서
   ```
   밤에 집으로 가는 것은 대개 `go_home_at_night` 이다.

6. **하루가 어떻게 도는지 본다.**
   ```
   npc timeline archetype <id>
   ```

7. **기록이 있으면 일지로 읽는다.**
   ```
   dotnet run --project tools/Npc.Narrate -- --trace logs/link.jsonl --npc 1247
   ```

## 흔한 원인

| 증상 | 원인 |
|---|---|
| 계속 같은 자리에 있다 | 스텝 전제가 안 서서 타임아웃만 반복. `plan explain` 의 ✗ |
| 밤에 일터에 있다 | `duty_hours` 와 폴백 스텝이 어긋났다. `timeline` |
| 엉뚱한 POI 로 간다 | 거리표가 낡았다. `npc regen --check` |
| 새 직업이 안 보인다 | `--npcs` 가 작다. `npc card roster <id>` 의 첨자 구간 |
| 아무것도 안 한다 | 플랜이 폴백인데 폴백도 전제가 안 선다. `npc card` 의 폴백 절 |

## 확인

진단이 끝났다는 것은 **다음 세 가지를 말할 수 있다**는 뜻이다.

1. 플랜 출처 — `Prebaked` · `Pinned` · `Fallback` · `Runtime` 중 무엇인가
2. 현재 스텝과 그 스텝의 전제가 **어느 앞 스텝의 `grants` 로** 충족됐는가
3. 고칠 곳 — 마스터데이터인가, 플랜인가, 인터럽트인가

셋 중 하나라도 못 말하면 아직 원인을 모르는 것이다.

## 파급

진단은 아무것도 바꾸지 않는다. 고치는 것은 해당 레시피로 간다.
