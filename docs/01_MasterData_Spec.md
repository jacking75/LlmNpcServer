# 01. 마스터데이터 사양

> NPC 서버를 동작시키기 위한 정적 데이터 전체 정의.
> **원칙: 마스터데이터가 LLM 프롬프트의 단일 원천(Single Source of Truth)이다.** 액션 카탈로그에서 프롬프트 프리픽스를 *생성*한다. 둘이 어긋나면 검증 실패율이 즉시 치솟는다.

---

## 0. 파일 목록

```
masterdata/
  world_flags.json        월드 상태 비트 플래그 정의 (64bit)
  actions.json            액션 카탈로그 (37개)
  items.json              아이템/자원 (~80)
  zones.json              존 (12)
  pois.json               관심지점 (~250)
  poi_distances.bin       POI 간 거리 행렬 (사전계산, 250×250 float16)
  archetypes.json         NPC 아키타입 (40)
  context_buckets.json    시간대/지역상태/기후 정의 + 버킷 키 규칙
  interrupts.json         인터럽트 규칙
  fallback_plans.json     폴백 플랜 (수동 작성, 아키타입당 1개)
  npc_instances.json      NPC 인스턴스 (5,000, 생성 스크립트 산출물)
  prompt/
    system_rules.md       시스템 규칙 (프롬프트 프리픽스 1부)
    fewshot/*.json        few-shot 예시 (3개)
```

**로딩 규약**: 기동 시 전량 로드 → 스키마 검증 → 참조 무결성 검증 → 읽기 전용 인덱스로 컴파일 → 이후 불변. 검증 실패는 **기동 실패**로 처리한다 (경고 후 진행 금지).

---

## 1. `world_flags.json` — 월드 상태 비트 플래그

플랜의 전제조건·효과, 인지 스캔의 이탈 판정이 전부 이 64비트 위에서 돈다. **가장 먼저 확정해야 하는 파일이다.**

```jsonc
{
  "version": 1,
  "flags": [
    // --- 위치 (bit 0~7) ---
    { "bit": 0,  "id": "AtHome",           "desc": "자택에 있다" },
    { "bit": 1,  "id": "AtWorkplace",      "desc": "자신의 일터에 있다" },
    { "bit": 2,  "id": "AtMarket",         "desc": "시장에 있다" },
    { "bit": 3,  "id": "AtTavern",         "desc": "선술집에 있다" },
    { "bit": 4,  "id": "AtTemple",         "desc": "신전에 있다" },
    { "bit": 5,  "id": "AtGate",           "desc": "성문에 있다" },
    { "bit": 6,  "id": "AtField",          "desc": "농경지/채집지에 있다" },
    { "bit": 7,  "id": "InWilderness",     "desc": "마을 밖 야외에 있다" },

    // --- 소지품 (bit 8~15) ---
    { "bit": 8,  "id": "HasFood",          "desc": "식량을 가지고 있다" },
    { "bit": 9,  "id": "HasWater",         "desc": "물을 가지고 있다" },
    { "bit": 10, "id": "HasTool",          "desc": "직업 도구를 가지고 있다" },
    { "bit": 11, "id": "HasRawMaterial",   "desc": "가공 전 재료를 가지고 있다" },
    { "bit": 12, "id": "HasProduct",       "desc": "판매 가능한 완성품을 가지고 있다" },
    { "bit": 13, "id": "HasCoin",          "desc": "화폐를 가지고 있다" },
    { "bit": 14, "id": "InventoryFull",    "desc": "인벤토리가 가득 찼다" },
    { "bit": 15, "id": "HasWeapon",        "desc": "무기를 장비했다" },

    // --- 신체 (bit 16~21) ---
    { "bit": 16, "id": "IsRested",         "desc": "충분히 쉬었다" },
    { "bit": 17, "id": "IsHungry",         "desc": "배고프다" },
    { "bit": 18, "id": "IsThirsty",        "desc": "목마르다" },
    { "bit": 19, "id": "IsInjured",        "desc": "부상 상태 (HP < 50%)" },
    { "bit": 20, "id": "IsExhausted",      "desc": "탈진 상태 (스태미나 < 20%)" },
    { "bit": 21, "id": "IsSleeping",       "desc": "수면 중" },

    // --- 시간 (bit 24~27, 상호 배타) ---
    { "bit": 24, "id": "IsDawn",           "desc": "새벽" },
    { "bit": 25, "id": "IsDay",            "desc": "낮" },
    { "bit": 26, "id": "IsEvening",        "desc": "저녁" },
    { "bit": 27, "id": "IsNight",          "desc": "밤" },

    // --- 사회 (bit 28~31) ---
    { "bit": 28, "id": "HasCustomer",      "desc": "손님이 있다" },
    { "bit": 29, "id": "HasCompanion",     "desc": "동행 NPC가 있다" },
    { "bit": 30, "id": "IsAlone",          "desc": "주변에 아무도 없다" },
    { "bit": 31, "id": "OnDuty",           "desc": "근무 시간이다" },

    // --- 월드 (bit 32~39) ---
    { "bit": 32, "id": "ShopOpen",         "desc": "자신의 상점이 열려있다" },
    { "bit": 33, "id": "MarketOpen",       "desc": "시장이 열려있다" },
    { "bit": 34, "id": "GatesOpen",        "desc": "성문이 열려있다" },
    { "bit": 35, "id": "RegionPeaceful",   "desc": "지역이 평시다" },
    { "bit": 36, "id": "RegionUnderAttack","desc": "지역이 공격받고 있다" },
    { "bit": 37, "id": "WeatherHarsh",     "desc": "악천후다" },
    { "bit": 38, "id": "ResourceDepleted", "desc": "대상 자원이 고갈됐다" },
    { "bit": 39, "id": "PathBlocked",      "desc": "목표 경로가 막혔다" },

    // --- 위협 (bit 40~43) ---
    { "bit": 40, "id": "ThreatNearby",     "desc": "적대 대상이 근처에 있다" },
    { "bit": 41, "id": "PlayerNearby",     "desc": "플레이어가 인지 범위에 있다" },
    { "bit": 42, "id": "AllyNearby",       "desc": "아군이 근처에 있다" },
    { "bit": 43, "id": "InCombat",         "desc": "전투 중이다" }
  ]
}
```

총 **42개 플래그**를 정의하고, **bit 22~23 및 44~63은 예약**한다. 확장 시 기존 비트 번호를 절대 재배치하지 않는다 (프리베이크된 플랜이 깨진다).

```csharp
// Npc.Core/WorldFlags.cs — 소스 생성기로 world_flags.json에서 자동 생성
[Flags]
public enum WorldFlags : ulong
{
    None = 0,
    AtHome = 1UL << 0,
    AtWorkplace = 1UL << 1,
    // ... (생성됨)
}
```

**이탈 판정** (인지 스캔의 핵심 연산):

```csharp
// 전제 미충족 또는 금지 플래그 성립 → 재계획 필요
static bool IsDeviated(WorldFlags cur, WorldFlags required, WorldFlags forbidden)
    => (required & ~cur) != 0 || (forbidden & cur) != 0;
```

---

## 2. `actions.json` — 액션 카탈로그 (37개)

**LLM이 조합할 수 있는 유일한 원자 단위.** 이 파일이 곧 프롬프트 프리픽스의 본문이 된다.

### 2.1 스키마

```jsonc
{
  "version": 1,
  "actions": [
    {
      "id": "MoveTo",                    // ActionId — 플랜 DSL에서 이 문자열을 쓴다
      "code": 1,                         // 내부 ushort. 절대 재배치 금지
      "category": "movement",
      "desc": "지정한 POI로 이동한다",    // ← LLM 프롬프트에 그대로 실림

      "params": {                        // JSON Schema 부분집합
        "poi":   { "type": "poi_ref", "required": true },
        "speed": { "type": "enum", "values": ["walk", "run"], "default": "walk" }
      },

      "requires":     [],                           // 전제 플래그 — 전부 성립해야 한다 (AND)
      "requires_any": [],                           // 전제 플래그 — 하나 이상 성립해야 한다 (OR). 비면 검사 안 함
      "forbids":   ["IsSleeping", "IsExhausted"],   // 금지 플래그
      "grants":    [],                              // 성공 시 세팅되는 플래그 (동적: POI 타입에 따름)
      "clears":    ["AtHome","AtWorkplace","AtMarket","AtTavern","AtTemple","AtGate","AtField"],

      "duration": { "kind": "distance", "base_s": 0, "per_meter_s": 0.6 },
      "cost": 2,                                    // 계획 시 비용 힌트 (LLM에 노출)
      "default_timeout_s": 300,

      "emits": [                                    // 게임서버 명령 매핑
        { "command": "MoveTo", "priority": "Normal", "map": { "TargetPoi": "$poi", "Flags": "$speed" } }
      ],
      "completes_on": ["NpcArrived"],               // 이 이벤트 수신 시 스텝 완료
      "fails_on":     ["NpcActionFailed"]
    }
  ]
}
```

**`requires` vs `requires_any`.** `requires`는 AND, `requires_any`는 OR다. 아래 §2.2 표에서 `A/B` 또는 `A | B`로 적힌 칸이 `requires_any`에 들어간다. 이탈 판정은 비트 연산 세 번이다.

```csharp
static bool IsDeviated(WorldFlags cur, WorldFlags required, WorldFlags requiredAny, WorldFlags forbidden)
    => (required & ~cur) != 0
    || (requiredAny != 0 && (requiredAny & cur) == 0)
    || (forbidden & cur) != 0;
```

**`emits[].priority`** 는 역압 시 드롭 순서다 (`docs/02 §1`). 생략하면 `Normal`.

**`emits[].map` 의 값 문법**

| 값 | 뜻 |
|---|---|
| `"$param"` | 플랜 스텝의 `args`에서 가져온다 |
| `"$home"` `"$workplace"` `"$self"` | NPC 인스턴스에서 바인딩한다 |
| `"$first:Flag"` | 해당 `WorldFlags`를 세우는 인벤토리 아이템 중 첫 번째 (`Eat`/`Drink`) |
| 그 외 문자열 | 아이템 id / 열거값 리터럴 |
| 숫자 | 정수 리터럴 (부호 있음) |

### 2.2 전체 액션 목록 (37개)

| 카테고리 | 액션 | 주요 파라미터 | requires | grants |
|---|---|---|---|---|
| **이동 (5)** | `MoveTo` | poi, speed | — | (POI별) |
| | `Follow` | npc, distance | — | HasCompanion |
| | `Wander` | duration_s | — | — |
| | `Flee` | poi | — | — |
| | `Patrol` | route(poi[]), laps | OnDuty | — |
| **노동 (8)** | `Work` | recipe, count | AtWorkplace, HasTool | HasProduct |
| | `Gather` | resource, count | AtField | HasRawMaterial |
| | `Mine` | resource, count | AtField, HasTool | HasRawMaterial |
| | `Farm` | crop, count | AtField, HasTool | HasRawMaterial |
| | `Fish` | count | AtField | HasFood |
| | `Craft` | recipe, count | AtWorkplace, HasRawMaterial | HasProduct |
| | `Cook` | recipe, count | AtHome/AtTavern, HasRawMaterial | HasFood |
| | `Repair` | target | AtWorkplace, HasTool | — |
| **사회 (6)** | `Talk` | npc, topic | — | — |
| | `Trade` | npc/poi, item, amount | HasCoin \| HasProduct | HasCoin/HasProduct |
| | `Greet` | npc | — | — |
| | `Gossip` | npc, topic | — | — |
| | `Pray` | duration_s | AtTemple | IsRested |
| | `Perform` | kind, duration_s | AtTavern/AtMarket | HasCoin |
| **욕구 (5)** | `Eat` | — | HasFood | (clears IsHungry) |
| | `Drink` | — | HasWater | (clears IsThirsty) |
| | `Sleep` | until_time | AtHome | IsRested (clears IsSleeping) |
| | `Rest` | duration_s | — | IsRested |
| | `Bathe` | — | AtHome | — |
| **전투 (5)** | `Attack` | target | HasWeapon | InCombat |
| | `Defend` | poi | — | — |
| | `Guard` | poi, duration_s | OnDuty | — |
| | `CallForHelp` | — | — | AllyNearby |
| | `Retreat` | poi | — | (clears InCombat) |
| **물품 (5)** | `PickUp` | item, count | — | — |
| | `Drop` | item, count | — | (clears InventoryFull) |
| | `Store` | item, count | AtHome/AtWorkplace | (clears InventoryFull) |
| | `Withdraw` | item, count | AtHome/AtWorkplace | — |
| | `Equip` | item | — | HasWeapon/HasTool |
| **기타 (3)** | `Wait` | duration_s | — | — |
| | `Observe` | target, duration_s | — | — |
| | `Emote` | animation | — | — |

> **37개.** §설계 제약(40개 이하)을 지킨다. 추가 시 반드시 하나를 빼거나 파라미터로 흡수한다.

### 2.3 파라미터 타입

| type | 검증 | 비고 |
|---|---|---|
| `poi_ref` | `pois.json`에 존재 + 아키타입 접근 허용 | 검증기 2단 |
| `item_ref` | `items.json`에 존재 | |
| `npc_ref` | `"self"` / `"nearest:<archetype>"` / `"poi_owner:<poi>"` | 인스턴스 ID 직접 지정 금지 |
| `zone_ref` | `zones.json`에 존재 | |
| `enum` | `values` 내 | |
| `int` | `min`/`max` | |
| `route` | `poi_ref[]`, 길이 2~8 | |

---

## 3. `items.json`

```jsonc
{
  "version": 1,
  "items": [
    { "id": "iron_ore",  "code": 1,  "category": "raw",      "grants": ["HasRawMaterial"], "stack": 50 },
    { "id": "coal",      "code": 2,  "category": "raw",      "grants": ["HasRawMaterial"], "stack": 50 },
    { "id": "iron_sword","code": 20, "category": "product",  "grants": ["HasProduct","HasWeapon"], "stack": 5 },
    { "id": "bread",     "code": 40, "category": "food",     "grants": ["HasFood"], "stack": 20 },
    { "id": "water",     "code": 41, "category": "drink",    "grants": ["HasWater"], "stack": 10 },
    { "id": "smith_hammer","code":60,"category": "tool",     "grants": ["HasTool"], "stack": 1 },
    { "id": "coin",      "code": 79, "category": "currency", "grants": ["HasCoin"], "stack": 99999 }
  ],
  "recipes": [
    { "id": "iron_sword", "workplace_type": "smithy",
      "inputs": [{ "item": "iron_ore", "count": 3 }, { "item": "coal", "count": 1 }],
      "outputs":[{ "item": "iron_sword", "count": 1 }],
      "duration_s": 600 }
  ]
}
```

**`grants` 필드가 중요하다.** 인벤토리 변경 이벤트를 받으면 아이템의 `grants`를 OR 해서 WorldFlags를 재계산한다. 코드에 하드코딩된 "빵을 가지면 HasFood" 같은 규칙이 없어야 한다.

---

## 4. `zones.json` / `pois.json`

```jsonc
// zones.json
{
  "version": 1,
  "zones": [
    { "id": "town_center", "code": 1, "name_key": "zone.town_center",
      "adjacent": ["town_north","town_south","gate_east"],
      "default_region_state": "Peace",
      "default_climate": "Fair",
      "capacity": 1200 }
  ]
}
```

```jsonc
// pois.json
{
  "version": 1,
  "pois": [
    { "id": "smithy_01", "code": 12, "zone": "town_center",
      "type": "workplace",              // home | workplace | market | tavern | temple | gate | field | wilderness
      "subtype": "smithy",
      "pos": { "x": 120.5, "y": 0.0, "z": -88.2 },
      "capacity": 3,
      "open_hours": { "from": "Morning", "to": "Evening" },
      "grants": ["AtWorkplace"],        // 이 POI에 있을 때 세팅되는 플래그
      "allowed_archetypes": ["blacksmith", "apprentice_smith"],   // 여기서 *일하는* 아키타입
      "resources": []                   // field 타입일 때 채집 가능 자원
    }
  ]
}
```

> **`allowed_archetypes`는 출입 허가가 아니라 근무 허가다.** 일터(`workplace`)·채집지(`field`)·야외 작업지(`wilderness`)에서만 출입을 제한하고, 시장·선술집·신전·성문·주거는 공공장소라 누구나 드나든다. 농부가 시장에 못 가면 `$market` 바인딩이 실패한다.

`poi_distances.bin` — 250×250 `Half` 행렬(≈122KB). Sim의 이동 소요시간 계산과 검증기 3단(도달 가능성)에 쓴다. 빌드 스크립트가 `pois.json`에서 생성한다.

---

## 5. `archetypes.json` — NPC 아키타입 (40종)

```jsonc
{
  "version": 1,
  "archetypes": [
    {
      "id": "blacksmith", "code": 3,
      "name_key": "npc.blacksmith",
      "desc": "마을의 대장장이. 광석을 제련해 무기와 도구를 만든다.",   // ← LLM 프롬프트에 실림

      "allowed_actions": ["MoveTo","Work","Craft","Gather","Mine","Trade","Talk",
                          "Eat","Drink","Sleep","Rest","Store","Withdraw","Equip",
                          "Repair","Greet","Wait","Emote","Flee","Retreat"],

      "home_poi_type": "home",
      "workplace_poi_type": "smithy",
      "primary_recipes": ["iron_sword","iron_tool","horseshoe"],

      "traits": {                        // 0~100. LLM에 노출되어 플랜 성향에 반영
        "diligence": 80,
        "sociability": 40,
        "courage": 55,
        "greed": 50
      },

      "default_goals": ["restock_ore","fulfill_orders","maintain_shop"],
      "fallback_plan": "fb_blacksmith",
      "combat_capable": true,
      "population_weight": 0.012         // 5,000 중 약 60마리
    }
  ]
}
```

**40종 구성안**

| 그룹 | 아키타입 |
|---|---|
| 생산 (10) | blacksmith, carpenter, tailor, alchemist, baker, brewer, jeweler, tanner, mason, scribe |
| 채집 (7) | miner, farmer, fisher, hunter, woodcutter, herbalist, shepherd |
| 상업 (5) | merchant, innkeeper, stablemaster, banker, peddler |
| 치안 (5) | guard_captain, town_guard, gate_guard, patrol_scout, watchman |
| 종교/학문 (4) | priest, acolyte, scholar, healer |
| 주민 (6) | villager, child, elder, beggar, drunkard, noble |
| 특수 (3) | quest_giver, wandering_bard, caravan_leader |

`population_weight` 합계는 1.0이어야 하며, 검증기가 확인한다.

---

## 6. `context_buckets.json` — 캐시 키 정의

```jsonc
{
  "version": 1,
  "dimensions": {
    "time_of_day": {
      "values": ["Dawn","Morning","Noon","Afternoon","Evening","Night"],
      "game_hours": { "Dawn":[5,7], "Morning":[7,11], "Noon":[11,14],
                      "Afternoon":[14,18], "Evening":[18,22], "Night":[22,5] },
      "world_flag": { "Dawn":"IsDawn", "Morning":"IsDay", "Noon":"IsDay",
                      "Afternoon":"IsDay", "Evening":"IsEvening", "Night":"IsNight" }
    },
    "region_state": { "values": ["Peace","Alert","War","Disaster"] },
    "climate":      { "values": ["Fair","Cold","Storm"] }
  },

  "key_format": "{archetype}@{time_of_day}.{region_state}.{climate}",
  "key_example": "blacksmith@Evening.War.Cold",

  "total_keys": 2880,     // 40 × 6 × 4 × 3 — 검증기가 계산해서 대조한다

  "prebake_priority": [   // 프리베이크 순서. 앞쪽이 실제로 많이 쓰인다
    { "region_state": "Peace", "weight": 10 },
    { "region_state": "Alert", "weight": 3 },
    { "region_state": "War",   "weight": 2 },
    { "region_state": "Disaster", "weight": 1 }
  ]
}
```

```csharp
// Npc.Core/Planning/BucketKey.cs — Npc.Planning 이 아니다. docs/03 §5 의 CompiledPlan.Bucket 이
// 이 타입을 들고 있고 CompiledPlan 은 Npc.Core 에 있어서, Planning 에 두면 순환 참조가 된다.
public readonly record struct BucketKey(ArchetypeId A, TimeOfDay T, RegionState R, Climate C)
{
    public int ToIndex() => ((A.Value * 6 + (int)T) * 4 + (int)R) * 3 + (int)C;   // 0..2879
    public override string ToString() => $"{A}@{T}.{R}.{C}";
}
```

플랜 스토어는 `Plan[2880]` 고정 배열이면 충분하다. 조회는 인덱스 계산 한 번 — 해시맵도 필요 없다.

---

## 7. `interrupts.json`

**LLM이 만들지 않는다.** 인터럽트는 반응 속도가 생명이라 결정론 규칙으로만 정의한다.

```jsonc
{
  "version": 1,
  "rules": [
    { "id": "flee_on_threat", "priority": 100,
      "when": { "any_flag": ["ThreatNearby","InCombat"], "archetype_trait": { "courage": "<40" } },
      "then": { "action": "Flee", "params": { "poi": "$nearest_safe" } },
      "replan": { "urgency": 100 } },

    { "id": "fight_on_threat", "priority": 100,
      "when": { "any_flag": ["ThreatNearby"], "archetype_trait": { "courage": ">=40" }, "combat_capable": true },
      "then": { "action": "Attack", "params": { "target": "$threat" } },
      "replan": { "urgency": 90 } },

    { "id": "yield_to_player", "priority": 90,
      "when": { "event": "PlayerInteracted" },
      "then": { "action": "Wait", "params": { "duration_s": 30 } },
      "replan": { "urgency": 70 } },

    { "id": "eat_when_starving", "priority": 50,
      "when": { "all_flag": ["IsHungry","HasFood"] },
      "then": { "action": "Eat" },
      "replan": { "urgency": 10 } },

    { "id": "seek_shelter", "priority": 40,
      "when": { "all_flag": ["WeatherHarsh","InWilderness"] },
      "then": { "action": "MoveTo", "params": { "poi": "$nearest_shelter" } },
      "replan": { "urgency": 40 } }
  ]
}
```

`then`은 **즉시 실행**되고, `replan.urgency`는 우선순위 큐(§14 문서)에 점수로 들어간다. 즉 "먼저 도망치고, 새 계획은 나중에 받는다".

---

## 8. `fallback_plans.json`

LLM·캐시가 전부 실패했을 때 쓰는 최후 보루. **아키타입당 1개, 사람이 직접 작성한다.** 시나리오 C(§00 문서)의 통과 조건이 이것이다.

```jsonc
{
  "version": 1,
  "plans": [
    { "id": "fb_blacksmith", "archetype": "blacksmith",
      "goal": "survive_and_work",
      "steps": [
        { "action": "MoveTo", "args": { "poi": "$workplace" }, "timeout_s": 300 },
        { "action": "Work",   "args": { "recipe": "$primary", "count": 1 }, "timeout_s": 900 },
        { "action": "MoveTo", "args": { "poi": "$home" }, "timeout_s": 300 },
        { "action": "Sleep",  "args": { "until_time": "Morning" } }
      ],
      "loop": true }
  ]
}
```

`$workplace`, `$home`, `$primary`는 NPC 인스턴스에서 바인딩되는 심볼이다. 폴백 플랜은 반드시 `loop: true`여서 무한히 지속 가능해야 한다.

---

## 9. `npc_instances.json` — NPC 5,000

생성 스크립트(`tools/gen_npcs.cs`)가 `archetypes.json`의 `population_weight`와 `pois.json`의 `capacity`를 보고 만든다. **수동 편집 대상이 아니다.**

```jsonc
{
  "version": 1,
  "seed": 20260725,                     // 결정론 재현용
  "npcs": [
    { "id": 1, "archetype": "blacksmith", "zone": "town_center",
      "home_poi": "house_042", "workplace_poi": "smithy_01",
      "spawn_pos": { "x": 120.5, "y": 0.0, "z": -90.0 },
      "trait_offsets": { "diligence": +5, "sociability": -10 },   // ±15 범위 개체 편차
      "initial_inventory": [{ "item": "smith_hammer", "count": 1 }, { "item": "bread", "count": 2 }] }
  ]
}
```

**개체 편차(`trait_offsets`)는 플랜 선택에는 영향을 주지 않는다** — 같은 버킷의 NPC는 같은 플랜을 쓴다. 편차는 런타임 실행 시 소요시간·대기시간·경로 선택 랜덤화에만 쓴다. 이게 "같은 플랜인데 다 다르게 보이는" 값싼 다양성의 원천이다.

---

## 10. `prompt/` — 프롬프트 프리픽스

### 10.1 프리픽스 조립 규칙

```
[1] system_rules.md                       (~600 tok)
[2] actions.json → 액션 카탈로그 렌더링    (~2,200 tok)  ← 자동 생성
[3] 03_PlanDSL_Spec의 스키마 요약          (~600 tok)   ← 자동 생성
[4] fewshot/*.json 3건                     (~900 tok)
────────────────────────────────────────────────────
합계 목표: 4,200 ~ 4,500 토큰
```

**4,096 토큰 이상이어야 한다.** Gemini 3.x와 Claude Haiku 4.5의 프롬프트 캐시 최소 임계가 4,096이다. 미달하면 캐시가 아예 안 걸린다. 짧으면 액션 설명을 늘려서 채운다.

### 10.2 프리픽스 불변성 검증 — 반드시 넣는다

```csharp
// Npc.Llm/PromptPrefix.cs
public sealed class PromptPrefix
{
    public string Text { get; }              // 조립 결과. 프로세스 생애 동안 불변
    public string Sha256 { get; }            // 로그·메트릭에 항상 기록
    public int TokenCount { get; }
}
```

- 프리픽스는 기동 시 **1회 조립**하고 그 뒤 절대 재조립하지 않는다
- 매 요청마다 `Sha256`을 메트릭에 태그로 붙인다 → 대시보드에서 **유니크 해시가 1개인지** 상시 확인
- 2개 이상 나오면 즉시 경보. 이게 리스크 R11(캐시 미적중)의 탐지 장치다
- 테스트: `PromptPrefix_IsByteIdentical_Across1000Builds`

### 10.3 `system_rules.md` 요지

```markdown
You are a behavior planner for NPCs in a fantasy MMORPG village simulation.

Your ONLY job: given an NPC's archetype and current situation, output a JSON plan
that composes actions from the catalog below.

HARD RULES
1. Use ONLY action ids from the catalog. Never invent an action.
2. Maximum 10 steps. Prefer 5-8.
3. Every step's `requires` flags must be satisfiable by the preceding steps' `grants`,
   or already present in the given `flags` field.
4. Reference POIs only by the symbolic forms: $home, $workplace, $market, $tavern,
   $temple, $gate, $nearest_field, $nearest_safe, $nearest_shelter. Never invent a POI id.
5. Do not include dialogue text. Use Speak with a dialogue id only.
6. The plan must be loopable or must end in a rest/sleep state.
7. Output JSON only. No prose, no markdown fences.
```

---

## 11. 로딩 파이프라인과 검증

```csharp
// Npc.MasterData/MasterDataLoader.cs
public sealed class MasterDataSet
{
    public required FlagTable      Flags      { get; init; }
    public required ActionCatalog  Actions    { get; init; }   // ActionId → ActionDef, 배열 인덱싱
    public required ItemTable      Items      { get; init; }
    public required ZoneTable      Zones      { get; init; }
    public required PoiTable       Pois       { get; init; }   // + 거리 행렬
    public required ArchetypeTable Archetypes { get; init; }
    public required BucketSpace    Buckets    { get; init; }
    public required InterruptRules Interrupts { get; init; }
    public required PlanTable      Fallbacks  { get; init; }
    public required NpcRoster      Npcs       { get; init; }
    public required PromptPrefix   Prefix     { get; init; }

    public string ContentHash { get; init; }   // 전체 해시. 플랜 스토어 유효성 판정에 사용
}
```

### 검증 규칙 (전부 기동 실패 조건)

| # | 검증 |
|---|---|
| V1 | 모든 `code` 값이 파일 내에서 유일하다 |
| V2 | 모든 flag id 참조가 `world_flags.json`에 존재한다 |
| V3 | 모든 poi/item/zone/action 참조가 대상 파일에 존재한다 |
| V4 | `archetypes[].allowed_actions`가 전부 카탈로그에 있다 |
| V5 | `population_weight` 합 = 1.0 (±0.001) |
| V6 | `context_buckets.total_keys` == 실제 조합 수 (2,880) |
| V7 | 모든 아키타입에 `fallback_plan`이 존재하고, 그 플랜이 검증기 4단을 통과한다 |
| V8 | 모든 아키타입의 `allowed_actions`만으로 폴백 플랜이 구성 가능하다 |
| V9 | 프롬프트 프리픽스 토큰 수 ≥ 4,096 |
| V10 | `pois.json`의 각 `allowed_archetypes`가 아키타입 정원을 충족한다 (workplace capacity 총합 ≥ 해당 아키타입 인구) |
| V11 | 모든 POI가 존 그래프에서 도달 가능하다 (고립 POI 없음) |

### 콘텐츠 해시와 플랜 스토어 무효화

```
MasterDataSet.ContentHash 가 바뀌면 → 프리베이크된 플랜 스토어 전량 무효
단, 변경된 파일이 pois.json 뿐이고 POI가 추가만 되었다면 → 부분 무효화 허용
```

이 판정 로직은 `Npc.Planning/PlanStoreValidator.cs`에 둔다. 마스터데이터를 조금 고칠 때마다 84분(또는 3분) 프리베이크를 다시 도는 것을 막아준다.

---

## 12. 작업 순서 권고

마스터데이터는 **의존 순서대로** 만들어야 한다. 역순으로 만들면 계속 되돌아온다.

```
1. world_flags.json      ← 모든 것의 기반. 여기서 실수하면 전부 다시
2. items.json            ← grants가 flags를 참조
3. actions.json          ← requires/grants가 flags를, params가 items를 참조
4. zones.json → pois.json → poi_distances.bin
5. archetypes.json       ← allowed_actions가 actions를, poi_type이 pois를 참조
6. context_buckets.json  ← archetypes 수에 의존 (2,880 계산)
7. interrupts.json
8. fallback_plans.json   ← 사람이 40개 작성. 가장 오래 걸린다 (W2~W4 내내)
9. prompt/               ← actions.json에서 자동 생성
10. npc_instances.json   ← 스크립트 생성
```

**8번(폴백 플랜 40개 수작성)이 이 프로젝트에서 사람 손이 가장 많이 가는 부분이다.** 그리고 동시에 **오써링 자동화의 비교 기준선**이 된다 — 사람이 40개 만드는 데 걸린 시간 대 LLM이 2,880개 만드는 데 걸린 시간이 곧 R&D 보고서의 핵심 수치다. 작성 시간을 반드시 기록한다.
