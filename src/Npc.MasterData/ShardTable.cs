using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npc.Contracts;

namespace Npc.MasterData;

/// <summary>샤드 하나. <c>deploy/shards.json</c> 의 한 줄이다 (A-08).</summary>
/// <param name="Shard">샤드 번호. 1 부터. 0 은 "단일 샤드" 를 뜻해 쓸 수 없다.</param>
/// <param name="Zones">이 샤드가 맡는 존 code. 오름차순이다.</param>
/// <param name="Mask">존 비트마스크. <c>1UL &lt;&lt; code</c> 의 OR.</param>
/// <param name="Description">사람이 읽는 설명. 로그·문서용이다.</param>
public readonly record struct ShardDef(
    ushort Shard, ImmutableArray<ZoneId> Zones, ulong Mask, string Description);

/// <summary>
/// 샤드 정의 (A-08 · 1단계 = 정적 샤딩).
///
/// <para>
/// <b>이것은 마스터데이터가 아니다.</b> 배포 토폴로지이지 콘텐츠가 아니라
/// <c>content_hash</c> 에 들어가지 않는다 — 샤드를 다시 나눴다고 프리베이크된 플랜 2,880개가
/// 무효가 되면 아무도 샤드를 다시 나누지 않는다. 대신 <b><see cref="ShardDef.Mask"/> 가
/// 핸드셰이크에 실려</b>, 게임서버와 다르면 그 자리에서 거절된다.
/// </para>
///
/// <para>
/// <b>V15 — 검증은 로드에서 끝난다.</b> 존이 겹치면 같은 NPC 를 두 프로세스가 움직이려 하고,
/// 증상은 "가끔 NPC 가 두 곳에 있는 것처럼 보인다" 라 원인 추적이 매우 어렵다. 그래서
/// 겹침·모르는 존·code 63 초과·샤드 번호 중복을 전부 <b>로드 실패</b>로 만든다.
/// </para>
/// </summary>
public sealed class ShardTable
{
    /// <summary>기본 파일 경로 (저장소 루트 기준).</summary>
    public const string DefaultPath = "deploy/shards.json";

    /// <summary>
    /// 비트마스크에 담을 수 있는 가장 큰 존 code.
    /// <b>넘으면 B-02 확장 슬롯으로 옮긴다</b> — 마스크를 늘리면 와이어가 바뀐다.
    /// </summary>
    public const int MaxZoneCode = 63;

    private ShardTable(ImmutableArray<ShardDef> shards) => Shards = shards;

    /// <summary>샤드. 번호 오름차순이다.</summary>
    public ImmutableArray<ShardDef> Shards { get; }

    /// <summary>샤드 수.</summary>
    public int Count => Shards.Length;

    /// <summary>
    /// 이 샤드 번호의 정의. 없으면 null.
    /// </summary>
    /// <param name="shard">샤드 번호.</param>
    public ShardDef? TryGet(ushort shard)
    {
        foreach (ShardDef def in Shards)
        {
            if (def.Shard == shard)
            {
                return def;
            }
        }

        return null;
    }

    /// <summary>
    /// 읽고 검증한다 (V15). <b>어긋나면 던진다</b> — 경고 후 진행을 허용하지 않는다.
    /// </summary>
    /// <param name="path">파일 경로.</param>
    /// <param name="data">존 이름을 code 로 풀 마스터데이터.</param>
    public static ShardTable Load(string path, MasterDataSet data)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(data);

        ShardsFile file = JsonSerializer.Deserialize(
            File.ReadAllText(path), ShardJsonContext.Default.ShardsFile)
            ?? throw new InvalidDataException($"{path} 를 읽지 못했다.");

        if (file.Shards is null || file.Shards.Length == 0)
        {
            throw new InvalidDataException($"{path}: 샤드가 하나도 없다.");
        }

        var byShard = new SortedDictionary<ushort, ShardDef>();
        var owner = new Dictionary<ushort, ushort>();   // 존 code → 샤드 번호

        foreach (ShardDto dto in file.Shards)
        {
            if (dto.Shard <= 0 || dto.Shard > ushort.MaxValue)
            {
                throw new InvalidDataException(
                    $"{path}: 샤드 번호 {dto.Shard} 가 범위 밖이다. 1..{ushort.MaxValue} 여야 한다 "
                    + "— 0 은 '단일 샤드' 를 뜻하므로 쓸 수 없다.");
            }

            var shard = (ushort)dto.Shard;

            if (byShard.ContainsKey(shard))
            {
                throw new InvalidDataException($"{path}: 샤드 {shard} 가 두 번 나온다.");
            }

            if (dto.Zones is null || dto.Zones.Length == 0)
            {
                throw new InvalidDataException($"{path}: 샤드 {shard} 에 존이 없다.");
            }

            var codes = new List<ZoneId>(dto.Zones.Length);
            ulong mask = 0;

            foreach (string id in dto.Zones)
            {
                if (!data.Zones.TryGet(id, out ZoneDef zone))
                {
                    throw new InvalidDataException(
                        $"{path}: 샤드 {shard} 의 존 '{id}' 이 zones.json 에 없다.");
                }

                if (zone.Code.Value > MaxZoneCode)
                {
                    throw new InvalidDataException(
                        $"{path}: 존 '{id}' 의 code {zone.Code.Value} 가 {MaxZoneCode} 를 넘는다. "
                        + "ZoneMask 가 u64 라 담을 수 없다 — B-02 확장 슬롯으로 옮겨야 한다.");
                }

                if (owner.TryGetValue(zone.Code.Value, out ushort other))
                {
                    throw new InvalidDataException(
                        $"{path}: 존 '{id}' 이 샤드 {other} 와 {shard} 에 둘 다 있다. "
                        + "겹치면 같은 NPC 를 두 프로세스가 움직인다.");
                }

                owner[zone.Code.Value] = shard;
                codes.Add(zone.Code);
                mask |= 1UL << zone.Code.Value;
            }

            codes.Sort((a, b) => a.Value.CompareTo(b.Value));

            byShard[shard] = new ShardDef(
                shard, [.. codes], mask, dto.Desc ?? string.Empty);
        }

        return new ShardTable([.. byShard.Values]);
    }

    /// <summary>
    /// 존 code 들의 비트마스크. 비어 있으면 0 = "전체" 다.
    /// </summary>
    /// <param name="zones">존 code.</param>
    public static ulong MaskOf(ReadOnlySpan<ZoneId> zones)
    {
        ulong mask = 0;

        foreach (ZoneId zone in zones)
        {
            if (zone.Value <= MaxZoneCode)
            {
                mask |= 1UL << zone.Value;
            }
        }

        return mask;
    }

    /// <summary>
    /// 이 마스크가 그 존을 담는가. <b>0 은 전체</b>라 언제나 참이다.
    /// <b>할당 0 · 비트 연산 하나</b> — 바인딩 후보를 거를 때 후보마다 돈다.
    /// </summary>
    /// <param name="mask">비트마스크.</param>
    /// <param name="zone">존 code.</param>
    public static bool Covers(ulong mask, ZoneId zone) =>
        mask == 0 || (zone.Value <= MaxZoneCode && (mask & (1UL << zone.Value)) != 0);
}

internal sealed record ShardsFile(int Version, ShardDto[] Shards);

internal sealed record ShardDto(int Shard, string[] Zones, string? Desc);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ShardsFile))]
internal sealed partial class ShardJsonContext : JsonSerializerContext;
