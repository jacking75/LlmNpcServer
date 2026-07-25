using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npc.Contracts;
using Npc.Core;

namespace Npc.MasterData;

/// <summary>
/// masterdata/ 를 통째로 읽어 읽기 전용 인덱스로 만든다. docs/01 §11.
///
/// 로딩 규약: 전량 로드 → 스키마 검증 → 참조 무결성 검증 → 인덱스 컴파일 → 이후 불변.
/// 참조가 깨져 있으면 여기서 <see cref="InvalidDataException"/> 을 던진다. 경고 후 진행은 없다.
/// 규칙 단위 검증(V1~V11)은 <c>MasterDataValidator</c> 가 따로 본다.
/// </summary>
public static class MasterDataLoader
{
    /// <summary>해시에 넣는 파일 목록. 순서를 고정해야 ContentHash 가 결정론이다.</summary>
    private static readonly string[] s_hashedFiles =
    [
        "actions.json",
        "archetypes.json",
        "context_buckets.json",
        "fallback_plans.json",
        "interrupts.json",
        "items.json",
        "poi_distances.bin",
        "pois.json",
        "world_flags.json",
        "zones.json",
    ];

    /// <summary>masterdata 폴더 전체 로드.</summary>
    public static MasterDataSet Load(string masterDataDirectory)
    {
        if (!Directory.Exists(masterDataDirectory))
        {
            throw new DirectoryNotFoundException($"masterdata 폴더를 찾지 못했다: {masterDataDirectory}");
        }

        string Path_(string name) => Path.Combine(masterDataDirectory, name);

        ItemTable items = ItemTable.Load(Path_("items.json"));
        ActionCatalog actions = ActionCatalog.Load(Path_("actions.json"), items);
        ArchetypeTable archetypes = ArchetypeTable.Load(Path_("archetypes.json"), actions, items);
        ZoneTable zones = LoadZones(Path_("zones.json"));
        PoiTable pois = LoadPois(Path_("pois.json"), Path_("poi_distances.bin"), zones, archetypes, items);
        BucketSpace buckets = LoadBuckets(Path_("context_buckets.json"));
        InterruptRules interrupts = InterruptRules.Load(Path_("interrupts.json"), actions);

        ImmutableArray<FileHash> hashes = HashFiles(masterDataDirectory);

        var set = new MasterDataSet
        {
            Actions = actions,
            Items = items,
            Zones = zones,
            Pois = pois,
            Archetypes = archetypes,
            Buckets = buckets,
            Interrupts = interrupts,
            FileHashes = hashes,
            ContentHash = CombineHashes(hashes),
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
    public static ImmutableArray<FileHash> HashFiles(string masterDataDirectory)
    {
        var builder = ImmutableArray.CreateBuilder<FileHash>(s_hashedFiles.Length);

        foreach (string name in s_hashedFiles)
        {
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
        string poisPath, string distancesPath, ZoneTable zones, ArchetypeTable archetypes, ItemTable items)
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

        (Half[] distances, int size) = LoadDistances(distancesPath, pois.Length);

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

    private static BucketSpace LoadBuckets(string path)
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

        return new BucketSpace(
            FlagsFor(time, "time_of_day"),
            FlagsFor(region, "region_state"),
            FlagsFor(climate, "climate"),
            gameHours,
            file.TotalKeys);

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
