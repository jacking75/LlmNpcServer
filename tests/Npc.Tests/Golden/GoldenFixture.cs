using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Npc.Contracts;
using Npc.Core;
using Npc.MasterData;

namespace Npc.Tests.Golden;

/// <summary>
/// 골든 픽스처 하나의 단언. docs/15 §2.
///
/// <b>필드는 <see cref="Kind"/> 마다 다르게 쓰인다.</b> 하나의 record 로 합쳐 둔 것은
/// 픽스처가 JSON 이라 종류마다 타입을 나누면 로더가 다형 역직렬화를 해야 하기 때문이다 —
/// 종류가 9개뿐이고 전부 이 파일에서만 읽히므로 판별 유니온을 만들 이유가 없다.
/// </summary>
/// <param name="Kind">단언 종류. <see cref="GoldenAssertions.Kinds"/> 의 9개 중 하나.</param>
/// <param name="Action"><c>contains_action</c>·<c>not_contains</c>·<c>acquires_before_use</c> 의 대상 액션.</param>
/// <param name="Actions"><c>ends_with_any</c> 의 허용 액션 목록.</param>
/// <param name="Category"><c>produces_item_of</c> 의 아이템 분류.</param>
/// <param name="Item"><c>acquires_before_use</c> 의 대상 아이템.</param>
/// <param name="Flag"><c>avoids_flag_while</c> 가 회피해야 할 플래그.</param>
/// <param name="When"><c>avoids_flag_while</c> 의 조건 플래그.</param>
/// <param name="Bucket"><c>differs_from</c> 의 비교 대상 버킷 표기.</param>
/// <param name="Stage"><c>validates</c> 가 통과해야 할 마지막 검증 단계.</param>
/// <param name="Min"><c>step_count_between</c> 의 하한.</param>
/// <param name="Max"><c>step_count_between</c> 의 상한.</param>
public sealed record GoldenAssertionSpec(
    string Kind,
    string? Action = null,
    ImmutableArray<string> Actions = default,
    string? Category = null,
    string? Item = null,
    string? Flag = null,
    string? When = null,
    string? Bucket = null,
    string? Stage = null,
    int Min = 0,
    int Max = int.MaxValue)
{
    /// <summary>로그·리포트에 한 줄로 적는다.</summary>
    public string Describe() => Kind switch
    {
        "contains_action" or "not_contains" => $"{Kind}({Action})",
        "ends_with_any" => $"{Kind}({string.Join('/', Actions.IsDefault ? [] : Actions)})",
        "produces_item_of" => $"{Kind}({Category})",
        "acquires_before_use" => $"{Kind}({Item} → {Action})",
        "avoids_flag_while" => $"{Kind}({Flag} while {When})",
        "differs_from" => $"{Kind}({Bucket})",
        "step_count_between" => $"{Kind}({Min}..{Max})",
        "validates" => $"{Kind}({Stage ?? "DryRun"})",
        _ => Kind,
    };
}

/// <summary>
/// 골든 픽스처 하나. docs/15 §2 의 <c>tests/golden/*.json</c>.
///
/// <b>속성 단언만 담는다.</b> 정확한 문자열 비교를 하는 필드는 이 타입에 없다 —
/// LLM 출력은 매번 다르고 그게 정상이라, "무기를 만든다"는 보장하되
/// "정확히 3자루를 만든다"는 보장하지 않는다.
/// </summary>
public sealed record GoldenFixture
{
    /// <summary>픽스처 id. <c>G-014</c> 형식.</summary>
    public required string Id { get; init; }

    /// <summary><c>blacksmith@Evening.War.Cold</c> 원문. 리포트에 그대로 쓴다.</summary>
    public required string BucketText { get; init; }

    /// <summary>파싱된 버킷 키.</summary>
    public required BucketKey Bucket { get; init; }

    /// <summary>아키타입 문자열 id.</summary>
    public required string Archetype { get; init; }

    /// <summary>
    /// 픽스처가 선언한 시작 플래그.
    /// <b>마스터데이터가 유도하는 값과 대조된다</b> — 픽스처가 있을 수 없는 상황을 적으면
    /// 검증기가 반려할 플랜을 요구하게 되고 그건 픽스처 버그다.
    /// </summary>
    public required WorldFlags Flags { get; init; }

    /// <summary>시작 인벤토리. <c>acquires_before_use</c> 가 "이미 갖고 있는가"를 여기서 본다.</summary>
    public required ImmutableArray<InventorySlot> Inventory { get; init; }

    /// <summary>이 픽스처의 단언.</summary>
    public required ImmutableArray<GoldenAssertionSpec> Assertions { get; init; }

    /// <summary>원본 파일 경로. 실패 리포트에 적는다.</summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>이 아이템을 몇 개 가지고 시작하는가.</summary>
    public int StartingCount(ItemId item)
    {
        foreach (InventorySlot slot in Inventory)
        {
            if (slot.Item == item)
            {
                return slot.Count;
            }
        }

        return 0;
    }

    /// <summary>파일 하나를 읽는다.</summary>
    public static GoldenFixture Load(string path, MasterDataSet data)
    {
        ArgumentNullException.ThrowIfNull(data);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));

        return Parse(document.RootElement, data, path);
    }

    /// <summary>
    /// <c>tests/golden/</c> 전량을 <b>파일명 정렬 순서로</b> 읽는다 —
    /// 디렉터리 순회 순서에 의존하면 리포트의 순서가 기계마다 달라진다 (CLAUDE.md §2.3).
    /// </summary>
    public static ImmutableArray<GoldenFixture> LoadAll(string directory, MasterDataSet data)
    {
        string[] files = [.. Directory.GetFiles(directory, "*.json")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)];

        var builder = ImmutableArray.CreateBuilder<GoldenFixture>(files.Length);

        foreach (string file in files)
        {
            builder.Add(Load(file, data));
        }

        return builder.ToImmutable();
    }

    /// <summary>JSON 한 덩어리를 픽스처로. 테스트가 인라인 JSON 으로도 쓴다.</summary>
    public static GoldenFixture Parse(JsonElement root, MasterDataSet data, string sourcePath = "")
    {
        ArgumentNullException.ThrowIfNull(data);

        JsonElement input = root.GetProperty("input");
        string bucketText = input.GetProperty("bucket").GetString()
            ?? throw new InvalidDataException($"{sourcePath}: input.bucket 이 없다.");

        (string archetype, BucketKey bucket) = ParseBucket(bucketText, data, sourcePath);

        WorldFlags flags = WorldFlags.None;

        if (input.TryGetProperty("flags", out JsonElement flagList))
        {
            foreach (JsonElement flag in flagList.EnumerateArray())
            {
                string name = flag.GetString() ?? string.Empty;

                if (!WorldFlagTable.TryParse(name, out WorldFlags parsed))
                {
                    throw new InvalidDataException($"{sourcePath}: world_flags.json 에 없는 플래그 '{name}'.");
                }

                flags |= parsed;
            }
        }

        var inventory = ImmutableArray.CreateBuilder<InventorySlot>();

        if (input.TryGetProperty("inventory", out JsonElement bag))
        {
            // 파일의 등장 순서가 아니라 id 정렬 순서로 담는다 — 리포트가 재현돼야 한다.
            foreach (JsonProperty slot in bag.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                if (!data.Items.TryGet(slot.Name, out ItemDef item))
                {
                    throw new InvalidDataException($"{sourcePath}: items.json 에 없는 아이템 '{slot.Name}'.");
                }

                inventory.Add(new InventorySlot(item.Code, slot.Value.GetInt32()));
            }
        }

        var assertions = ImmutableArray.CreateBuilder<GoldenAssertionSpec>();

        foreach (JsonElement element in root.GetProperty("assertions").EnumerateArray())
        {
            assertions.Add(ParseAssertion(element, sourcePath));
        }

        return new GoldenFixture
        {
            Id = root.GetProperty("id").GetString() ?? Path.GetFileNameWithoutExtension(sourcePath),
            BucketText = bucketText,
            Bucket = bucket,
            Archetype = archetype,
            Flags = flags,
            Inventory = inventory.ToImmutable(),
            Assertions = assertions.ToImmutable(),
            SourcePath = sourcePath,
        };
    }

    /// <summary><c>blacksmith@Evening.War.Cold</c> → <see cref="BucketKey"/>. <c>BucketKey.Format</c> 의 역함수다.</summary>
    public static (string Archetype, BucketKey Bucket) ParseBucket(
        string text, MasterDataSet data, string sourcePath = "")
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrEmpty(text);

        int at = text.IndexOf('@', StringComparison.Ordinal);

        if (at <= 0)
        {
            throw new InvalidDataException($"{sourcePath}: 버킷 표기가 'archetype@Time.Region.Climate' 가 아니다: {text}");
        }

        string archetype = text[..at];
        string[] parts = text[(at + 1)..].Split('.');

        if (parts.Length != 3)
        {
            throw new InvalidDataException($"{sourcePath}: 버킷 표기의 뒤가 'Time.Region.Climate' 가 아니다: {text}");
        }

        if (!data.Archetypes.TryGet(archetype, out ArchetypeDef def))
        {
            throw new InvalidDataException($"{sourcePath}: archetypes.json 에 없는 아키타입 '{archetype}'.");
        }

        return (
            archetype,
            new BucketKey(
                def.Code,
                Enum.Parse<TimeOfDay>(parts[0]),
                Enum.Parse<RegionState>(parts[1]),
                Enum.Parse<Climate>(parts[2])));
    }

    private static GoldenAssertionSpec ParseAssertion(JsonElement element, string sourcePath)
    {
        string kind = element.GetProperty("kind").GetString()
            ?? throw new InvalidDataException($"{sourcePath}: 단언에 kind 가 없다.");

        if (!GoldenAssertions.Kinds.Contains(kind, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"{sourcePath}: 모르는 단언 종류 '{kind}'. 허용: {string.Join(", ", GoldenAssertions.Kinds)}");
        }

        ImmutableArray<string> actions = element.TryGetProperty("actions", out JsonElement list)
            ? [.. list.EnumerateArray().Select(a => a.GetString()!)]
            : default;

        return new GoldenAssertionSpec(
            kind,
            Text(element, "action"),
            actions,
            Text(element, "category"),
            Text(element, "item"),
            Text(element, "flag"),
            Text(element, "when"),
            Text(element, "bucket"),
            Text(element, "stage"),
            Number(element, "min", 0),
            Number(element, "max", int.MaxValue));

        static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out JsonElement value) ? value.GetString() : null;

        static int Number(JsonElement element, string name, int fallback) =>
            element.TryGetProperty(name, out JsonElement value)
                ? value.GetInt32()
                : fallback;
    }

    /// <summary>실패 리포트 한 줄.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Id} {BucketText} ({Assertions.Length} assertions)");
}
