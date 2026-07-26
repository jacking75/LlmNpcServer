using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Npc.Core;
using Npc.MasterData;

namespace Npc.Llm;

/// <summary>
/// <c>actions.json</c> · <c>items.json</c> → 프롬프트용 마크다운. docs/01 §10.1 [2] · docs/12 §8.
///
/// <b>손으로 쓴 카탈로그를 두지 않는다.</b> 두는 순간 마스터데이터와 어긋나고,
/// 어긋난 카탈로그는 <c>V2.UNKNOWN_ACTION</c> 을 조용히 양산한다 (CLAUDE.md §2.4).
///
/// 플래그 5종을 <b>표로</b> 싣는 것이 docs/12 §8 의 <c>V3.PRECONDITION_UNMET</c> 처방이다.
/// 산문으로 흩어 놓으면 모델이 "무엇이 무엇을 세워 주는가"를 읽어내지 못한다.
///
/// 출력은 완전히 결정론적이다 — 프리픽스 SHA 가 여기서 흔들리면 캐시가 통째로 미적중이 된다.
/// 그래서 줄바꿈은 LF 로 고정하고, 열거는 전부 code 순서(또는 이름 순서)로만 돈다.
/// </summary>
public static class CatalogRenderer
{
    /// <summary>플래그·액션·레시피 전부. 프리픽스의 [2] 절이 된다.</summary>
    public static string Render(MasterDataSet data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var sb = new StringBuilder(64 * 1024);

        RenderFlags(sb);
        RenderActions(sb, data);
        RenderRecipes(sb, data);

        return sb.ToString();
    }

    /// <summary>
    /// 월드 플래그 목록. bit 오름차순.
    ///
    /// <c>world_flags.json</c> 의 <c>desc</c> 는 싣지 않는다 — 생성된 <see cref="WorldFlagTable"/> 이
    /// 이름만 들고 있고, 이름 자체가 영문 서술형이라 의미가 드러난다.
    /// 통과율 개선(T2-21)에서 <c>V3.PRECONDITION_UNMET</c> 이 상위로 오면 그때 설명을 붙인다.
    /// </summary>
    private static void RenderFlags(StringBuilder sb)
    {
        sb.Append("# WORLD FLAGS\n\n");
        sb.Append("A flag is either set or not set. Before a step runs, every flag in its\n");
        sb.Append("`requires` column must hold, at least one flag in `requires_any` must hold, and\n");
        sb.Append("no flag in `forbids` may hold. After the step succeeds the flags in `grants`\n");
        sb.Append("become set and the flags in `clears` become unset. The request lists only the\n");
        sb.Append("flags that are set right now; everything else is unset.\n\n");
        sb.Append("The ").Append(WorldFlagTable.Count).Append(" flags are:\n\n");

        for (int i = 0; i < WorldFlagTable.Names.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            sb.Append(WorldFlagTable.Names[i]);
        }

        sb.Append(".\n\n");
    }

    /// <summary>액션 카탈로그. 플래그 5종 표 + 액션별 설명·인자.</summary>
    private static void RenderActions(StringBuilder sb, MasterDataSet data)
    {
        ImmutableArray<ActionDef> actions = data.Actions.Actions;

        sb.Append("# ACTION CATALOG\n\n");
        sb.Append("These ").Append(actions.Length).Append(" ids are the complete vocabulary. ");
        sb.Append("Anything else is rejected by the validator.\n");
        sb.Append("An archetype may use only the subset listed in its request; the rest are rejected too.\n\n");

        sb.Append("## flag transitions\n\n");
        sb.Append("| action | requires | requires_any | forbids | grants | clears |\n");
        sb.Append("|---|---|---|---|---|---|\n");

        foreach (ActionDef action in actions)
        {
            sb.Append("| ").Append(action.Id)
              .Append(" | ").Append(FlagList(action.Requires))
              .Append(" | ").Append(FlagList(action.RequiresAny))
              .Append(" | ").Append(FlagList(action.Forbids))
              .Append(" | ").Append(FlagList(action.Grants))
              .Append(" | ").Append(FlagList(action.Clears))
              .Append(" |\n");
        }

        sb.Append('\n');
        sb.Append("`MoveTo` is the only way to satisfy a location flag: it clears every location\n");
        sb.Append("flag and the arrival sets the one that matches its `poi` argument.\n\n");

        foreach (ActionDef action in actions)
        {
            RenderAction(sb, action);
        }
    }

    private static void RenderAction(StringBuilder sb, ActionDef action)
    {
        sb.Append("## ").Append(action.Id)
          .Append("  (").Append(CategoryName(action.Category))
          .Append(", cost ").Append(action.Cost.ToString(CultureInfo.InvariantCulture))
          .Append(", default timeout ")
          .Append(action.DefaultTimeoutSeconds.ToString(CultureInfo.InvariantCulture))
          .Append("s)\n\n");

        sb.Append(Compact(action.Description)).Append("\n\n");
        sb.Append("args:\n");

        if (action.Params.Length == 0)
        {
            sb.Append("- (none - use an empty object)\n\n");
            return;
        }

        foreach (ParamDef param in action.Params)
        {
            sb.Append("- `").Append(param.Name).Append("` : ").Append(TypeName(param.Type));

            if (param.EnumValues.Length > 0)
            {
                sb.Append(" one of {").Append(string.Join(", ", param.EnumValues)).Append('}');
            }

            // npc_ref 는 열거로 못 박을 수 없다 (아키타입 40종 × 3형식). 형식만 그 자리에 적는다 —
            // 안 적으면 모델이 `nearest:player` 같은 것을 지어낸다 (T2-21 2차 실측).
            if (param.Type == ParamType.NpcRef)
            {
                sb.Append(" one of {self, nearest:<archetype id>, poi_owner:<poi symbol>}");
            }

            if (param.Type == ParamType.Int && param.Min != int.MinValue && param.Max != int.MaxValue)
            {
                sb.Append(" range ").Append(param.Min.ToString(CultureInfo.InvariantCulture))
                  .Append("..").Append(param.Max.ToString(CultureInfo.InvariantCulture));
            }

            sb.Append(param.Required ? " - required" : " - optional");

            if (param.DefaultText is { } text)
            {
                sb.Append(", default ").Append(text);
            }
            else if (param.Type == ParamType.Int && param.DefaultInt != 0)
            {
                sb.Append(", default ").Append(param.DefaultInt.ToString(CultureInfo.InvariantCulture));
            }

            sb.Append('\n');
        }

        sb.Append('\n');
    }

    /// <summary>
    /// 레시피 입출력. docs/12 §8 의 <c>V3.RESOURCE_IMBALANCE</c> 처방이다.
    /// 이게 없으면 모델은 검 3자루에 광석이 몇 개 드는지 알 방법이 없다.
    /// <b>서픽스가 아니라 여기(프리픽스)에 싣는다</b> — 정적 데이터이고, 서픽스는 300토큰뿐이다.
    /// </summary>
    private static void RenderRecipes(StringBuilder sb, MasterDataSet data)
    {
        sb.Append("# RECIPES\n\n");
        sb.Append("`Craft`, `Cook` and `Work` consume the inputs and produce the outputs listed\n");
        sb.Append("here, multiplied by the step's `count`. A plan that crafts must gather or\n");
        sb.Append("already hold enough input.\n\n");

        foreach (RecipeDef recipe in data.Items.Recipes)
        {
            sb.Append("- `").Append(recipe.Id).Append("`: ")
              .Append(SlotList(data, recipe.Inputs))
              .Append(" -> ")
              .Append(SlotList(data, recipe.Outputs))
              .Append(" at ").Append(recipe.WorkplaceType)
              .Append('\n');
        }

        sb.Append('\n');
    }

    private static string SlotList(MasterDataSet data, ImmutableArray<RecipeSlot> slots)
    {
        if (slots.Length == 0)
        {
            return "nothing";
        }

        var sb = new StringBuilder(64);

        foreach (RecipeSlot slot in slots)
        {
            if (sb.Length > 0)
            {
                sb.Append(" + ");
            }

            sb.Append(slot.Count.ToString(CultureInfo.InvariantCulture))
              .Append(' ')
              .Append(data.Items[slot.Item].Id);
        }

        return sb.ToString();
    }

    /// <summary>비트마스크를 표 칸에 넣을 이름 목록으로. 비어 있으면 대시.</summary>
    private static string FlagList(WorldFlags flags)
    {
        if (flags == WorldFlags.None)
        {
            return "-";
        }

        var sb = new StringBuilder(64);

        for (int i = 0; i < WorldFlagTable.Values.Length; i++)
        {
            if ((flags & WorldFlagTable.Values[i]) == 0)
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append(", ");
            }

            sb.Append(WorldFlagTable.Names[i]);
        }

        return sb.ToString();
    }

    /// <summary>설명의 줄바꿈을 공백으로 눕힌다. 표·목록 안에서 줄이 갈라지면 마크다운이 깨진다.</summary>
    private static string Compact(string text) =>
        text.Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

    private static string CategoryName(ActionCategory category) => category switch
    {
        ActionCategory.Movement => "movement",
        ActionCategory.Labor => "labor",
        ActionCategory.Social => "social",
        ActionCategory.Needs => "needs",
        ActionCategory.Combat => "combat",
        ActionCategory.Items => "items",
        _ => "misc",
    };

    private static string TypeName(ParamType type) => type switch
    {
        ParamType.PoiRef => "poi_ref",
        ParamType.ItemRef => "item_ref",
        ParamType.NpcRef => "npc_ref",
        ParamType.ZoneRef => "zone_ref",
        ParamType.Enum => "enum",
        ParamType.Int => "int",
        _ => "route",
    };
}
