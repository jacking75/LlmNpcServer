using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Npc.Contracts;
using Npc.Core;
using Npc.Gateway;
using Npc.MasterData;
using Npc.Narrate;
using Npc.Narrative;

namespace Npc.Tests.Narrate;

/// <summary>
/// T5-12 — 로그 → 서술. docs/15 §6.
///
/// 두 가지를 지킨다.
/// <list type="number">
///   <item>§6 예시 형식으로 나온다 (머리말 + <c>HH:MM  본문</c>).</item>
///   <item><b>어느 쪽(LLM/사람)이 만든 플랜인지 서술에서 드러나지 않는다.</b>
///         드러나면 블라인드 평가가 성립하지 않는다.</item>
/// </list>
/// </summary>
public sealed class NarrateTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private static readonly NpcInstanceTable s_instances =
        NpcInstanceTable.Load(Path.Combine(TestPaths.MasterData, "npc_instances.json"), s_data);

    /// <summary>머리말. 예: <c>[NPC #1247 · 대장장이 · 공성 중 · 추위 저녁]</c>.</summary>
    private static readonly Regex s_header = new(@"^\[NPC #\d+ · .+ · .+ · .+\]$", RegexOptions.CultureInvariant);

    /// <summary>본문 한 줄. 예: <c>06:45  대장간 도착</c>.</summary>
    private static readonly Regex s_line = new(@"^\d{2}:\d{2}  \S.*$", RegexOptions.CultureInvariant);

    // ---------------------------------------------------------------- 형식

    [Fact]
    public void Narrate_MatchesTheSpecFormat()
    {
        string[] lines = Narrate(BlacksmithDay());

        Assert.Matches(s_header, lines[0]);
        Assert.True(lines.Length > 1, "본문이 비었다.");

        foreach (string line in lines.Skip(1))
        {
            Assert.Matches(s_line, line);
        }

        // 시각이 흐른다 — 뒤로 가는 일지는 일지가 아니다.
        string[] clocks = [.. lines.Skip(1).Select(l => l[..5])];

        Assert.Equal(clocks.Order(StringComparer.Ordinal), clocks);
    }

    [Fact]
    public void Narrate_ReadsLikeADayNotALog()
    {
        string text = string.Join('\n', Narrate(BlacksmithDay()));

        // 도착 → 제작 → 귀가 → 취침. §6 예시가 보여 주는 하루의 모양이다.
        Assert.Contains("대장간", text, StringComparison.Ordinal);
        Assert.Contains("철검", text, StringComparison.Ordinal);
        Assert.Contains("잠자리에 듦", text, StringComparison.Ordinal);

        // 조사가 다듬어져 있다 — "물을(를)" 같은 표기가 남으면 사람이 읽는 글이 아니다.
        Assert.DoesNotContain("(를)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("(으)로", text, StringComparison.Ordinal);
        Assert.Contains("대장간으로 이동", text, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- A/B 단서가 없다

    /// <summary>
    /// 서술에 틱 번호·상관 ID·플랜 id 가 남지 않는다 (docs/15 §6).
    /// 그 자체가 A/B 를 가르는 단서가 된다.
    /// </summary>
    [Fact]
    public void Narrate_LeaksNoTickCorrelationOrPlanId()
    {
        string text = string.Join('\n', Narrate(BlacksmithDay()));

        foreach (string marker in new[] { "tick", "Tick", "correlation", "Correlation", "plan", "Plan", "#G-", "bucket" })
        {
            Assert.DoesNotContain(marker, text, StringComparison.Ordinal);
        }

        // 숫자가 나오는 자리는 셋뿐이다: 머리말의 NPC 번호 · 시각 · 수량.
        // 틱 번호(수천~수만)가 새면 여기서 걸린다.
        foreach (Match match in Regex.Matches(text, @"\d+", RegexOptions.CultureInvariant))
        {
            Assert.True(match.Value.Length <= 4, $"긴 숫자가 새어 나왔다: {match.Value}");
        }
    }

    /// <summary>
    /// <b>서술은 명령 열만의 함수다.</b> 같은 명령 열이면 그 플랜을 누가 만들었든 같은 글이 나온다 —
    /// 이것이 "어느 쪽인지 드러나지 않는다"의 정확한 뜻이다.
    ///
    /// 패킷에 플랜 출처를 실을 자리가 없다는 것도 같이 고정한다 (N2·N3).
    /// </summary>
    [Fact]
    public void Narrate_IsAFunctionOfTheCommandStreamAlone()
    {
        ImmutableArray<LinkRecord> records = BlacksmithDay();

        Assert.Equal(Narrate(records), Narrate(records));

        // NpcCommand·GameEvent 에 플랜/출처를 담을 필드가 없다.
        foreach (Type packet in new[] { typeof(NpcCommand), typeof(GameEvent) })
        {
            foreach (System.Reflection.PropertyInfo property in packet.GetProperties())
            {
                Assert.DoesNotContain("Plan", property.Name, StringComparison.Ordinal);
                Assert.DoesNotContain("Origin", property.Name, StringComparison.Ordinal);
            }
        }
    }

    // ---------------------------------------------------------------- 반복 억제

    /// <summary>
    /// 하루 일과는 <c>loop</c> 플랜이라 같은 사이클이 계속 돈다.
    /// 기본값은 한 바퀴 — 반복분을 그대로 실으면 읽는 사람이 같은 글을 스무 번 본다.
    /// </summary>
    [Fact]
    public void Narrate_StopsAfterOneCycleByDefault()
    {
        ImmutableArray<LinkRecord> twice = [.. BlacksmithDay(), .. BlacksmithDay(startTick: 500)];

        Assert.Equal(Narrate(BlacksmithDay()).Length, Narrate(twice).Length);
        Assert.True(Narrate(twice, cycles: 2).Length > Narrate(twice).Length);
    }

    // ---------------------------------------------------------------- 어휘

    /// <summary>
    /// 사전이 마스터데이터의 id 를 전부 덮는가.
    /// 빠지면 영어 id 가 조용히 새어 나가 두 군이 다르게 읽힐 수 있다.
    /// </summary>
    [Fact]
    public void Lexicon_CoversEveryId()
    {
        foreach (ArchetypeDef archetype in s_data.Archetypes.Archetypes)
        {
            Assert.True(Lexicon.Archetypes.ContainsKey(archetype.Id), $"아키타입 '{archetype.Id}' 표기가 없다.");
        }

        foreach (ItemDef item in s_data.Items.Items)
        {
            Assert.True(Lexicon.Items.ContainsKey(item.Id), $"아이템 '{item.Id}' 표기가 없다.");
        }

        foreach (string subtype in s_data.Pois.Pois.Select(p => p.Subtype).Distinct(StringComparer.Ordinal))
        {
            Assert.True(Lexicon.Places.ContainsKey(subtype), $"장소 '{subtype}' 표기가 없다.");
        }

        // 반대 방향 — 사전에만 있고 마스터데이터에 없는 id 는 낡은 것이다.
        var ids = s_data.Items.Items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);

        Assert.All(Lexicon.Items.Keys, id => Assert.Contains(id, ids));
    }

    [Fact]
    public void Lexicon_AttachesTheRightParticle()
    {
        // 받침 있음 → 을 / 없음 → 를
        Assert.Equal("물을", Lexicon.With("물", "을", "를"));
        Assert.Equal("차를", Lexicon.With("차", "을", "를"));

        // ~(으)로 — ㄹ 받침은 "로"
        Assert.Equal("대장간으로", Lexicon.To("대장간"));
        Assert.Equal("광산으로", Lexicon.To("광산"));
        Assert.Equal("낚시터로", Lexicon.To("낚시터"));
        Assert.Equal("길로", Lexicon.To("길"));
    }

    // ---------------------------------------------------------------- 옵션

    [Fact]
    public void Options_RejectUnknownArgumentsAndRequireTrace()
    {
        Assert.False(NarrateOptions.TryParse(["--nope"], out _, out string? error));
        Assert.Contains("모르는 인자", error!, StringComparison.Ordinal);

        Assert.False(NarrateOptions.TryParse([], out _, out error));
        Assert.Contains("--trace", error!, StringComparison.Ordinal);

        Assert.False(NarrateOptions.TryParse(["--trace", "a", "--region", "Chaos"], out _, out _));

        Assert.True(NarrateOptions.TryParse(
            ["--trace", "a", "--region", "war", "--climate", "storm", "--cycles", "3"],
            out NarrateOptions options,
            out _));

        Assert.Equal(RegionState.War, options.Region);
        Assert.Equal(Climate.Storm, options.Climate);
        Assert.Equal(3, options.Cycles);
    }

    // ---------------------------------------------------------------- 헬퍼

    private static string[] Narrate(ImmutableArray<LinkRecord> records, int cycles = 1)
    {
        var options = new NarrateOptions(
            Trace: "(inline)",
            MasterData: TestPaths.MasterData,
            Npcs: [],
            OutputDirectory: null,
            TimeScale: 600,
            StartGameHour: NarrateOptions.DefaultStartHour,
            MaxLines: NarrateOptions.DefaultMaxLines,
            Region: RegionState.War,
            Climate: Climate.Cold,
            Cycles: cycles,
            Population: s_instances.Count);

        var narrator = new Narrator(s_data, s_instances, options);

        return narrator.NarrateFrom(records, npc: 0)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>대장장이의 한 바퀴. §6 예시와 같은 모양이다.</summary>
    private static ImmutableArray<LinkRecord> BlacksmithDay(long startTick = 0)
    {
        PoiId smithy = Poi("smithy");
        PoiId home = Poi("house");
        ItemId sword = Item("iron_sword");

        return
        [
            Command(startTick + 1, NpcCommandKind.MoveTo, poi: smithy, flags: (byte)MoveSpeed.Walk),
            Event(startTick + 4, GameEventKind.NpcArrived, smithy),
            Command(startTick + 5, NpcCommandKind.Interact, poi: smithy, item: sword, amount: 3),
            Command(startTick + 20, NpcCommandKind.InventoryChange, item: sword, amount: 3),
            Command(startTick + 22, NpcCommandKind.MoveTo, poi: home, flags: (byte)MoveSpeed.Run),
            Event(startTick + 26, GameEventKind.NpcArrived, home),
            Command(startTick + 27, NpcCommandKind.SetVisualState, visual: VisualState.Sleeping),
        ];
    }

    private static PoiId Poi(string subtype) => s_data.Pois.OfSubtype(subtype)[0];

    private static ItemId Item(string id)
    {
        Assert.True(s_data.Items.TryGet(id, out ItemDef item));
        return item.Code;
    }

    private static LinkRecord Command(
        long tick,
        NpcCommandKind kind,
        PoiId poi = default,
        ItemId item = default,
        int amount = 0,
        byte flags = 0,
        VisualState visual = VisualState.Idle) =>
        new(RecordKind.Command, new NpcCommand
        {
            Kind = kind,
            Npc = new NpcId(0),
            IssuedAt = new Tick(tick),
            Correlation = new CorrelationId((uint)tick),
            Priority = CommandPriority.Normal,
            TargetPoi = poi,
            Item = item,
            Amount = amount,
            Flags = flags,
            Visual = visual,
        }, null);

    private static LinkRecord Event(long tick, GameEventKind kind, PoiId poi) =>
        new(RecordKind.Event, null, new GameEvent
        {
            Kind = kind,
            Sequence = tick,
            OccurredAt = new Tick(tick),
            Npc = new NpcId(0),
            Poi = poi,
        });
}
