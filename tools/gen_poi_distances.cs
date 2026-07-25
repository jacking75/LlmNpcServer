// masterdata/poi_distances.bin 생성기. docs/01 §4.
//
// .NET 10 파일 기반 앱이다. 추가 도구 설치 없이 그대로 돈다:
//     dotnet run tools/gen_poi_distances.cs
//
// 무엇을 만드는가
//   N×N Half 행렬. 첨자는 POI code - 1 이다 (code 는 1부터 연속).
//   Sim 의 이동 소요시간 계산(docs/02 §5)과 검증기 3단의 도달 가능성 판정에 쓴다.
//
// 어떻게 재는가
//   같은 존       : 두 POI 좌표의 직선거리
//   다른 존       : |a - centroid(zoneA)| + 존그래프 최단거리 + |centroid(zoneB) - b|
//   존그래프 최단거리는 인접 존 중심 사이의 직선거리를 간선 가중치로 둔 Floyd-Warshall 이다.
//   패스파인딩을 하지 않는 이유는 docs/02 §5 에 있다 — 경로 계산은 게임서버 소관이다.
//
// 결정론
//   난수도 시각도 쓰지 않는다. 같은 zones.json/pois.json 이면 바이트 동일한 파일이 나온다.

using System.Globalization;
using System.Text.Json;

string root = FindRepoRoot();
string masterData = Path.Combine(root, "masterdata");
string outPath = Path.Combine(masterData, "poi_distances.bin");

using JsonDocument zonesDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(masterData, "zones.json")));
using JsonDocument poisDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(masterData, "pois.json")));

// --- 존 그래프 ---
string[] zoneIds = zonesDoc.RootElement.GetProperty("zones").EnumerateArray()
    .Select(z => z.GetProperty("id").GetString()!)
    .ToArray();

Dictionary<string, int> zoneIndex = new(StringComparer.Ordinal);
for (int i = 0; i < zoneIds.Length; i++)
{
    zoneIndex[zoneIds[i]] = i;
}

// --- POI ---
JsonElement[] poiElements = poisDoc.RootElement.GetProperty("pois").EnumerateArray().ToArray();
int n = poiElements.Length;

var x = new double[n];
var y = new double[n];
var z = new double[n];
var poiZone = new int[n];

foreach (JsonElement poi in poiElements)
{
    int slot = poi.GetProperty("code").GetInt32() - 1;
    if (slot < 0 || slot >= n)
    {
        throw new InvalidDataException($"pois.json: code 가 1..{n} 의 연속 범위를 벗어난다 ({slot + 1}).");
    }

    JsonElement pos = poi.GetProperty("pos");
    x[slot] = pos.GetProperty("x").GetDouble();
    y[slot] = pos.GetProperty("y").GetDouble();
    z[slot] = pos.GetProperty("z").GetDouble();
    poiZone[slot] = zoneIndex[poi.GetProperty("zone").GetString()!];
}

// --- 존 중심 = 그 존 POI 좌표의 평균 ---
int zoneCount = zoneIds.Length;
var cx = new double[zoneCount];
var cy = new double[zoneCount];
var cz = new double[zoneCount];
var members = new int[zoneCount];

for (int i = 0; i < n; i++)
{
    int zi = poiZone[i];
    cx[zi] += x[i];
    cy[zi] += y[i];
    cz[zi] += z[i];
    members[zi]++;
}

for (int i = 0; i < zoneCount; i++)
{
    if (members[i] == 0)
    {
        throw new InvalidDataException($"zones.json: 존 '{zoneIds[i]}' 에 POI 가 하나도 없다.");
    }

    cx[i] /= members[i];
    cy[i] /= members[i];
    cz[i] /= members[i];
}

// --- 존 그래프 Floyd-Warshall ---
const double Inf = double.PositiveInfinity;
var zoneDist = new double[zoneCount, zoneCount];

for (int i = 0; i < zoneCount; i++)
{
    for (int j = 0; j < zoneCount; j++)
    {
        zoneDist[i, j] = i == j ? 0 : Inf;
    }
}

foreach (JsonElement zone in zonesDoc.RootElement.GetProperty("zones").EnumerateArray())
{
    int a = zoneIndex[zone.GetProperty("id").GetString()!];

    foreach (JsonElement adj in zone.GetProperty("adjacent").EnumerateArray())
    {
        int b = zoneIndex[adj.GetString()!];
        double d = Euclid(cx[a], cy[a], cz[a], cx[b], cy[b], cz[b]);
        zoneDist[a, b] = Math.Min(zoneDist[a, b], d);
        zoneDist[b, a] = zoneDist[a, b];
    }
}

for (int k = 0; k < zoneCount; k++)
{
    for (int i = 0; i < zoneCount; i++)
    {
        for (int j = 0; j < zoneCount; j++)
        {
            double through = zoneDist[i, k] + zoneDist[k, j];
            if (through < zoneDist[i, j])
            {
                zoneDist[i, j] = through;
            }
        }
    }
}

for (int i = 0; i < zoneCount; i++)
{
    for (int j = 0; j < zoneCount; j++)
    {
        if (double.IsInfinity(zoneDist[i, j]))
        {
            throw new InvalidDataException($"zones.json: '{zoneIds[i]}' 에서 '{zoneIds[j]}' 로 갈 수 없다 (고립 존).");
        }
    }
}

// --- POI × POI ---
// 존 중심까지의 거리를 미리 재둔다 (O(N) 한 번).
var toCentroid = new double[n];
for (int i = 0; i < n; i++)
{
    int zi = poiZone[i];
    toCentroid[i] = Euclid(x[i], y[i], z[i], cx[zi], cy[zi], cz[zi]);
}

var matrix = new Half[n * n];
double maxDistance = 0;

for (int i = 0; i < n; i++)
{
    matrix[(i * n) + i] = (Half)0f;

    for (int j = i + 1; j < n; j++)
    {
        double d = poiZone[i] == poiZone[j]
            ? Euclid(x[i], y[i], z[i], x[j], y[j], z[j])
            : toCentroid[i] + zoneDist[poiZone[i], poiZone[j]] + toCentroid[j];

        // 대칭성은 여기서 보장한다 — 같은 값을 두 칸에 쓴다.
        Half h = (Half)d;
        matrix[(i * n) + j] = h;
        matrix[(j * n) + i] = h;

        if (d > maxDistance)
        {
            maxDistance = d;
        }
    }
}

if (maxDistance > 65_504)
{
    throw new InvalidDataException($"최대 거리 {maxDistance:F1}m 가 Half 의 표현 범위를 넘는다.");
}

// --- 쓰기 ---
// 헤더 8바이트: 매직 "POID" + POI 수(int32 little-endian). 그 뒤에 N×N Half.
using (FileStream fs = File.Create(outPath))
using (var writer = new BinaryWriter(fs))
{
    writer.Write((byte)'P');
    writer.Write((byte)'O');
    writer.Write((byte)'I');
    writer.Write((byte)'D');
    writer.Write(n);

    foreach (Half h in matrix)
    {
        writer.Write(BitConverter.HalfToUInt16Bits(h));
    }
}

long size = new FileInfo(outPath).Length;
Console.WriteLine(string.Create(
    CultureInfo.InvariantCulture,
    $"poi_distances.bin: POI {n}개 · {size:N0} bytes ({size / 1024.0:F1} KB) · 최대 거리 {maxDistance:F1}m"));

static double Euclid(double ax, double ay, double az, double bx, double by, double bz)
{
    double dx = ax - bx;
    double dy = ay - by;
    double dz = az - bz;
    return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
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

    throw new InvalidOperationException("NpcServer.sln 을 찾지 못했다. 저장소 루트에서 실행해라.");
}
