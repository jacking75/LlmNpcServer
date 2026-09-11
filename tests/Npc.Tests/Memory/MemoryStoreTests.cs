using System.Collections.Immutable;
using Npc.Contracts;
using Npc.Core;
using Npc.Memory;

namespace Npc.Tests.Memory;

/// <summary>
/// D-03 — 기억·관계 저장소.
///
/// <para>
/// <b>여기서 지키는 것은 셋이다.</b> (1) 밴드가 호감도에서 정확히 나온다,
/// (2) 플레이어 삭제가 세 종류를 전부 지운다(개인정보 요건), (3) 파일로 내렸다 올려도 같다.
/// </para>
///
/// <para>
/// <b>자연어가 저장되지 않는다</b>는 것도 형식으로 지킨다 — <c>EpisodeSummary</c> 의 문자열
/// 필드는 <c>SummaryKey</c> 하나뿐이고 그것은 로컬라이즈 키다 (CLAUDE.md §2.5).
/// </para>
/// </summary>
public sealed class MemoryStoreTests
{
    /// <summary>
    /// <b>완료 조건 — 세 번 거래한 상인이 <c>friendly</c> 를 받는다.</b>
    ///
    /// 대역의 <c>InteractAffinity</c>(+10)가 세 번이면 25 를 넘는다. 경계값을 여기 적지 않고
    /// <see cref="RelationshipBands"/> 를 쓰는 이유는, 경계를 옮길 때 두 곳이 어긋나지 않게 하려는 것이다.
    /// </summary>
    [Fact]
    public async Task ThreeTrades_MakeTheMerchantFriendly()
    {
        var store = new InMemoryStore();

        for (int i = 1; i <= 3; i++)
        {
            await store.RecordInteractionAsync(
                npc: 42, player: 7, delta: 10, new Tick(i * 100), RelationshipTags.Traded);
        }

        Relationship relationship = Assert.NotNull(await store.RelationshipAsync(42, 7));

        Assert.Equal(30, relationship.Affinity);
        Assert.Equal(3, relationship.Interactions);
        Assert.Equal(RelationshipTags.Traded, relationship.Tags);
        Assert.Equal(300, relationship.LastInteractionTick);

        Assert.Equal(RelationshipBand.Friendly, await store.BandAsync(42, 7));
        Assert.True(relationship.Affinity >= RelationshipBands.FriendlyAtOrAbove);
    }

    /// <summary>공격 한 번이 거래 세 번을 되돌린다. 밴드가 실제로 움직여야 의미가 있다.</summary>
    [Fact]
    public async Task OneAttack_UndoesThreeTrades()
    {
        var store = new InMemoryStore();

        for (int i = 0; i < 3; i++)
        {
            await store.RecordInteractionAsync(42, 7, 10, new Tick(i), RelationshipTags.Traded);
        }

        Assert.Equal(RelationshipBand.Friendly, await store.BandAsync(42, 7));

        await store.RecordInteractionAsync(42, 7, -30, new Tick(9), RelationshipTags.Attacked);

        Assert.Equal(RelationshipBand.Neutral, await store.BandAsync(42, 7));

        await store.RecordInteractionAsync(42, 7, -30, new Tick(10), RelationshipTags.Attacked);

        Assert.Equal(RelationshipBand.Hostile, await store.BandAsync(42, 7));
    }

    /// <summary>기록이 없으면 <c>Unknown</c> 이고, 그것은 서픽스에 실리지 않는다.</summary>
    [Fact]
    public async Task NoRecord_IsUnknown()
    {
        var store = new InMemoryStore();

        Assert.Equal(RelationshipBand.Unknown, await store.BandAsync(1, 1));
        Assert.Null(await store.RelationshipAsync(1, 1));
        Assert.Empty(await store.EpisodesAsync(1, 10));
        Assert.Equal(string.Empty, RelationshipBands.ToText(RelationshipBand.Unknown));
    }

    /// <summary>호감도는 ±100 에서 멈춘다. 자르는 곳이 한 군데여야 백엔드마다 다른 값이 안 남는다.</summary>
    [Fact]
    public async Task Affinity_SaturatesAtTheBounds()
    {
        var store = new InMemoryStore();

        for (int i = 0; i < 30; i++)
        {
            await store.RecordInteractionAsync(1, 1, 10, new Tick(i));
        }

        Assert.Equal(Relationship.MaxAffinity, Assert.NotNull(await store.RelationshipAsync(1, 1)).Affinity);

        for (int i = 0; i < 60; i++)
        {
            await store.RecordInteractionAsync(1, 1, -10, new Tick(100 + i));
        }

        Assert.Equal(Relationship.MinAffinity, Assert.NotNull(await store.RelationshipAsync(1, 1)).Affinity);
    }

    /// <summary>기억은 salience 내림차순이고 상한을 넘으면 낮은 것부터 밀린다.</summary>
    [Fact]
    public async Task Episodes_AreOrderedAndBounded()
    {
        var store = new InMemoryStore();

        for (int i = 0; i < InMemoryStore.MaxEpisodesPerNpc + 10; i++)
        {
            await store.AppendEpisodeAsync(
                1, EpisodeKind.Trade, 7, subjectIsPlayer: true, new Tick(i),
                salience: (byte)(i % 200), summaryKey: "dialogue.trade");
        }

        ImmutableArray<EpisodeSummary> episodes = await store.EpisodesAsync(1, 100);

        Assert.Equal(InMemoryStore.MaxEpisodesPerNpc, episodes.Length);

        for (int i = 1; i < episodes.Length; i++)
        {
            Assert.True(
                episodes[i - 1].Salience >= episodes[i].Salience,
                "salience 내림차순이 아니다");
        }

        // 같은 입력이면 같은 순서다 — Dictionary 순회에 기대면 여기가 흔들린다.
        //
        // <b>ImmutableArray 끼리 비교하지 않는다.</b> ImmutableArray 의 Equals 는 내부 배열의
        // 참조 비교라 내용이 같아도 false 다 — 두 번 호출하면 언제나 다른 배열이 나온다.
        Assert.Equal(episodes.ToArray(), (await store.EpisodesAsync(1, 100)).ToArray());
    }

    /// <summary>
    /// <b>플레이어 삭제는 세 종류를 전부 지운다</b> (개인정보 요건).
    /// 하나라도 남으면 "지웠다" 고 말할 수 없다.
    /// </summary>
    [Fact]
    public async Task ForgetPlayer_RemovesEverything()
    {
        var store = new InMemoryStore();
        var faction = new FactionId(2);

        await store.RecordInteractionAsync(1, 7, 10, new Tick(1), RelationshipTags.Traded);
        await store.RecordInteractionAsync(2, 7, -10, new Tick(2), RelationshipTags.Attacked);
        await store.RecordInteractionAsync(1, 8, 10, new Tick(3));
        await store.AppendEpisodeAsync(1, EpisodeKind.Trade, 7, true, new Tick(4), 10, "dialogue.trade");
        await store.AppendEpisodeAsync(1, EpisodeKind.Trade, 8, true, new Tick(5), 10, "dialogue.trade");
        await store.RecordReputationAsync(faction, 7, -20, new Tick(6));

        int removed = await store.ForgetPlayerAsync(7);

        Assert.Equal(4, removed);
        Assert.Null(await store.RelationshipAsync(1, 7));
        Assert.Null(await store.RelationshipAsync(2, 7));
        Assert.Null(await store.ReputationAsync(faction, 7));
        Assert.All(await store.EpisodesAsync(1, 100), e => Assert.NotEqual(7, e.Subject));

        // 다른 플레이어는 그대로다.
        Assert.NotNull(await store.RelationshipAsync(1, 8));
    }

    /// <summary>보존 기간이 지난 기록은 지운다. <b>기준은 틱이다</b> — 벽시계가 아니다.</summary>
    [Fact]
    public async Task Prune_DropsOldRecordsByTick()
    {
        var store = new InMemoryStore();

        await store.RecordInteractionAsync(1, 7, 10, new Tick(100));
        await store.RecordInteractionAsync(2, 7, 10, new Tick(900));
        await store.AppendEpisodeAsync(1, EpisodeKind.Trade, 7, true, new Tick(100), 10, "dialogue.trade");
        await store.AppendEpisodeAsync(2, EpisodeKind.Trade, 7, true, new Tick(900), 10, "dialogue.trade");

        int removed = await store.PruneAsync(new Tick(500));

        Assert.Equal(2, removed);
        Assert.Null(await store.RelationshipAsync(1, 7));
        Assert.NotNull(await store.RelationshipAsync(2, 7));
        Assert.Empty(await store.EpisodesAsync(1, 10));
        Assert.Single(await store.EpisodesAsync(2, 10));
    }

    /// <summary>세력 평판도 같은 규칙으로 든다.</summary>
    [Fact]
    public async Task Reputation_RoundTrips()
    {
        var store = new InMemoryStore();
        var bandit = new FactionId(5);

        await store.RecordReputationAsync(bandit, 7, -40, new Tick(10));
        await store.RecordReputationAsync(bandit, 7, -40, new Tick(20));

        Reputation found = Assert.NotNull(await store.ReputationAsync(bandit, 7));

        Assert.Equal(-80, found.Score);
        Assert.Equal(20, found.UpdatedTick);
    }

    /// <summary>
    /// 파일로 내렸다 올리면 같다. <b>임시 파일 → 바꿔 끼우기</b>라 중간에 죽어도 옛 파일이 남는다.
    /// </summary>
    [Fact]
    public async Task FileStore_RoundTrips()
    {
        string directory = NewDirectory();

        FileMemoryStore first = FileMemoryStore.Open(directory);

        Assert.False(first.Loaded);

        await first.RecordInteractionAsync(42, 7, 30, new Tick(300), RelationshipTags.Traded);
        await first.AppendEpisodeAsync(
            42, EpisodeKind.Trade, 7, true, new Tick(300), 60, "dialogue.trade");
        await first.RecordReputationAsync(new FactionId(2), 7, 15, new Tick(300));
        await first.FlushAsync();

        FileMemoryStore second = FileMemoryStore.Open(directory);

        Assert.True(second.Loaded);
        Assert.Equal(RelationshipBand.Friendly, await second.BandAsync(42, 7));

        Relationship relationship = Assert.NotNull(await second.RelationshipAsync(42, 7));

        Assert.Equal(30, relationship.Affinity);
        Assert.Equal(RelationshipTags.Traded, relationship.Tags);

        EpisodeSummary episode = Assert.Single(await second.EpisodesAsync(42, 10));

        Assert.Equal(EpisodeKind.Trade, episode.Kind);
        Assert.Equal("dialogue.trade", episode.SummaryKey);
        Assert.True(episode.SubjectIsPlayer);

        Assert.Equal(15, Assert.NotNull(await second.ReputationAsync(new FactionId(2), 7)).Score);

        // 순번이 이어진다 — 다시 올린 뒤 붙인 기억이 앞의 것과 같은 번호를 쓰면 정렬이 깨진다.
        EpisodeSummary next = await second.AppendEpisodeAsync(
            42, EpisodeKind.Combat, 7, true, new Tick(400), 10, "dialogue.combat");

        Assert.True(next.Sequence > episode.Sequence);
    }

    /// <summary>형식 버전이 다르면 읽지 않는다 — 낡은 기억을 잘못 해석하느니 멈춘다.</summary>
    [Fact]
    public void FileStore_RejectsAnotherFormatVersion()
    {
        string directory = NewDirectory();

        File.WriteAllText(
            Path.Combine(directory, FileMemoryStore.FileName),
            """{"version":99,"sequence":0,"relationships":[],"episodes":[],"reputations":[]}""");

        Assert.Throws<InvalidDataException>(() => FileMemoryStore.Open(directory));
    }

    /// <summary>
    /// <b>자연어를 담을 자리가 없다.</b> <c>EpisodeSummary</c> 의 문자열 필드는
    /// <c>SummaryKey</c> 하나이고 그것은 로컬라이즈 키다 (CLAUDE.md §2.5).
    /// </summary>
    [Fact]
    public void Records_HaveNoFreeTextField()
    {
        string[] strings =
        [
            .. typeof(EpisodeSummary)
                .GetProperties()
                .Where(p => p.PropertyType == typeof(string))
                .Select(p => p.Name),
        ];

        Assert.Equal(["SummaryKey"], strings);

        Assert.DoesNotContain(
            typeof(Relationship).GetProperties(), p => p.PropertyType == typeof(string));

        Assert.DoesNotContain(
            typeof(Reputation).GetProperties(), p => p.PropertyType == typeof(string));
    }

    private static string NewDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "npc-d03-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(directory);

        return directory;
    }
}
