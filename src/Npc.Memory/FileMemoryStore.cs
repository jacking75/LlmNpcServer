using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npc.Contracts;
using Npc.Core;

namespace Npc.Memory;

/// <summary>
/// 파일 한 벌에 얹은 기억 저장소 (D-03).
///
/// <para>
/// <b>왜 Redis 가 아닌가.</b> 로드맵은 Redis·PostgreSQL 어댑터를 적었지만,
/// <b>이 저장소에는 그것을 돌려 볼 인스턴스가 없다</b> — 한 번도 붙여 보지 않은 어댑터를
/// 넣어 두는 것은 없는 것보다 나쁘다("있다" 고 읽히기 때문이다). 어댑터 자리는
/// <see cref="IMemoryStore"/> 가 잡아 두었고, 여기서는 <b>외부 의존 없이 실제로 도는</b>
/// 구현 하나를 준다.
/// </para>
///
/// <para>
/// <b>전량 로드 · 전량 쓰기다.</b> 기억은 NPC 5,000 × 관계 몇 개 수준이라 수 MB 를 넘지 않고,
/// 쓰기는 <see cref="FlushAsync"/> 에서만 일어난다 — 재계획 경로는 읽기뿐이다.
/// 증분 로그가 필요할 만큼 커지면 그때가 외부 저장소로 옮길 때다.
/// </para>
///
/// <para>
/// <b>쓰기는 원자적이다.</b> 임시 파일에 쓰고 바꿔 끼운다 — 중간에 죽으면 옛 파일이 남는다.
/// 반쯤 쓰인 파일이 남으면 다음 기동에 기억이 통째로 사라진다.
/// </para>
/// </summary>
public sealed class FileMemoryStore : IMemoryStore
{
    /// <summary>파일 이름.</summary>
    public const string FileName = "memory.json";

    /// <summary>형식 버전. 다르면 읽지 않는다 — 낡은 기억을 잘못 해석하느니 비우고 시작한다.</summary>
    public const int FormatVersion = 1;

    private readonly InMemoryStore _inner = new();
    private readonly string _path;

    private FileMemoryStore(string path) => _path = path;

    /// <summary>파일 경로. 진단·로그용.</summary>
    public string Path => _path;

    /// <summary>이 파일에서 읽었는가. 파일이 없으면 false 이고 빈 저장소로 시작한다.</summary>
    public bool Loaded { get; private init; }

    /// <summary>지금 들고 있는 관계 수.</summary>
    public int RelationshipCount => _inner.RelationshipCount;

    /// <summary>지금 들고 있는 기억 수.</summary>
    public int EpisodeCount => _inner.EpisodeCount;

    /// <summary>
    /// 폴더에서 연다. 파일이 없으면 빈 저장소다 — <b>오류가 아니다</b>.
    /// </summary>
    /// <param name="directory">파일이 들어갈 폴더.</param>
    public static FileMemoryStore Open(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);

        string path = System.IO.Path.Combine(directory, FileName);

        if (!File.Exists(path))
        {
            return new FileMemoryStore(path);
        }

        MemoryFile? file = JsonSerializer.Deserialize(
            File.ReadAllText(path), MemoryJsonContext.Default.MemoryFile);

        if (file is null || file.Version != FormatVersion)
        {
            throw new InvalidDataException(
                $"{FileName} 형식 버전이 다르다: 파일 {file?.Version} vs 기대 {FormatVersion}. "
                + "낡은 기억을 잘못 해석하느니 파일을 지우고 다시 시작한다.");
        }

        var store = new FileMemoryStore(path) { Loaded = true };

        store._inner.Restore(new MemorySnapshot(
            [.. (file.Relationships ?? []).Select(r => new Relationship(
                r.Npc, r.Player, r.Affinity, r.LastInteractionTick, r.Interactions, (RelationshipTags)r.Tags))],
            [.. (file.Episodes ?? []).Select(e => new EpisodeSummary(
                e.Npc, e.Sequence, (EpisodeKind)e.Kind, e.Subject, e.SubjectIsPlayer,
                e.Tick, e.Salience, e.SummaryKey))],
            [.. (file.Reputations ?? []).Select(r => new Reputation(
                new FactionId(r.Faction), r.Player, r.Score, r.UpdatedTick))],
            file.Sequence));

        return store;
    }

    /// <inheritdoc />
    public ValueTask<RelationshipBand> BandAsync(int npc, int player, CancellationToken ct = default) =>
        _inner.BandAsync(npc, player, ct);

    /// <inheritdoc />
    public ValueTask<Relationship?> RelationshipAsync(int npc, int player, CancellationToken ct = default) =>
        _inner.RelationshipAsync(npc, player, ct);

    /// <inheritdoc />
    public ValueTask<ImmutableArray<EpisodeSummary>> EpisodesAsync(
        int npc, int limit, CancellationToken ct = default) => _inner.EpisodesAsync(npc, limit, ct);

    /// <inheritdoc />
    public ValueTask<Reputation?> ReputationAsync(
        FactionId faction, int player, CancellationToken ct = default) =>
        _inner.ReputationAsync(faction, player, ct);

    /// <inheritdoc />
    public ValueTask<Relationship> RecordInteractionAsync(
        int npc,
        int player,
        int delta,
        Tick tick,
        RelationshipTags tags = RelationshipTags.None,
        CancellationToken ct = default) =>
        _inner.RecordInteractionAsync(npc, player, delta, tick, tags, ct);

    /// <inheritdoc />
    public ValueTask<EpisodeSummary> AppendEpisodeAsync(
        int npc,
        EpisodeKind kind,
        int subject,
        bool subjectIsPlayer,
        Tick tick,
        byte salience,
        string summaryKey,
        CancellationToken ct = default) =>
        _inner.AppendEpisodeAsync(npc, kind, subject, subjectIsPlayer, tick, salience, summaryKey, ct);

    /// <inheritdoc />
    public ValueTask<Reputation> RecordReputationAsync(
        FactionId faction, int player, int delta, Tick tick, CancellationToken ct = default) =>
        _inner.RecordReputationAsync(faction, player, delta, tick, ct);

    /// <inheritdoc />
    public ValueTask<int> ForgetPlayerAsync(int player, CancellationToken ct = default) =>
        _inner.ForgetPlayerAsync(player, ct);

    /// <inheritdoc />
    public ValueTask<int> PruneAsync(Tick olderThanTick, CancellationToken ct = default) =>
        _inner.PruneAsync(olderThanTick, ct);

    /// <inheritdoc />
    public async ValueTask FlushAsync(CancellationToken ct = default)
    {
        MemorySnapshot snapshot = _inner.Snapshot();

        var file = new MemoryFile(
            FormatVersion,
            snapshot.Sequence,
            [.. snapshot.Relationships.Select(r => new RelationshipDto(
                r.Npc, r.Player, r.Affinity, r.LastInteractionTick, r.Interactions, (ulong)r.Tags))],
            [.. snapshot.Episodes.Select(e => new EpisodeDto(
                e.Npc, e.Sequence, (byte)e.Kind, e.Subject, e.SubjectIsPlayer,
                e.Tick, e.Salience, e.SummaryKey))],
            [.. snapshot.Reputations.Select(r => new ReputationDto(
                r.Faction.Value, r.Player, r.Score, r.UpdatedTick))]);

        string? directory = System.IO.Path.GetDirectoryName(_path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 임시 파일 → 바꿔 끼우기. 중간에 죽어도 옛 파일이 그대로다.
        string temporary = _path + ".tmp";

        await File.WriteAllTextAsync(
            temporary,
            JsonSerializer.Serialize(file, MemoryJsonContext.Default.MemoryFile),
            ct).ConfigureAwait(false);

        File.Move(temporary, _path, overwrite: true);
    }
}

internal sealed record MemoryFile(
    int Version,
    long Sequence,
    RelationshipDto[]? Relationships,
    EpisodeDto[]? Episodes,
    ReputationDto[]? Reputations);

internal sealed record RelationshipDto(
    int Npc, int Player, int Affinity, long LastInteractionTick, int Interactions, ulong Tags);

internal sealed record EpisodeDto(
    int Npc,
    long Sequence,
    byte Kind,
    int Subject,
    bool SubjectIsPlayer,
    long Tick,
    byte Salience,
    string SummaryKey);

internal sealed record ReputationDto(ushort Faction, int Player, int Score, long UpdatedTick);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true)]
[JsonSerializable(typeof(MemoryFile))]
internal sealed partial class MemoryJsonContext : JsonSerializerContext;
