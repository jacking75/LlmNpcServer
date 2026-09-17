using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npc.Contracts;
using Npc.Core;

namespace Npc.MasterData;

/// <summary>
/// 로드 옵션 (H07). <b>기본값이 예전 동작이다</b> — 기동 경로는 이것을 주지 않는다.
/// </summary>
public sealed record MasterDataLoadOptions
{
    /// <summary>
    /// 거리표(<c>poi_distances.bin</c>)를 읽지 않는다.
    ///
    /// <para>
    /// <b>편집 도구 전용이다.</b> 장소를 하나 더하면 거리표는 정의상 낡고, 그때 로더가
    /// 정확히 거절한다 — 그런데 그 낡음을 없애려면 먼저 장소를 저장해야 하므로 Studio 는
    /// "다시 만들라" 고 말해 줄 화면을 띄워야 한다. 이 옵션이 그 화면을 그릴 만큼만 읽게 한다.
    /// 거리는 전부 0 이 되므로 <b>소요 예측을 신뢰할 수 없다</b>.
    /// </para>
    /// </summary>
    public bool SkipDistances { get; init; }
}

/// <summary>
/// masterdata/ 를 통째로 읽어 읽기 전용 인덱스로 만든다. docs/01 §11.
///
/// 로딩 규약: 전량 로드 → 스키마 검증 → 참조 무결성 검증 → 인덱스 컴파일 → 이후 불변.
/// 참조가 깨져 있으면 여기서 <see cref="InvalidDataException"/> 을 던진다. 경고 후 진행은 없다.
/// 규칙 단위 검증(V1~V13)은 <c>MasterDataValidator</c> 가 따로 본다.
/// </summary>
public static class MasterDataLoader
{
    /// <summary>해시에 넣는 파일 목록. 순서를 고정해야 ContentHash 가 결정론이다.</summary>
    private static readonly string[] s_hashedFiles =
    [
        "actions.json",
        "archetypes.json",
        "context_buckets.json",
        "dialogue_lines.json",
        "factions.json",
        "fallback_plans.json",
        "interrupts.json",
        "items.json",
        "poi_distances.bin",
        "pois.json",
        "world_flags.json",
        "zones.json",
    ];

    /// <summary>masterdata 폴더 전체 로드.</summary>
    /// <param name="masterDataDirectory">masterdata 경로.</param>
    public static MasterDataSet Load(string masterDataDirectory) => Load(masterDataDirectory, null);

    /// <summary>
    /// masterdata 폴더 전체 로드. 옵션을 준다 (H07).
    ///
    /// <para>
    /// <b>기본값은 예전 그대로다</b> — 기동 경로(<c>Npc.Host</c>)는 이 인자를 주지 않으므로
    /// 거리표가 낡으면 예전처럼 던진다. 옵션을 켜는 것은 <b>편집 도구뿐</b>이다: Studio 는
    /// "거리표를 다시 만들라" 고 말해 줄 화면을 띄워야 하는데, 그 화면을 그리려면 먼저
    /// 마스터데이터를 읽을 수 있어야 한다.
    /// </para>
    /// </summary>
    /// <param name="masterDataDirectory">masterdata 경로.</param>
    /// <param name="options">로드 옵션. null 이면 엄격 모드다.</param>
    public static MasterDataSet Load(string masterDataDirectory, MasterDataLoadOptions? options)
    {
        if (!Directory.Exists(masterDataDirectory))
        {
            throw new DirectoryNotFoundException($"masterdata 폴더를 찾지 못했다: {masterDataDirectory}");
        }

        bool skipDistances = options?.SkipDistances == true;

        string Path_(string name) => Path.Combine(masterDataDirectory, name);

        ItemTable items = ItemTable.Load(Path_("items.json"));

        // D-02 — 대사 code 는 파일이 정한다. 파일이 없으면 옛 방식(사전순 첨자)으로 떨어진다:
        // 이 저장소에는 항상 있지만, 최소 픽스처로 도는 테스트가 그 경로를 쓴다.
        string dialoguePath = Path_(DialogueTable.FileName);
        DialogueTable? dialogues = File.Exists(dialoguePath) ? DialogueTable.Load(dialoguePath) : null;

        // D-04 — 세력 표. 없으면 null 이고, 그때 npc_overrides.json 의 faction 은 쓸 수 없다.
        string factionPath = Path_(FactionTable.FileName);
        FactionTable? factions = File.Exists(factionPath) ? FactionTable.Load(factionPath) : null;

        ActionCatalog actions = ActionCatalog.Load(Path_("actions.json"), items, dialogues);
        ArchetypeTable archetypes = ArchetypeTable.Load(Path_("archetypes.json"), actions, items);
        ZoneTable zones = LoadZones(Path_("zones.json"));
        PoiTable pois = LoadPois(
            Path_("pois.json"), Path_("poi_distances.bin"), zones, archetypes, items, skipDistances);
        BucketSpace buckets = LoadBuckets(Path_("context_buckets.json"), archetypes.Count);
        InterruptRules interrupts = InterruptRules.Load(Path_("interrupts.json"), actions);

        ImmutableArray<FileHash> hashes = HashFiles(masterDataDirectory, includeDistances: !skipDistances);

        var set = new MasterDataSet
        {
            Dialogues = dialogues,

            // D-02 — 표시 문구. 없으면 빈 배열이고, 그때 표시 계층은 키를 그대로 쓴다.
            Locales = LocalizationTable.LoadAll(masterDataDirectory),

            // D-04 — 세력 code. npc_overrides.json 의 faction 이 이 표로 풀린다.
            Factions = factions,
            Actions = actions,
            Items = items,
            Zones = zones,
            Pois = pois,
            Archetypes = archetypes,
            Buckets = buckets,
            Interrupts = interrupts,
            FileHashes = hashes,
            ContentHash = CombineHashes(hashes),
            StructuralHash = MasterData.StructuralHash.Compute(
                actions, items, zones, pois, archetypes, buckets, dialogues, factions),

            // 파생물 신선도 (F-04). 여기서 던지지 않는다 — 호출부가 경고로 낸다.
            StaleArtifacts = Authoring.DerivedArtifacts.Stale(masterDataDirectory),

            // H07 — 거리표를 건너뛰고 읽었는가. 참이면 거리가 전부 0 이라 소요 예측이 거짓이다.
            DistancesAvailable = !skipDistances,
        };

        // 폴백 플랜은 MasterDataSet 자신을 어휘로 써서 검증·컴파일하므로 나중에 붙인다.
        // 파일이 없으면 null 이다 — PlanStore 가 최후 플랜으로 대신한다 (T1-39).
        string fallbackPath = Path_("fallback_plans.json");
        if (File.Exists(fallbackPath))
        {
            set.Fallbacks = PlanTable.Load(fallbackPath, set);
        }

        return set;
    }

    // ---------------------------------------------------------------- 해시

    /// <summary>
    /// 파일별 SHA-256. 줄 끝은 LF 로 통일해서 잰다 —
    /// CRLF 로 체크아웃된 기계에서 해시가 달라지면 프리베이크가 통째로 무효가 된다.
    /// 바이너리(.bin)는 바이트 그대로 잰다.
    /// </summary>
    public static ImmutableArray<FileHash> HashFiles(string masterDataDirectory) =>
        HashFiles(masterDataDirectory, includeDistances: true);

    /// <summary>파일별 SHA-256. 거리표를 건너뛰고 읽었으면 그 해시도 뺀다 (H07).</summary>
    /// <param name="masterDataDirectory">masterdata 경로.</param>
    /// <param name="includeDistances">거리표를 해시에 넣을 것인가.</param>
    public static ImmutableArray<FileHash> HashFiles(string masterDataDirectory, bool includeDistances)
    {
        var builder = ImmutableArray.CreateBuilder<FileHash>(s_hashedFiles.Length);

        foreach (string name in s_hashedFiles)
        {
            if (!includeDistances && name == "poi_distances.bin")
            {
                continue;
            }

            string path = Path.Combine(masterDataDirectory, name);
            if (!File.Exists(path))
            {
                // fallback_plans.json 은 T1-54 전에는 없을 수 있다. 있으면 해시에 넣는다.
                if (name == "fallback_plans.json")
                {
                    continue;
                }

                throw new FileNotFoundException($"마스터데이터 파일이 없다: {name}", path);
            }

            byte[] bytes = name.EndsWith(".bin", StringComparison.Ordinal)
                ? File.ReadAllBytes(path)
                : Encoding.UTF8.GetBytes(NormalizeText(File.ReadAllText(path)));

            builder.Add(new FileHash(name, Convert.ToHexStringLower(SHA256.HashData(bytes))));
        }

        return builder.ToImmutable();
    }

    /// <summary>파일별 해시를 하나로 접는다. 파일명 오름차순이라 순서가 결정론이다.</summary>
    public static string CombineHashes(ImmutableArray<FileHash> hashes)
    {
        var sb = new StringBuilder(hashes.Length * 80);

        foreach (FileHash hash in hashes.Sort((a, b) => string.CompareOrdinal(a.FileName, b.FileName)))
        {
            sb.Append(hash.FileName).Append(':').Append(hash.Sha256).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static string NormalizeText(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    // ---------------------------------------------------------------- zones.json

    private static ZoneTable LoadZones(string path)
    {
        using FileStream stream = File.OpenRead(path);
        ZonesFile? file = JsonSerializer.Deserialize(stream, WorldJsonContext.Default.ZonesFile);

        if (file?.Zones is null || file.Zones.Length == 0)
        {
            throw new InvalidDataException($"zones.json 에서 존을 읽지 못했다: {path}");
        }

        int maxCode = 0;
        foreach (ZoneDto dto in file.Zones)
        {
            maxCode = Math.Max(maxCode, dto.Code);
        }

        // 1단 — id → code
        var codeById = new Dictionary<string, int>(file.Zones.Length, StringComparer.Ordinal);
        foreach (ZoneDto dto in file.Zones)
        {
            if (!codeById.TryAdd(dto.Id, dto.Code))
            {
                throw new InvalidDataException($"zones.json: id '{dto.Id}' 가 중복이다.");
            }
        }

        // 2단 — 인접 해석
        var byCode = new ZoneDef?[maxCode + 1];
        var byId = new Dictionary<string, ZoneDef>(file.Zones.Length, StringComparer.Ordinal);
        var builder = ImmutableArray.CreateBuilder<ZoneDef>(file.Zones.Length);

        foreach (ZoneDto dto in file.Zones)
        {
            if (dto.Code is < 0 or > ushort.MaxValue)
            {
                throw new InvalidDataException($"zones.json: {dto.Id} 의 code {dto.Code} 가 범위를 벗어난다.");
            }

            if (byCode[dto.Code] is not null)
            {
                throw new InvalidDataException($"zones.json: code {dto.Code} 가 중복이다 ({dto.Id}).");
            }

            var adjacent = ImmutableArray.CreateBuilder<ZoneId>();
            foreach (string neighbour in (dto.Adjacent ?? []).Order(StringComparer.Ordinal))
            {
                if (!codeById.TryGetValue(neighbour, out int code))
                {
                    throw new InvalidDataException($"zones.json: {dto.Id} 가 없는 존 '{neighbour}' 을 인접으로 든다.");
                }

                adjacent.Add(new ZoneId((ushort)code));
            }

            var def = new ZoneDef(
                new ZoneId((ushort)dto.Code),
                dto.Id,
                dto.NameKey,
                adjacent.ToImmutable(),
                ParseEnum<RegionState>(dto.DefaultRegionState, $"zones.json:{dto.Id}.default_region_state"),
                ParseEnum<Climate>(dto.DefaultClimate, $"zones.json:{dto.Id}.default_climate"),
                dto.Capacity);

            byCode[dto.Code] = def;
            byId[dto.Id] = def;
            builder.Add(def);
        }

        builder.Sort((a, b) => a.Code.Value.CompareTo(b.Code.Value));

        return new ZoneTable(byCode, byId, builder.ToImmutable());
    }

    // ---------------------------------------------------------------- pois.json + poi_distances.bin

    private static PoiTable LoadPois(
        string poisPath,
        string distancesPath,
        ZoneTable zones,
        ArchetypeTable archetypes,
        ItemTable items,
        bool skipDistances)
    {
        using FileStream stream = File.OpenRead(poisPath);
        PoisFile? file = JsonSerializer.Deserialize(stream, WorldJsonContext.Default.PoisFile);

        if (file?.Pois is null || file.Pois.Length == 0)
        {
            throw new InvalidDataException($"pois.json 에서 POI 를 읽지 못했다: {poisPath}");
        }

        int maxCode = 0;
        foreach (PoiDto dto in file.Pois)
        {
            maxCode = Math.Max(maxCode, dto.Code);
        }

        var byCode = new PoiDef?[maxCode + 1];
        var byId = new Dictionary<string, PoiDef>(file.Pois.Length, StringComparer.Ordinal);
        var builder = ImmutableArray.CreateBuilder<PoiDef>(file.Pois.Length);

        foreach (PoiDto dto in file.Pois)
        {
            if (dto.Code is < 1 or > ushort.MaxValue)
            {
                throw new InvalidDataException($"pois.json: {dto.Id} 의 code {dto.Code} 가 범위를 벗어난다.");
            }

            if (byCode[dto.Code] is not null)
            {
                throw new InvalidDataException($"pois.json: code {dto.Code} 가 중복이다 ({dto.Id}).");
            }

            if (!zones.TryGet(dto.Zone, out ZoneDef zone))
            {
                throw new InvalidDataException($"pois.json: {dto.Id} 가 없는 존 '{dto.Zone}' 을 가리킨다.");
            }

            WorldFlags grants = WorldFlags.None;
            foreach (string flagId in dto.Grants ?? [])
            {
                if (!WorldFlagTable.TryParse(flagId, out WorldFlags flag))
                {
                    throw new InvalidDataException($"pois.json: {dto.Id} 가 없는 플래그 '{flagId}' 를 준다.");
                }

                grants |= flag;
            }

            ulong archetypeMask = 0;
            foreach (string archetypeId in dto.AllowedArchetypes ?? [])
            {
                if (!archetypes.TryGet(archetypeId, out ArchetypeDef archetype))
                {
                    throw new InvalidDataException($"pois.json: {dto.Id} 가 없는 아키타입 '{archetypeId}' 를 허용한다.");
                }

                if (archetype.Code.Value >= 64)
                {
                    throw new InvalidDataException(
                        $"pois.json: 아키타입 code 가 64 이상이면 허용 비트셋에 담을 수 없다 ({archetypeId}).");
                }

                archetypeMask |= 1UL << archetype.Code.Value;
            }

            var resources = ImmutableArray.CreateBuilder<ItemId>();
            foreach (string itemId in dto.Resources ?? [])
            {
                if (!items.TryGet(itemId, out ItemDef item))
                {
                    throw new InvalidDataException($"pois.json: {dto.Id} 가 없는 아이템 '{itemId}' 을 낸다.");
                }

                resources.Add(item.Code);
            }

            var def = new PoiDef(
                new PoiId((ushort)dto.Code),
                dto.Id,
                zone.Code,
                ParseEnum<PoiType>(dto.Type, $"pois.json:{dto.Id}.type"),
                dto.Subtype,
                new WorldPos(dto.Pos.X, dto.Pos.Y, dto.Pos.Z),
                dto.Capacity,
                ParseEnum<TimeOfDay>(dto.OpenHours?.From ?? nameof(TimeOfDay.Dawn), $"pois.json:{dto.Id}.open_hours.from"),
                ParseEnum<TimeOfDay>(dto.OpenHours?.To ?? nameof(TimeOfDay.Night), $"pois.json:{dto.Id}.open_hours.to"),
                grants,
                archetypeMask,
                resources.ToImmutable());

            byCode[dto.Code] = def;
            byId[dto.Id] = def;
            builder.Add(def);
        }

        builder.Sort((a, b) => a.Code.Value.CompareTo(b.Code.Value));
        ImmutableArray<PoiDef> pois = builder.ToImmutable();

        // H07 — 건너뛰면 0 행렬이다. 거리를 쓰는 예측·정렬은 전부 0 을 받으므로
        // <b>부르는 쪽이 "거리표가 없다" 를 화면에 적어야 한다</b> (MasterDataSet.DistancesAvailable).
        (Half[] distances, int size) = skipDistances
            ? (new Half[pois.Length * pois.Length], pois.Length)
            : LoadDistances(distancesPath, pois.Length);

        return new PoiTable(
            byCode, byId, pois, distances, size,
            GroupByZone(pois, zones.Zones.Length == 0 ? 1 : zones.Zones.Max(z => z.Code.Value) + 1),
            GroupByType(pois),
            GroupBySubtype(pois));
    }

    private static (Half[] Distances, int Size) LoadDistances(string path, int expectedCount)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "poi_distances.bin 이 없다. `dotnet run tools/gen_poi_distances.cs` 로 생성해라.", path);
        }

        byte[] bytes = File.ReadAllBytes(path);

        if (bytes.Length < 8 || bytes[0] != (byte)'P' || bytes[1] != (byte)'O' || bytes[2] != (byte)'I' || bytes[3] != (byte)'D')
        {
            throw new InvalidDataException($"poi_distances.bin 의 헤더가 'POID' 가 아니다: {path}");
        }

        int n = BitConverter.ToInt32(bytes, 4);

        if (n != expectedCount)
        {
            throw new InvalidDataException(
                $"poi_distances.bin 이 POI {n}개 기준인데 pois.json 은 {expectedCount}개다. 행렬을 다시 생성해라.");
        }

        long expectedLength = 8L + ((long)n * n * 2);
        if (bytes.Length != expectedLength)
        {
            throw new InvalidDataException(
                $"poi_distances.bin 크기가 {bytes.Length} 인데 {expectedLength} 여야 한다.");
        }

        var matrix = new Half[n * n];
        for (int i = 0; i < matrix.Length; i++)
        {
            matrix[i] = BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(bytes, 8 + (i * 2)));
        }

        return (matrix, n);
    }

    private static ImmutableArray<PoiId>[] GroupByZone(ImmutableArray<PoiDef> pois, int zoneSlots)
    {
        var lists = new List<PoiId>[zoneSlots];

        foreach (PoiDef poi in pois)
        {
            (lists[poi.Zone.Value] ??= []).Add(poi.Code);
        }

        var result = new ImmutableArray<PoiId>[zoneSlots];
        for (int i = 0; i < zoneSlots; i++)
        {
            result[i] = lists[i] is { } list ? [.. list] : [];
        }

        return result;
    }

    private static ImmutableArray<PoiId>[] GroupByType(ImmutableArray<PoiDef> pois)
    {
        int typeCount = Enum.GetValues<PoiType>().Length;
        var lists = new List<PoiId>[typeCount];

        foreach (PoiDef poi in pois)
        {
            (lists[(int)poi.Type] ??= []).Add(poi.Code);
        }

        var result = new ImmutableArray<PoiId>[typeCount];
        for (int i = 0; i < typeCount; i++)
        {
            result[i] = lists[i] is { } list ? [.. list] : [];
        }

        return result;
    }

    private static Dictionary<string, ImmutableArray<PoiId>> GroupBySubtype(ImmutableArray<PoiDef> pois)
    {
        var lists = new Dictionary<string, List<PoiId>>(StringComparer.Ordinal);

        foreach (PoiDef poi in pois)
        {
            if (!lists.TryGetValue(poi.Subtype, out List<PoiId>? list))
            {
                list = [];
                lists[poi.Subtype] = list;
            }

            list.Add(poi.Code);
        }

        var result = new Dictionary<string, ImmutableArray<PoiId>>(lists.Count, StringComparer.Ordinal);
        foreach ((string subtype, List<PoiId> list) in lists)
        {
            result[subtype] = [.. list];
        }

        return result;
    }

    // ---------------------------------------------------------------- context_buckets.json

    private static BucketSpace LoadBuckets(string path, int archetypeCount)
    {
        using FileStream stream = File.OpenRead(path);
        BucketsFile? file = JsonSerializer.Deserialize(stream, WorldJsonContext.Default.BucketsFile);

        if (file?.Dimensions is null)
        {
            throw new InvalidDataException($"context_buckets.json 을 읽지 못했다: {path}");
        }

        DimensionDto time = Dimension(file, "time_of_day");
        DimensionDto region = Dimension(file, "region_state");
        DimensionDto climate = Dimension(file, "climate");

        RequireValues<TimeOfDay>(time, "time_of_day");
        RequireValues<RegionState>(region, "region_state");
        RequireValues<Climate>(climate, "climate");

        var gameHours = new (int From, int To)[time.Values.Length];
        for (int i = 0; i < time.Values.Length; i++)
        {
            if (time.GameHours is null || !time.GameHours.TryGetValue(time.Values[i], out int[]? span) || span.Length != 2)
            {
                throw new InvalidDataException($"context_buckets.json: {time.Values[i]} 의 game_hours 가 없다.");
            }

            gameHours[i] = (span[0], span[1]);
        }

        // prebake_priority — 프리베이크 순서의 가중치 (docs/01 §6). 없는 값은 0 이다.
        var priority = new int[region.Values.Length];

        foreach (PrebakePriorityDto entry in file.PrebakePriority ?? [])
        {
            if (!Enum.TryParse(entry.RegionState, out RegionState state) || !Enum.IsDefined(state))
            {
                throw new InvalidDataException(
                    $"context_buckets.json: prebake_priority 의 '{entry.RegionState}' 가 region_state 값이 아니다.");
            }

            priority[(int)state] = entry.Weight;
        }

        return new BucketSpace(
            FlagsFor(time, "time_of_day"),
            FlagsFor(region, "region_state"),
            FlagsFor(climate, "climate"),
            gameHours,
            file.TotalKeys,
            priority,
            archetypeCount);

        static DimensionDto Dimension(BucketsFile file, string name) =>
            file.Dimensions.TryGetValue(name, out DimensionDto? d)
                ? d
                : throw new InvalidDataException($"context_buckets.json: 차원 '{name}' 이 없다.");

        static void RequireValues<TEnum>(DimensionDto dimension, string name) where TEnum : struct, Enum
        {
            if (dimension.Values.Length != Enum.GetValues<TEnum>().Length)
            {
                throw new InvalidDataException(
                    $"context_buckets.json: {name} 의 값이 {dimension.Values.Length}개인데 {typeof(TEnum).Name} 는 {Enum.GetValues<TEnum>().Length}개다.");
            }

            for (int i = 0; i < dimension.Values.Length; i++)
            {
                if (!Enum.TryParse(dimension.Values[i], out TEnum parsed)
                    || Convert.ToInt32(parsed, System.Globalization.CultureInfo.InvariantCulture) != i)
                {
                    throw new InvalidDataException(
                        $"context_buckets.json: {name}[{i}] = '{dimension.Values[i]}' 가 {typeof(TEnum).Name} 의 {i}번째와 다르다.");
                }
            }
        }

        static WorldFlags[] FlagsFor(DimensionDto dimension, string name)
        {
            var flags = new WorldFlags[dimension.Values.Length];

            for (int i = 0; i < dimension.Values.Length; i++)
            {
                if (dimension.WorldFlag is null
                    || !dimension.WorldFlag.TryGetValue(dimension.Values[i], out string? flagId)
                    || flagId is null)
                {
                    continue;
                }

                if (!WorldFlagTable.TryParse(flagId, out WorldFlags flag))
                {
                    throw new InvalidDataException(
                        $"context_buckets.json: {name}.{dimension.Values[i]} 가 없는 플래그 '{flagId}' 를 가리킨다.");
                }

                flags[i] = flag;
            }

            return flags;
        }
    }

    private static TEnum ParseEnum<TEnum>(string value, string where) where TEnum : struct, Enum =>
        Enum.TryParse(value, ignoreCase: true, out TEnum parsed)
            ? parsed
            : throw new InvalidDataException($"{where}: '{value}' 를 {typeof(TEnum).Name} 으로 해석할 수 없다.");
}
