using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npc.Core;

namespace Npc.MasterData;

/// <summary>콘텐츠 작성자가 바꿀 수 없는 엔진 코어 어휘와 빌드 일치 검사.</summary>
public static partial class CoreVocabulary
{
    /// <summary>코드가 이름으로 참조하는 플래그. 새 참조를 넣으면 이 목록도 갱신한다.</summary>
    public static readonly string[] Flags =
    [
        "AtField", "AtGate", "AtHome", "AtMarket", "AtTavern", "AtTemple", "AtWorkplace",
        "HasRawMaterial", "HostilePlayerNearby", "InCombat", "InventoryFull", "InWilderness",
        "IsDawn", "IsDay", "IsEvening", "IsExhausted", "IsInjured", "IsNight", "IsRested",
        "OnDuty", "PlayerNearby", "RegionPeaceful", "RegionUnderAttack", "ThreatNearby",
        "WeatherHarsh",
    ];

    /// <summary>플랜 바인더가 직접 해석하는 POI 역할.</summary>
    public static readonly string[] PoiRoles =
    [
        "$home", "$workplace", "$market", "$tavern", "$temple", "$gate",
        "$nearest_field", "$nearest_safe", "$nearest_shelter", "$patrol_route",
    ];

    /// <summary>버킷 축. 값의 순서를 바꾸면 기존 플랜 키가 달라진다.</summary>
    public static readonly string[] TimeOfDay = ["Dawn", "Morning", "Noon", "Afternoon", "Evening", "Night"];
    public static readonly string[] RegionStates = ["Peace", "Alert", "War", "Disaster"];
    public static readonly string[] Climates = ["Fair", "Cold", "Storm"];

    /// <summary>시뮬레이션·소요 예측이 이름으로 조회하는 액션.</summary>
    public static readonly string[] Actions = ["MoveTo", "Gather"];

    /// <summary>world_flags.json 이 이 바이너리를 빌드할 때 사용한 이름·bit 와 같은가.</summary>
    public static string? WorldFlagsMismatch(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        var actual = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (JsonElement flag in document.RootElement.GetProperty("flags").EnumerateArray())
        {
            actual.Add(flag.GetProperty("id").GetString()!, flag.GetProperty("bit").GetInt32());
        }

        var built = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < WorldFlagTable.Count; i++)
        {
            built.Add(WorldFlagTable.Names[i],
                BitOperations.TrailingZeroCount((ulong)WorldFlagTable.Values[i]));
        }

        string[] added = [.. actual.Keys.Except(built.Keys).Order(StringComparer.Ordinal)];
        string[] removed = [.. built.Keys.Except(actual.Keys).Order(StringComparer.Ordinal)];
        string[] moved = [.. actual.Keys.Intersect(built.Keys)
            .Where(name => actual[name] != built[name])
            .Select(name => $"{name}:{built[name]}→{actual[name]}")
            .Order(StringComparer.Ordinal)];

        return added.Length == 0 && removed.Length == 0 && moved.Length == 0
            ? null
            : "이 폴더의 world_flags.json 이 빌드에 쓰인 것과 다르다: "
                + $"+{string.Join(',', added)}, −{string.Join(',', removed)}, "
                + $"bit 변경 {string.Join(',', moved)}. "
                + "저장소 masterdata/world_flags.json 을 이것으로 바꾸고 다시 빌드한다.";
    }

    /// <summary>어긋난 플래그는 다른 파일을 읽기 전에 이해할 수 있는 오류로 막는다.</summary>
    public static void RequireBuiltWorldFlags(string directory)
    {
        string path = Path.Combine(directory, "world_flags.json");
        if (!File.Exists(path)) throw new FileNotFoundException("world_flags.json 이 없다.", path);
        try
        {
            if (WorldFlagsMismatch(path) is { } error) throw new InvalidDataException(error);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"world_flags.json 을 읽지 못했다: {e.Message}", e);
        }
    }

    /// <summary>소스에서 등록되지 않은 WorldFlags 참조 이름을 찾는다.</summary>
    public static string[] UnregisteredFlagReferences(string source) =>
        [.. FlagReferenceRegex().Matches(source).Select(match => match.Groups[1].Value)
            .Where(name => name != "None" && !Flags.Contains(name, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>도입자에게 보여 줄 설명. 모든 이름은 엔진 코드가 직접 해석한다.</summary>
    public static string Explain() =>
        "# 엔진 코어 어휘\n\n"
        + "월드 플래그: " + string.Join(", ", Flags) + "\n"
        + "이 플래그들은 인지·검증·계획 코드가 이름으로 읽는다. world_flags.json 은 빌드와 일치해야 한다.\n\n"
        + "POI 역할: " + string.Join(", ", PoiRoles) + "\n"
        + "플랜의 심볼을 NPC마다 실제 POI로 바인딩한다.\n\n"
        + "버킷 시간대: " + string.Join(", ", TimeOfDay) + "\n"
        + "버킷 지역 상태: " + string.Join(", ", RegionStates) + "\n"
        + "버킷 기후: " + string.Join(", ", Climates) + "\n"
        + "세 축의 값과 순서는 플랜 캐시 키다.\n\n"
        + "코어 액션: " + string.Join(", ", Actions) + "\n"
        + "MoveTo는 이동 소요 예측, Gather는 기본 작업 소요 예측에서 이름으로 찾는다.\n";

    [GeneratedRegex(@"\bWorldFlags\.([A-Z][A-Za-z0-9_]*)\b")]
    private static partial Regex FlagReferenceRegex();
}
