using System.Text.Json;
using System.Text.Json.Nodes;

namespace Spike;

/// <summary>버킷 키. docs/01 §6 의 `{archetype}@{time_of_day}.{region_state}.{climate}`.</summary>
internal readonly record struct Bucket(string Archetype, string TimeOfDay, string RegionState, string Climate)
{
    public override string ToString() => $"{Archetype}@{TimeOfDay}.{RegionState}.{Climate}";
}

/// <summary>
/// T0-07 — 가변 서픽스 생성기. docs/12 §3 의 형식과 250~300 토큰 예산을 따른다.
///
/// **같은 서픽스를 반복하지 않는다.** 100번 같은 걸 던지면 캐시가 응답까지 재활용해
/// 측정이 통째로 무의미해진다.
/// </summary>
internal static class SuffixGen
{
    // 축소판이므로 아키타입 4종만 쓴다 (정식 40종은 P1).
    public static readonly string[] Archetypes = ["blacksmith", "farmer", "merchant", "town_guard"];
    public static readonly string[] TimesOfDay = ["Dawn", "Morning", "Noon", "Afternoon", "Evening", "Night"];
    public static readonly string[] RegionStates = ["Peace", "Alert", "War", "Disaster"];
    public static readonly string[] Climates = ["Fair", "Cold", "Storm"];

    /// <summary>4 × 6 × 4 × 3 = 288. 작업 지시서의 (4 × 6 × 4) 는 96 뿐이라 100종을 못 뽑는다.</summary>
    public static int SpaceSize => Archetypes.Length * TimesOfDay.Length * RegionStates.Length * Climates.Length;

    private static readonly Dictionary<string, (int Diligence, int Sociability, int Courage, int Greed)> Traits = new()
    {
        ["blacksmith"] = (80, 40, 55, 50),
        ["farmer"] = (70, 35, 30, 40),
        ["merchant"] = (55, 85, 35, 75),
        ["town_guard"] = (65, 45, 80, 25),
    };

    private static readonly Dictionary<string, string[]> Goals = new()
    {
        ["blacksmith"] = ["restock_ore", "fulfill_orders", "maintain_shop"],
        ["farmer"] = ["harvest_crops", "stock_pantry", "tend_field"],
        ["merchant"] = ["move_stock", "meet_customers", "track_prices"],
        ["town_guard"] = ["hold_the_gate", "patrol_streets", "protect_civilians"],
    };

    /// <summary>시간대별로 NPC 가 보통 있는 곳. 여기서 초기 위치 플래그가 나온다.</summary>
    private static readonly Dictionary<string, string[]> WhereByTime = new()
    {
        //                     Dawn          Morning        Noon           Afternoon      Evening       Night
        ["blacksmith"] = ["AtHome", "AtWorkplace", "AtWorkplace", "AtWorkplace", "AtMarket", "AtHome"],
        ["farmer"] = ["AtHome", "AtField", "AtField", "AtField", "AtHome", "AtHome"],
        ["merchant"] = ["AtHome", "AtMarket", "AtMarket", "AtMarket", "AtTavern", "AtHome"],
        ["town_guard"] = ["AtWorkplace", "AtWorkplace", "AtHome", "AtHome", "AtWorkplace", "AtWorkplace"],
    };

    // ---------------------------------------------------------------- 샘플링
    /// <summary>
    /// 288 조합에서 <paramref name="count"/> 종을 결정론적으로 고른다.
    /// 91 과 288 은 서로소라 인덱스가 겹치지 않는다 — 난수를 쓰면 재현이 안 된다 (CLAUDE.md §2.3).
    /// </summary>
    public static Bucket[] Sample(int count)
    {
        var space = SpaceSize;
        var result = new Bucket[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = FromIndex(i * 91 % space);
        }

        return result;
    }

    public static Bucket FromIndex(int index)
    {
        var c = index % Climates.Length;
        var r = index / Climates.Length % RegionStates.Length;
        var t = index / (Climates.Length * RegionStates.Length) % TimesOfDay.Length;
        var a = index / (Climates.Length * RegionStates.Length * TimesOfDay.Length) % Archetypes.Length;
        return new Bucket(Archetypes[a], TimesOfDay[t], RegionStates[r], Climates[c]);
    }

    // ---------------------------------------------------------------- 서픽스 본문
    /// <summary>
    /// 압축 원칙 (docs/12 §3): 값은 열거형 문자열만, 설명 문장 금지, 참인 플래그만 나열.
    /// </summary>
    public static string Build(in Bucket b)
    {
        var flags = FlagsFor(b);
        var (dil, soc, cou, gre) = Traits[b.Archetype];

        var obj = new JsonObject
        {
            ["archetype"] = b.Archetype,
            ["time_of_day"] = b.TimeOfDay,
            ["region_state"] = b.RegionState,
            ["climate"] = b.Climate,
            ["flags"] = new JsonArray([.. flags.Select(f => (JsonNode)f!)]),
            ["traits"] = new JsonObject
            {
                ["diligence"] = dil,
                ["sociability"] = soc,
                ["courage"] = cou,
                ["greed"] = gre,
            },
            ["goals"] = new JsonArray([.. Goals[b.Archetype].Select(g => (JsonNode)g!)]),
        };

        var json = obj.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        return $"""

            # REQUEST

            {json}

            Output the plan JSON for this request. JSON only.
            """;
    }

    /// <summary>버킷에서 초기 월드 플래그를 유도한다. 전부 결정론적이다.</summary>
    public static string[] FlagsFor(in Bucket b)
    {
        var flags = new List<string>(8);

        var timeIndex = Array.IndexOf(TimesOfDay, b.TimeOfDay);
        flags.Add(WhereByTime[b.Archetype][timeIndex]);

        // 직업 소지품
        switch (b.Archetype)
        {
            case "blacksmith":
            case "farmer":
                flags.Add("HasTool");
                break;
            case "merchant":
                flags.Add("HasCoin");
                break;
            case "town_guard":
                flags.Add("HasFood");
                break;
        }

        if (b.TimeOfDay == "Night")
        {
            flags.Add("IsNight");
        }

        if (b.TimeOfDay is "Dawn" or "Morning")
        {
            flags.Add("IsRested");
        }

        if (b.RegionState is "War" or "Disaster")
        {
            flags.Add("RegionUnderAttack");
        }

        if (b.Climate is "Storm")
        {
            flags.Add("WeatherHarsh");
        }

        // 개체 상태는 버킷 해시로 흔든다 — Random 을 쓰면 재현이 안 된다.
        var h = Fnv1a(b.ToString());
        if (h % 3 == 0)
        {
            flags.Add("IsHungry");
        }

        if (h % 5 is 0 or 1)
        {
            flags.Add("HasFood");
        }

        if (h % 4 == 0 && b.Archetype is "blacksmith" or "farmer")
        {
            flags.Add("HasRawMaterial");
        }

        if (h % 7 == 0 && b.Archetype is "blacksmith" or "merchant")
        {
            flags.Add("HasProduct");
        }

        // 순서를 고정한다 — 집합 순회 순서에 의존하면 서픽스가 흔들린다.
        return [.. flags.Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal)];
    }

    private static uint Fnv1a(string s)
    {
        uint h = 2166136261;
        foreach (var ch in s)
        {
            h = (h ^ ch) * 16777619;
        }

        return h;
    }

    // ---------------------------------------------------------------- 완료 조건
    /// <summary>`spike suffix` — 100종이 서로 다른지, 각 토큰 수가 300 이하인지 확인한다.</summary>
    public static int Run()
    {
        var buckets = Sample(100);
        var suffixes = buckets.Select(b => Build(b)).ToArray();

        var distinctBuckets = buckets.Distinct().Count();
        var distinctText = suffixes.Distinct(StringComparer.Ordinal).Count();

        var tokens = suffixes.Select(PromptPrefix.CountTokens).ToArray();
        var max = tokens.Max();
        var avg = tokens.Average();

        Console.WriteLine($"space          = {SpaceSize} buckets (4 archetypes x 6 times x 4 region states x 3 climates)");
        Console.WriteLine($"sampled        = {buckets.Length}");
        Console.WriteLine($"distinct bucket= {distinctBuckets}");
        Console.WriteLine($"distinct text  = {distinctText}");
        Console.WriteLine($"tokens         = min {tokens.Min()} / avg {avg:F1} / max {max}");
        Console.WriteLine();
        Console.WriteLine("first 3 samples:");
        for (var i = 0; i < 3; i++)
        {
            Console.WriteLine($"--- {buckets[i]}");
            Console.WriteLine(suffixes[i].Trim());
        }

        var allDistinct = distinctBuckets == 100 && distinctText == 100;
        var inBudget = max <= 300;
        Console.WriteLine();
        Console.WriteLine($"100종이 서로 다름 : {(allDistinct ? "PASS" : "FAIL")}");
        Console.WriteLine($"각 토큰 수 <= 300 : {(inBudget ? "PASS" : "FAIL")}");

        return allDistinct && inBudget ? 0 : 1;
    }
}
