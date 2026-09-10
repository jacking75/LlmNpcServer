#:project ../src/Npc.MasterData/Npc.MasterData.csproj

// masterdata/npc_instances.json 생성기. docs/01 §9 · docs/11 §8.
//
// .NET 10 파일 기반 앱이다. 추가 도구 설치 없이 그대로 돈다:
//     dotnet run tools/gen_npcs.cs
//     dotnet run tools/gen_npcs.cs --seed 20260725 --out C:\tmp\npcs.json
//
// 무엇을 만드는가
//   NPC 5,000마리(--population 으로 변경 가능). archetypes.json 의 population_weight 로 인원을 나누고,
//   pois.json 의 capacity 를 넘지 않게 일터와 집을 배정한다.
//   npc_instances.json 은 산출물이다 — 손으로 편집하지 않는다 (docs/01 §9).
//
// 순서 (docs/11 §8)
//   1) population_weight × 인구를 최대잔여법으로 나눠 합이 정확히 인구가 되게 한다
//   2) 일터가 있는 아키타입을 먼저 배정한다 — 시골 전문직이 자기 일터 근처 집을 먼저 가져가야 한다
//   3) 집은 일터 존에서 존 그래프 홉이 가까운 순으로 찾는다. 일터가 없으면 잔여 정원이 가장 많은 집
//   4) trait_offsets 는 seed 고정 해시로 ±15
//   5) 초기 인벤토리는 아키타입 기본값
//   6) 정원·존 인구를 검증한 뒤 출력
//
// 결정론
//   Random 을 쓰지 않는다. 개체 편차는 SplitMix64(seed, id, trait) 다.
//   동률은 전부 code 오름차순으로 깬다. 같은 seed 면 바이트 동일한 파일이 나온다.

using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Npc.MasterData.Authoring;

// docs/01 §9 가 정한 인구. --population 으로 바꿀 수 있지만 masterdata/npc_instances.json 은
// 이 값으로 유지한다 — P1 게이트와 docs/01 §9 가 5,000 을 인용한다.
const int DefaultPopulation = 5_000;
const int TraitSpread = 15;   // trait_offsets 범위 ±15 (docs/01 §9)

int seed = 20260725;
int population = DefaultPopulation;
string? outArg = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--":
            break;
        case "--seed" when i + 1 < args.Length:
            seed = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--out" when i + 1 < args.Length:
            outArg = args[++i];
            break;
        // 부하 매트릭스의 10,000(스트레스) 축을 재려면 그만큼의 인스턴스가 있어야 한다
        // (docs/14 §6 · T4-16 에서 --npcs 10000 이 조용히 5,000 으로 줄던 문제).
        // 기본값을 바꾸지 않고 --out 으로 별도 파일에 뽑는 용도다.
        //
        // 상한은 pois.json 의 총 정원 9,238 이고 일터별로는 훨씬 적다 — 10,000 은
        // 'smithy' 배정에서 멈춘다. 월드가 5,000 인구로 설계돼 있다는 뜻이므로
        // 그 위를 재려면 pois.json 부터 늘려야 한다 (SSOT 변경 · CLAUDE.md §2.4).
        case "--population" when i + 1 < args.Length:
            population = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        default:
            throw new ArgumentException($"모르는 인자다: {args[i]}");
    }
}

string root = FindRepoRoot();
string masterData = Path.Combine(root, "masterdata");
string outPath = outArg ?? Path.Combine(masterData, "npc_instances.json");

using JsonDocument zonesDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(masterData, "zones.json")));
using JsonDocument poisDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(masterData, "pois.json")));
using JsonDocument archetypesDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(masterData, "archetypes.json")));

// ---------------------------------------------------------------- 존 그래프

Zone[] zones = [.. zonesDoc.RootElement.GetProperty("zones").EnumerateArray()
    .Select(z => new Zone(
        z.GetProperty("id").GetString()!,
        z.GetProperty("code").GetInt32(),
        [.. z.GetProperty("adjacent").EnumerateArray().Select(a => a.GetString()!)],
        z.GetProperty("capacity").GetInt32()))
    .OrderBy(z => z.Code)];

Dictionary<string, int> zoneIndex = new(StringComparer.Ordinal);
for (int i = 0; i < zones.Length; i++)
{
    zoneIndex[zones[i].Id] = i;
}

// 존별 홉 거리. 집을 일터에서 가까운 순으로 찾을 때 쓴다.
int[][] hops = new int[zones.Length][];
for (int i = 0; i < zones.Length; i++)
{
    hops[i] = Bfs(i);
}

// ---------------------------------------------------------------- POI

Poi[] pois = [.. poisDoc.RootElement.GetProperty("pois").EnumerateArray()
    .Select(p => new Poi(
        p.GetProperty("id").GetString()!,
        p.GetProperty("code").GetInt32(),
        zoneIndex[p.GetProperty("zone").GetString()!],
        p.GetProperty("type").GetString()!,
        p.GetProperty("subtype").GetString()!,
        p.GetProperty("pos").GetProperty("x").GetDouble(),
        p.GetProperty("pos").GetProperty("y").GetDouble(),
        p.GetProperty("pos").GetProperty("z").GetDouble(),
        p.GetProperty("capacity").GetInt32(),
        [.. p.GetProperty("allowed_archetypes").EnumerateArray().Select(a => a.GetString()!)]))
    .OrderBy(p => p.Code)];

var remaining = new int[pois.Length];
for (int i = 0; i < pois.Length; i++)
{
    remaining[i] = pois[i].Capacity;
}

// 집은 존별로 묶어 둔다 — 일터 존에서 가까운 순으로 훑는다.
List<int>[] homesByZone = [.. Enumerable.Range(0, zones.Length).Select(_ => new List<int>())];
var homes = new List<int>();

for (int i = 0; i < pois.Length; i++)
{
    if (pois[i].Type == "home")
    {
        homesByZone[pois[i].Zone].Add(i);
        homes.Add(i);
    }
}

// ---------------------------------------------------------------- 인구 배분

Archetype[] archetypes = [.. archetypesDoc.RootElement.GetProperty("archetypes").EnumerateArray()
    .Select(a => new Archetype(
        a.GetProperty("id").GetString()!,
        a.GetProperty("code").GetInt32(),
        a.GetProperty("workplace_poi_type").ValueKind == JsonValueKind.Null
            ? null
            : a.GetProperty("workplace_poi_type").GetString(),
        [.. a.GetProperty("traits").EnumerateObject().Select(t => t.Name)],
        a.GetProperty("population_weight").GetDouble(),
        a.GetProperty("initial_inventory")))
    .OrderBy(a => a.Code)];

int[] quota = Apportion(archetypes, population);

// ---------------------------------------------------------------- 배정

var npcs = new Assignment[population];
int next = 0;

foreach (Archetype archetype in archetypes)
{
    for (int i = 0; i < quota[archetype.Code]; i++)
    {
        npcs[next] = new Assignment(next + 1, archetype, -1, -1);
        next++;
    }
}

if (next != population)
{
    throw new InvalidDataException($"인구 배분이 {next} 다. {population} 이어야 한다.");
}

// 1차 — 일터가 있는 NPC. 일터를 먼저 잡고 그 존에서 가까운 집을 찾는다.
for (int i = 0; i < npcs.Length; i++)
{
    if (npcs[i].Archetype.Workplace is null)
    {
        continue;
    }

    int workplace = TakeWorkplace(npcs[i].Archetype);
    npcs[i] = npcs[i] with { Workplace = workplace, Home = TakeHome(pois[workplace].Zone) };
}

// 2차 — 일터가 없는 NPC. 잔여 정원이 가장 많은 집으로 흩는다.
for (int i = 0; i < npcs.Length; i++)
{
    if (npcs[i].Archetype.Workplace is not null)
    {
        continue;
    }

    npcs[i] = npcs[i] with { Home = TakeHome(-1) };
}

// ---------------------------------------------------------------- 검증

foreach (Assignment npc in npcs)
{
    if (npc.Home < 0)
    {
        throw new InvalidDataException($"NPC {npc.Id} ({npc.Archetype.Id}) 에 집이 없다. 주거 정원이 모자란다.");
    }
}

for (int i = 0; i < pois.Length; i++)
{
    if (remaining[i] < 0)
    {
        throw new InvalidDataException($"pois.json: '{pois[i].Id}' 정원 {pois[i].Capacity} 를 넘겼다.");
    }
}

var zonePopulation = new int[zones.Length];
foreach (Assignment npc in npcs)
{
    zonePopulation[pois[npc.Home].Zone]++;
}

for (int i = 0; i < zones.Length; i++)
{
    if (zonePopulation[i] > zones[i].Capacity)
    {
        throw new InvalidDataException(
            $"zones.json: '{zones[i].Id}' 인구 {zonePopulation[i]} 가 정원 {zones[i].Capacity} 를 넘는다.");
    }
}

// ---------------------------------------------------------------- 출력

var buffer = new ArrayBufferWriter<byte>(1 << 20);

using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
{
    Indented = true,
    NewLine = "\n",   // 윈도우/리눅스에서 바이트가 같아야 한다
}))
{
    writer.WriteStartObject();
    writer.WriteNumber("version", 1);
    writer.WriteNumber("seed", seed);

    writer.WriteStartArray("_comment");
    writer.WriteStringValue("docs/01 §9 · docs/11 §8. tools/gen_npcs.cs 가 만든다. 손으로 편집하지 않는다.");
    writer.WriteStringValue("trait_offsets 는 플랜 선택에 영향을 주지 않는다 — 같은 버킷의 NPC 는 같은 플랜을 쓴다.");
    writer.WriteStringValue("소요시간·대기시간·경로 랜덤화에만 쓰는 값싼 다양성의 원천이다.");
    writer.WriteEndArray();

    writer.WriteStartArray("npcs");

    foreach (Assignment npc in npcs)
    {
        Poi home = pois[npc.Home];

        writer.WriteStartObject();
        writer.WriteNumber("id", npc.Id);
        writer.WriteString("archetype", npc.Archetype.Id);
        writer.WriteString("zone", zones[home.Zone].Id);
        writer.WriteString("home_poi", home.Id);

        if (npc.Workplace >= 0)
        {
            writer.WriteString("workplace_poi", pois[npc.Workplace].Id);
        }
        else
        {
            writer.WriteNull("workplace_poi");
        }

        // 스폰은 집 앞이다. 같은 집의 NPC 가 한 점에 겹치지 않게 결정론적으로 흩는다.
        writer.WriteStartObject("spawn_pos");
        writer.WriteNumber("x", Math.Round(home.X + Offset(npc.Id, 11, 4), 1));
        writer.WriteNumber("y", Math.Round(home.Y, 1));
        writer.WriteNumber("z", Math.Round(home.Z + Offset(npc.Id, 12, 4), 1));
        writer.WriteEndObject();

        writer.WriteStartObject("trait_offsets");

        for (int t = 0; t < npc.Archetype.Traits.Length; t++)
        {
            writer.WriteNumber(npc.Archetype.Traits[t], Offset(npc.Id, t, TraitSpread));
        }

        writer.WriteEndObject();

        writer.WritePropertyName("initial_inventory");
        npc.Archetype.Inventory.WriteTo(writer);

        writer.WriteEndObject();
    }

    writer.WriteEndArray();
    writer.WriteEndObject();
}

using (var file = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None))
{
    file.Write(buffer.WrittenSpan);
    file.WriteByte((byte)'\n');
}

Console.WriteLine($"{outPath}: NPC {npcs.Length}마리, seed {seed}, {buffer.WrittenCount + 1} bytes");

// 기본 위치에 썼을 때만 잠금을 갱신한다 (F-04) — --out 으로 딴 데 쓴 것은 masterdata 의 파생물이 아니다.
if (outArg is null)
{
    DerivedArtifacts.Record(masterData, "npc_instances.json");
    Console.WriteLine($"{DerivedArtifacts.FileName}: npc_instances.json 기록");
}

for (int i = 0; i < zones.Length; i++)
{
    Console.WriteLine($"  {zones[i].Id,-20} {zonePopulation[i],5} / {zones[i].Capacity}");
}

// ---------------------------------------------------------------- 헬퍼

// 최대잔여법. weight × 5,000 의 정수부를 나눠주고 남은 자리는 소수부가 큰 순으로 준다.
// 그냥 반올림하면 합이 5,000 에서 어긋난다.
static int[] Apportion(Archetype[] archetypes, int total)
{
    var quota = new int[archetypes.Length];
    var remainder = new (double Fraction, int Code)[archetypes.Length];
    int assigned = 0;

    foreach (Archetype archetype in archetypes)
    {
        double exact = archetype.Weight * total;
        quota[archetype.Code] = (int)Math.Floor(exact);
        remainder[archetype.Code] = (exact - Math.Floor(exact), archetype.Code);
        assigned += quota[archetype.Code];
    }

    // 동률이면 code 오름차순 — 결정론.
    Array.Sort(remainder, (a, b) => b.Fraction != a.Fraction
        ? b.Fraction.CompareTo(a.Fraction)
        : a.Code.CompareTo(b.Code));

    for (int i = 0; assigned < total; i++, assigned++)
    {
        quota[remainder[i % remainder.Length].Code]++;
    }

    return quota;
}

int TakeWorkplace(Archetype archetype)
{
    int best = -1;

    for (int i = 0; i < pois.Length; i++)
    {
        if (!string.Equals(pois[i].Subtype, archetype.Workplace, StringComparison.Ordinal)
            || remaining[i] <= 0
            || !pois[i].Allows(archetype.Id))
        {
            continue;
        }

        // 잔여가 가장 많은 곳 → 존 사이가 고르게 찬다. 동률은 code 작은 쪽.
        if (best < 0 || remaining[i] > remaining[best])
        {
            best = i;
        }
    }

    if (best < 0)
    {
        throw new InvalidDataException(
            $"pois.json: '{archetype.Workplace}' 정원이 모자라 {archetype.Id} 를 배정할 수 없다.");
    }

    remaining[best]--;
    return best;
}

// fromZone 이 -1 이면 전 존에서 잔여가 가장 많은 집. 아니면 홉이 가까운 존부터 훑는다.
int TakeHome(int fromZone)
{
    if (fromZone < 0)
    {
        return Take(homes);
    }

    int[] distance = hops[fromZone];

    foreach (int zone in Enumerable.Range(0, zones.Length)
        .Where(z => distance[z] >= 0)
        .OrderBy(z => distance[z])
        .ThenBy(z => zones[z].Code))
    {
        int home = TryTake(homesByZone[zone]);

        if (home >= 0)
        {
            return home;
        }
    }

    throw new InvalidDataException($"주거 정원이 모자란다 ('{zones[fromZone].Id}' 에서 출발).");

    int Take(List<int> candidates)
    {
        int home = TryTake(candidates);

        return home >= 0 ? home : throw new InvalidDataException("주거 정원이 모자란다.");
    }
}

int TryTake(List<int> candidates)
{
    int best = -1;

    foreach (int i in candidates)
    {
        if (remaining[i] > 0 && (best < 0 || remaining[i] > remaining[best]))
        {
            best = i;
        }
    }

    if (best >= 0)
    {
        remaining[best]--;
    }

    return best;
}

int[] Bfs(int start)
{
    var distance = new int[zones.Length];
    Array.Fill(distance, -1);
    distance[start] = 0;

    var queue = new Queue<int>();
    queue.Enqueue(start);

    while (queue.Count > 0)
    {
        int current = queue.Dequeue();

        foreach (string neighbour in zones[current].Adjacent)
        {
            int index = zoneIndex[neighbour];

            if (distance[index] < 0)
            {
                distance[index] = distance[current] + 1;
                queue.Enqueue(index);
            }
        }
    }

    return distance;
}

// SplitMix64. Random 을 쓰면 리플레이가 깨진다 (CLAUDE.md §2.3).
int Offset(int id, int salt, int spread)
{
    ulong z = (ulong)(uint)seed * 0x9E3779B97F4A7C15UL
        + (ulong)(uint)id * 0xBF58476D1CE4E5B9UL
        + (ulong)(uint)salt * 0x94D049BB133111EBUL;

    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
    z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
    z ^= z >> 31;

    return (int)(z % (ulong)(2 * spread + 1)) - spread;
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());

    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "NpcServer.sln")))
        {
            return dir.FullName;
        }

        dir = dir.Parent;
    }

    throw new InvalidOperationException("NpcServer.sln 을 찾지 못했다. 저장소 루트에서 실행한다.");
}

internal sealed record Zone(string Id, int Code, string[] Adjacent, int Capacity);

internal sealed record Poi(
    string Id,
    int Code,
    int Zone,
    string Type,
    string Subtype,
    double X,
    double Y,
    double Z,
    int Capacity,
    string[] AllowedArchetypes)
{
    // 빈 목록은 제한 없음이다 (docs/01 §4).
    public bool Allows(string archetype) =>
        AllowedArchetypes.Length == 0 || Array.IndexOf(AllowedArchetypes, archetype) >= 0;
}

internal sealed record Archetype(
    string Id,
    int Code,
    string? Workplace,
    string[] Traits,
    double Weight,
    JsonElement Inventory);

internal readonly record struct Assignment(int Id, Archetype Archetype, int Home, int Workplace);
