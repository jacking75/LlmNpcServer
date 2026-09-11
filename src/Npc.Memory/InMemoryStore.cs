using System.Collections.Immutable;
using Npc.Contracts;
using Npc.Core;

namespace Npc.Memory;

/// <summary>
/// 프로세스 안에 드는 기억 저장소 (D-03).
///
/// <para>
/// <b>루프백·테스트·단일 프로세스 데모용이다.</b> 프로세스가 죽으면 기억도 죽는다 —
/// 상용에서는 <see cref="FileMemoryStore"/> 나 외부 저장소 어댑터를 쓴다.
/// </para>
///
/// <para>
/// <b>스레드 안전하다.</b> 재계획 워커가 여러 개이고 게임서버 대역이 같이 쓴다.
/// 잠금 하나로 감싼다 — 틱 루프 밖이라 <c>lock</c> 금지 규칙(CLAUDE.md §2.1)이 걸리지 않는다.
/// </para>
///
/// <para>
/// <b>출력은 언제나 정렬돼 있다.</b> <c>Dictionary</c> 순회 순서를 그대로 내보내면
/// 같은 입력에 다른 출력이 나오고, 그 값이 프롬프트에 실리면 캐시가 깨진다 (CLAUDE.md §2.3).
/// </para>
/// </summary>
public sealed class InMemoryStore : IMemoryStore
{
    /// <summary>NPC 한 명이 들고 있는 기억 수 상한. 넘으면 salience 가 낮은 것부터 밀린다.</summary>
    public const int MaxEpisodesPerNpc = 32;

    private readonly Lock _gate = new();
    private readonly Dictionary<(int Npc, int Player), Relationship> _relationships = [];
    private readonly Dictionary<int, List<EpisodeSummary>> _episodes = [];
    private readonly Dictionary<(ushort Faction, int Player), Reputation> _reputations = [];

    private long _sequence;

    /// <summary>지금 들고 있는 관계 수. 진단용.</summary>
    public int RelationshipCount
    {
        get
        {
            lock (_gate)
            {
                return _relationships.Count;
            }
        }
    }

    /// <summary>지금 들고 있는 기억 수. 진단용.</summary>
    public int EpisodeCount
    {
        get
        {
            lock (_gate)
            {
                int total = 0;

                foreach (List<EpisodeSummary> list in _episodes.Values)
                {
                    total += list.Count;
                }

                return total;
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<RelationshipBand> BandAsync(int npc, int player, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return ValueTask.FromResult(
                _relationships.TryGetValue((npc, player), out Relationship found)
                    ? found.Band
                    : RelationshipBand.Unknown);
        }
    }

    /// <inheritdoc />
    public ValueTask<Relationship?> RelationshipAsync(int npc, int player, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return ValueTask.FromResult<Relationship?>(
                _relationships.TryGetValue((npc, player), out Relationship found) ? found : null);
        }
    }

    /// <inheritdoc />
    public ValueTask<ImmutableArray<EpisodeSummary>> EpisodesAsync(
        int npc, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (limit <= 0)
        {
            return ValueTask.FromResult(ImmutableArray<EpisodeSummary>.Empty);
        }

        lock (_gate)
        {
            if (!_episodes.TryGetValue(npc, out List<EpisodeSummary>? list) || list.Count == 0)
            {
                return ValueTask.FromResult(ImmutableArray<EpisodeSummary>.Empty);
            }

            var ordered = new List<EpisodeSummary>(list);
            ordered.Sort(Compare);

            return ValueTask.FromResult(ImmutableArray.CreateRange(ordered.Take(limit)));
        }
    }

    /// <inheritdoc />
    public ValueTask<Reputation?> ReputationAsync(
        FactionId faction, int player, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return ValueTask.FromResult<Reputation?>(
                _reputations.TryGetValue((faction.Value, player), out Reputation found) ? found : null);
        }
    }

    /// <inheritdoc />
    public ValueTask<Relationship> RecordInteractionAsync(
        int npc,
        int player,
        int delta,
        Tick tick,
        RelationshipTags tags = RelationshipTags.None,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            Relationship current = _relationships.TryGetValue((npc, player), out Relationship found)
                ? found
                : new Relationship(npc, player, 0, tick.Value, 0, RelationshipTags.None);

            Relationship next = current.Apply(delta, tick, tags);
            _relationships[(npc, player)] = next;

            return ValueTask.FromResult(next);
        }
    }

    /// <inheritdoc />
    public ValueTask<EpisodeSummary> AppendEpisodeAsync(
        int npc,
        EpisodeKind kind,
        int subject,
        bool subjectIsPlayer,
        Tick tick,
        byte salience,
        string summaryKey,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(summaryKey);

        lock (_gate)
        {
            var episode = new EpisodeSummary(
                npc, ++_sequence, kind, subject, subjectIsPlayer, tick.Value, salience, summaryKey);

            if (!_episodes.TryGetValue(npc, out List<EpisodeSummary>? list))
            {
                list = [];
                _episodes[npc] = list;
            }

            list.Add(episode);

            // 상한을 넘으면 중요도가 낮은 것부터 버린다. 자르지 않으면 한 NPC 의 기억이
            // 무한히 자라고, 그것을 알아챌 계기는 메모리 경보뿐이다.
            if (list.Count > MaxEpisodesPerNpc)
            {
                list.Sort(Compare);
                list.RemoveRange(MaxEpisodesPerNpc, list.Count - MaxEpisodesPerNpc);
            }

            return ValueTask.FromResult(episode);
        }
    }

    /// <inheritdoc />
    public ValueTask<Reputation> RecordReputationAsync(
        FactionId faction, int player, int delta, Tick tick, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            int score = _reputations.TryGetValue((faction.Value, player), out Reputation found)
                ? found.Score
                : 0;

            var next = new Reputation(
                faction,
                player,
                Math.Clamp(score + delta, Relationship.MinAffinity, Relationship.MaxAffinity),
                tick.Value);

            _reputations[(faction.Value, player)] = next;

            return ValueTask.FromResult(next);
        }
    }

    /// <inheritdoc />
    public ValueTask<int> ForgetPlayerAsync(int player, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            int removed = 0;

            foreach ((int Npc, int Player) key in _relationships.Keys.Where(k => k.Player == player).ToArray())
            {
                _relationships.Remove(key);
                removed++;
            }

            foreach ((int npc, List<EpisodeSummary> list) in _episodes)
            {
                _ = npc;
                removed += list.RemoveAll(e => e.SubjectIsPlayer && e.Subject == player);
            }

            foreach ((ushort Faction, int Player) key in _reputations.Keys.Where(k => k.Player == player).ToArray())
            {
                _reputations.Remove(key);
                removed++;
            }

            return ValueTask.FromResult(removed);
        }
    }

    /// <inheritdoc />
    public ValueTask<int> PruneAsync(Tick olderThanTick, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            int removed = 0;
            long cutoff = olderThanTick.Value;

            foreach ((int Npc, int Player) key in _relationships
                .Where(kv => kv.Value.LastInteractionTick < cutoff)
                .Select(kv => kv.Key)
                .ToArray())
            {
                _relationships.Remove(key);
                removed++;
            }

            foreach ((int npc, List<EpisodeSummary> list) in _episodes)
            {
                _ = npc;
                removed += list.RemoveAll(e => e.Tick < cutoff);
            }

            foreach ((ushort Faction, int Player) key in _reputations
                .Where(kv => kv.Value.UpdatedTick < cutoff)
                .Select(kv => kv.Key)
                .ToArray())
            {
                _reputations.Remove(key);
                removed++;
            }

            return ValueTask.FromResult(removed);
        }
    }

    /// <inheritdoc />
    public ValueTask FlushAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    /// <summary>지금 든 것 전부. 파일 저장소가 압축할 때 쓴다.</summary>
    internal MemorySnapshot Snapshot()
    {
        lock (_gate)
        {
            return new MemorySnapshot(
                [.. _relationships.Values.OrderBy(r => r.Npc).ThenBy(r => r.Player)],
                [.. _episodes.Values.SelectMany(list => list).OrderBy(e => e.Sequence)],
                [.. _reputations.Values.OrderBy(r => r.Faction.Value).ThenBy(r => r.Player)],
                _sequence);
        }
    }

    /// <summary>스냅샷을 그대로 얹는다. 파일 저장소가 기동 시 부른다.</summary>
    internal void Restore(in MemorySnapshot snapshot)
    {
        lock (_gate)
        {
            _relationships.Clear();
            _episodes.Clear();
            _reputations.Clear();

            foreach (Relationship relationship in snapshot.Relationships)
            {
                _relationships[(relationship.Npc, relationship.Player)] = relationship;
            }

            foreach (EpisodeSummary episode in snapshot.Episodes)
            {
                if (!_episodes.TryGetValue(episode.Npc, out List<EpisodeSummary>? list))
                {
                    list = [];
                    _episodes[episode.Npc] = list;
                }

                list.Add(episode);
            }

            foreach (Reputation reputation in snapshot.Reputations)
            {
                _reputations[(reputation.Faction.Value, reputation.Player)] = reputation;
            }

            _sequence = snapshot.Sequence;
        }
    }

    /// <summary>salience 내림차순, 동점이면 최신순, 그래도 같으면 순번. <b>전순서여야 한다.</b></summary>
    private static int Compare(EpisodeSummary a, EpisodeSummary b)
    {
        int bySalience = b.Salience.CompareTo(a.Salience);

        if (bySalience != 0)
        {
            return bySalience;
        }

        int byTick = b.Tick.CompareTo(a.Tick);

        return byTick != 0 ? byTick : b.Sequence.CompareTo(a.Sequence);
    }
}

/// <summary>저장소 한 벌 (D-03). 파일 저장소가 읽고 쓴다.</summary>
/// <param name="Relationships">관계.</param>
/// <param name="Episodes">기억.</param>
/// <param name="Reputations">평판.</param>
/// <param name="Sequence">마지막 기억 순번.</param>
internal readonly record struct MemorySnapshot(
    ImmutableArray<Relationship> Relationships,
    ImmutableArray<EpisodeSummary> Episodes,
    ImmutableArray<Reputation> Reputations,
    long Sequence);
