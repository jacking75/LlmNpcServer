using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.Core.Validation;
using Npc.MasterData;

namespace Npc.Llm;

/// <summary>
/// 플랜 품질 등급. docs/12 §2. [W9~] 티어 선택에 쓴다 (docs/14 §4).
/// </summary>
public enum PlanQuality
{
    /// <summary>(아키타입 × 버킷) 단위. 수천 NPC 가 공유하므로 품질에 투자한다.</summary>
    Archetype = 0,

    /// <summary>개체 1회용 재계획.</summary>
    Individual = 1,
}

/// <summary>직전 플랜의 결말. 서픽스의 <c>last_plan_outcome</c>.</summary>
public enum PlanOutcome
{
    /// <summary>모르거나 직전 플랜이 없다. 서픽스에 싣지 않는다.</summary>
    Unknown = 0,

    /// <summary>끝까지 실행됐다.</summary>
    Completed = 1,

    /// <summary>어느 스텝에서 실패했다.</summary>
    FailedAtStep = 2,
}

/// <summary>
/// 최근에 이 NPC 에게 일어난 일 하나. docs/12 §3 의 <c>recent</c>.
/// <b>문자열이 아니라 강타입이다</b> — 플레이어가 쓴 문자열이 프롬프트에 새는 길을 막는다 (CLAUDE.md §2.5).
/// </summary>
/// <param name="Kind">이벤트 종류.</param>
/// <param name="Poi">어디에서 일어났는가. 없으면 <see cref="PoiSymbol.None"/>.</param>
/// <param name="Salience">중요도. 큰 것 3개만 서픽스에 실린다.</param>
public readonly record struct RecentEvent(GameEventKind Kind, PoiSymbol Poi, byte Salience);

/// <summary>
/// 개별 NPC 재계획에만 실리는 개체 상태. docs/12 §3.
/// 아키타입 플랜(캐시 대상)에는 <c>null</c> 이다 — 개체 정보가 섞이면 재사용이 무의미해진다.
/// </summary>
/// <param name="Inventory">인벤토리. 0 인 항목은 서픽스에 싣지 않는다.</param>
/// <param name="Recent">최근 사건. salience 상위 3개만 실린다.</param>
/// <param name="LastOutcome">직전 플랜의 결말.</param>
/// <param name="LastFailedStep"><see cref="PlanOutcome.FailedAtStep"/> 일 때 실패한 스텝 번호.</param>
public readonly record struct NpcSnapshot(
    ImmutableArray<InventorySlot> Inventory,
    ImmutableArray<RecentEvent> Recent,
    PlanOutcome LastOutcome = PlanOutcome.Unknown,
    int LastFailedStep = -1);

/// <summary>
/// 플랜 생성 요청. docs/12 §2.
///
/// <c>docs/12 §2</c> 는 이 타입을 <c>IPlanCompiler.cs</c> 옆에 그려 두었지만,
/// 실제로 먼저 필요한 쪽은 서픽스 조립기다 (T2-06 이 T2-09 보다 앞선다).
/// </summary>
/// <param name="Bucket">아키타입 × 시간대 × 지역상태 × 기후.</param>
/// <param name="Flags">지금 서 있는 월드 플래그. 참인 것만 서픽스에 실린다.</param>
/// <param name="Quality">아키타입 플랜인가 개체 플랜인가.</param>
/// <param name="Individual">개체 상태. null 이면 아키타입 플랜(캐시 대상)이다.</param>
/// <param name="PreviousFailure">재시도일 때 직전 검증 실패. 서픽스에만 붙는다 (CLAUDE.md §2.5).</param>
public readonly record struct PlanRequest(
    BucketKey Bucket,
    WorldFlags Flags,
    PlanQuality Quality = PlanQuality.Archetype,
    NpcSnapshot? Individual = null,
    ValidationResult? PreviousFailure = null);

/// <summary>
/// 가변 서픽스 조립. docs/12 §3.
///
/// <b>프리픽스는 캐시되고 서픽스는 매번 prefill 된다.</b> 짧을수록 곧바로 지연이 준다 —
/// dotLLM 은 prefill 이 2~5배 느리므로 특히 그렇다. 그래서 300 토큰 상한을 테스트로 강제한다
/// (<c>Suffix_NeverExceeds300Tokens</c>). 이 테스트가 없으면 서픽스는 반드시 자란다.
///
/// 압축 원칙 (docs/12 §3):
/// <list type="bullet">
///   <item><b>키 이름은 줄이지 않는다</b> — <c>archetype</c> 을 <c>a</c> 로 줄이면 모델 이해도가 떨어진다</item>
///   <item>값은 열거형 문자열만. 설명 문장 금지</item>
///   <item>플래그는 참인 것만. 거짓은 생략</item>
///   <item>인벤토리는 0 이 아닌 항목만</item>
///   <item><c>recent</c> 는 salience 상위 3개만</item>
/// </list>
///
/// 아키타입별 허용 액션은 <b>여기에도</b> 싣는다 (T2-21 1차). 프리픽스의 ARCHETYPES 표만으로는
/// 모델이 자기 행을 안정적으로 찾지 못해 <c>V2.ACTION_NOT_ALLOWED</c> 가 최다 실패 원인이었다.
/// docs/12 §8 이 제시한 두 처방 중 "서픽스에 allowed_actions 추가" 쪽이다.
/// </summary>
public static class PlanRequestSuffix
{
    /// <summary>docs/12 §3 이 정한 <c>recent</c> 상한.</summary>
    public const int MaxRecentEvents = 3;

    /// <summary>
    /// 서픽스에 싣는 인벤토리 항목 수 상한. 아이템은 82종이라 만재 인벤을 그대로 실으면
    /// 그것만으로 300 토큰을 넘긴다. code 오름차순(원자재 → 완제품 → 식량 …)으로 자른다.
    /// </summary>
    public const int MaxInventoryEntries = 8;

    /// <summary>
    /// 재시도 블록의 <c>detail</c> 길이 상한(문자).
    ///
    /// 검증기 3단의 설명은 실패 시점 상태를 통째로 펴서 넣기 때문에 길어질 수 있는데,
    /// 그 상태는 바로 위 <c>flags</c> 에 이미 있다. 잘라도 "무엇을 고쳐야 하는가"(액션 이름 ·
    /// 모자란 플래그 · 스텝 번호)는 문장 앞쪽에 있어 살아남는다.
    /// 자르지 않으면 재시도 서픽스가 예산의 두 배를 넘긴다 (T2-07 실측 665 토큰).
    /// </summary>
    public const int MaxFailureDetailChars = 180;

    /// <summary>docs/12 §3 · CLAUDE.md §2.5 의 서픽스 토큰 상한.</summary>
    public const int TokenBudget = 300;

    /// <summary>덜어내기 단계 수. <see cref="Compose"/> 의 주석에 단계별로 무엇이 빠지는지 있다.</summary>
    private const int MaxTrimLevel = 5;

    /// <summary>
    /// 요청 하나를 서픽스 문자열로. <b>예산을 넘기면 우선순위대로 덜어낸다.</b>
    ///
    /// 예산 초과를 테스트로만 막으면, 실제로 넘치는 입력이 들어왔을 때 그대로 나간다.
    /// 상황·플래그·허용 액션은 절대 덜어내지 않는다 — 그건 플랜의 유효성을 결정하는 정보다.
    /// </summary>
    public static string Build(in PlanRequest request, MasterDataSet data)
    {
        ArgumentNullException.ThrowIfNull(data);

        for (int trim = 0; ; trim++)
        {
            string suffix = Compose(request, data, trim);

            if (trim >= MaxTrimLevel || PromptPrefix.CountTokens(suffix) <= TokenBudget)
            {
                return suffix;
            }
        }
    }

    /// <summary>
    /// 덜어내기 단계별 조립.
    /// <list type="number">
    ///   <item>0 — 전부 싣는다</item>
    ///   <item>1 — <c>recent</c> 를 뺀다 (재시도 블록이 더 구체적이다)</item>
    ///   <item>2 — <c>last_plan_outcome</c> 도 뺀다</item>
    ///   <item>3 — 인벤토리를 4종으로 줄인다</item>
    ///   <item>4 — 인벤토리를 빼고 실패 설명을 절반으로 줄인다</item>
    ///   <item>5 — 성향·목표까지 뺀다. 최후다 — 이 단계에서는 "무엇이 유효한가"가 "어떤 성격인가"를 이긴다</item>
    /// </list>
    /// </summary>
    private static string Compose(in PlanRequest request, MasterDataSet data, int trim)
    {
        ArchetypeDef archetype = data.Archetypes[request.Bucket.A];

        var json = new JsonObject
        {
            ["archetype"] = archetype.Id,

            // T2-21 1차 — docs/12 §8 의 V2.ACTION_NOT_ALLOWED 처방.
            // 프리픽스의 아키타입 표만으로는 모델이 자기 행을 안정적으로 찾지 못했다
            // (탐침 20건에서 최다 실패 원인). 여기 실으면 눈앞에 있어 놓칠 수 없다.
            // 아키타입당 20여 개 id 라 서픽스가 약 80토큰 늘지만 300 예산 안이다.
            ["allowed_actions"] = AllowedActions(archetype, data),

            ["time_of_day"] = request.Bucket.T.ToString(),
            ["region_state"] = request.Bucket.R.ToString(),
            ["climate"] = request.Bucket.C.ToString(),
            ["flags"] = FlagArray(request.Flags),
        };

        // 성향·목표는 플랜의 "성격"을 정한다. 유효성을 정하는 것은 아니므로 마지막에 뺀다.
        if (trim < 5)
        {
            json["traits"] = new JsonObject
            {
                ["diligence"] = archetype.Traits.Diligence,
                ["sociability"] = archetype.Traits.Sociability,
                ["courage"] = archetype.Traits.Courage,
                ["greed"] = archetype.Traits.Greed,
            };

            json["goals"] = StringArray(archetype.DefaultGoals);
        }

        if (request.Individual is { } snapshot)
        {
            AppendIndividual(json, snapshot, data, trim);
        }

        var sb = new StringBuilder(2_048);

        sb.Append("\n# REQUEST\n\n");
        sb.Append(json.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        sb.Append('\n');

        if (request.PreviousFailure is { IsValid: false } failure)
        {
            // 블록을 새로 만들지 않는다 — 검증기가 만든 것을 그대로 붙인다 (docs/12 §6).
            // 다만 detail 길이만 예산 안으로 자른다.
            sb.Append("\n# PREVIOUS ATTEMPT REJECTED\n\n");
            sb.Append(CoherenceValidator.Explain(failure with { Detail = Truncate(failure.Detail, trim) }));
            sb.Append("\n\nFix exactly that and output the corrected plan. JSON only.\n");
            return sb.ToString();
        }

        sb.Append("\nOutput the plan JSON for this request. JSON only.\n");
        return sb.ToString();
    }

    private static string Truncate(string detail, int trim)
    {
        int limit = trim < 4 ? MaxFailureDetailChars : MaxFailureDetailChars / 2;

        return detail.Length <= limit ? detail : detail[..limit] + "...";
    }

    private static void AppendIndividual(JsonObject json, in NpcSnapshot snapshot, MasterDataSet data, int trim)
    {
        int limit = trim switch
        {
            < 3 => MaxInventoryEntries,
            3 => MaxInventoryEntries / 2,
            _ => 0,
        };

        var inventory = new JsonObject();
        int written = 0;

        // ItemId 오름차순. Dictionary 순회 순서에 의존하지 않는다 (CLAUDE.md §2.3).
        foreach (InventorySlot slot in snapshot.Inventory.Sort((a, b) => a.Item.Value.CompareTo(b.Item.Value)))
        {
            if (slot.Count <= 0)
            {
                continue;
            }

            if (written++ >= limit)
            {
                break;
            }

            inventory[data.Items[slot.Item].Id] = slot.Count;
        }

        if (inventory.Count > 0)
        {
            json["inventory"] = inventory;
        }

        if (trim < 1)
        {
            JsonArray recent = RecentArray(snapshot.Recent);
            if (recent.Count > 0)
            {
                json["recent"] = recent;
            }
        }

        if (trim >= 2)
        {
            return;
        }

        switch (snapshot.LastOutcome)
        {
            case PlanOutcome.Completed:
                json["last_plan_outcome"] = "completed";
                break;

            case PlanOutcome.FailedAtStep:
                json["last_plan_outcome"] = "failed_at_step_" + Math.Max(0, snapshot.LastFailedStep);
                break;

            default:
                break;
        }
    }

    /// <summary>salience 내림차순 상위 3개. 동점이면 종류·POI 순서로 갈라 결정론을 지킨다.</summary>
    private static JsonArray RecentArray(ImmutableArray<RecentEvent> events)
    {
        var array = new JsonArray();

        if (events.IsDefaultOrEmpty)
        {
            return array;
        }

        ImmutableArray<RecentEvent> ordered = events.Sort((a, b) =>
        {
            int bySalience = b.Salience.CompareTo(a.Salience);
            if (bySalience != 0)
            {
                return bySalience;
            }

            int byKind = ((byte)a.Kind).CompareTo((byte)b.Kind);
            return byKind != 0 ? byKind : ((byte)a.Poi).CompareTo((byte)b.Poi);
        });

        for (int i = 0; i < ordered.Length && i < MaxRecentEvents; i++)
        {
            RecentEvent e = ordered[i];

            array.Add(e.Poi == PoiSymbol.None
                ? e.Kind.ToString()
                : e.Kind.ToString() + "@" + PoiSymbols.ToText(e.Poi));
        }

        return array;
    }

    /// <summary>참인 플래그만. bit 오름차순이라 같은 상태면 같은 문자열이 나온다.</summary>
    private static JsonArray FlagArray(WorldFlags flags)
    {
        var array = new JsonArray();

        for (int i = 0; i < WorldFlagTable.Values.Length; i++)
        {
            if ((flags & WorldFlagTable.Values[i]) != 0)
            {
                array.Add(WorldFlagTable.Names[i]);
            }
        }

        return array;
    }

    /// <summary>이 아키타입이 쓸 수 있는 액션. archetypes.json 등장 순서 그대로.</summary>
    private static JsonArray AllowedActions(ArchetypeDef archetype, MasterDataSet data)
    {
        var array = new JsonArray();

        foreach (ActionId action in archetype.AllowedActions)
        {
            array.Add(data.ActionName(action));
        }

        return array;
    }

    private static JsonArray StringArray(ImmutableArray<string> values)
    {
        var array = new JsonArray();

        foreach (string value in values)
        {
            array.Add(value);
        }

        return array;
    }
}
