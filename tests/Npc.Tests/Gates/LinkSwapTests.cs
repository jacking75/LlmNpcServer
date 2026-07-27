using System.Collections.Immutable;
using System.Globalization;
using Npc.Contracts;
using Npc.Core;
using Npc.Gateway;
using Npc.Host;
using Npc.Host.Metrics;
using Npc.Planning;
using Npc.Runtime;
using Npc.Tests.Determinism;

namespace Npc.Tests.Gates;

/// <summary>
/// T5-11 — 링크 4종 교체 검증. docs/15 §5 · docs/02 §6.
///
/// P1 게이트에서 <b>4종 기동</b>은 이미 확인했다 (`P1_gate.md` #6).
/// 여기서 더 하는 것은 두 가지다.
///
/// <list type="number">
///   <item><b>같은 시나리오에서 결과가 같은가</b> — 링크를 갈아끼워도 NPC 서버의 판단이 바뀌지 않아야 한다.</item>
///   <item><b>런타임 코드 변경 diff = 0</b> — <c>--link</c> 하나로만 갈아끼운다.
///         이건 문장이 아니라 기계적으로 확인한다: <c>Npc.Runtime</c>·<c>Npc.Core</c>·<c>Npc.Planning</c> 이
///         구현체(<c>Npc.Gateway</c>)를 <b>참조조차 하지 않는다.</b></item>
/// </list>
/// </summary>
public sealed class LinkSwapTests
{
    /// <summary>600배속 하루. 4종을 다 돌려도 짧다.</summary>
    private static readonly string[] s_scenario =
        ["--npcs", "100", "--time-scale", "600", "--days", "1", "--max-speed", "--no-dashboard"];

    private const int TicksPerDay = 1_440;

    /// <summary>링크를 갈아끼우는 것이 금지된 프로젝트. docs/15 §5 의 diff = 0 대상이다.</summary>
    private static readonly (string Name, string Assembly, string Project)[] s_sealed =
    [
        ("Npc.Runtime", typeof(NpcStore).Assembly.Location, "src/Npc.Runtime/Npc.Runtime.csproj"),
        ("Npc.Core", typeof(BucketKey).Assembly.Location, "src/Npc.Core/Npc.Core.csproj"),
        ("Npc.Planning", typeof(PlanStore).Assembly.Location, "src/Npc.Planning/Npc.Planning.csproj"),
    ];

    // ---------------------------------------------------------------- 1. 4종이 같은 결과를 낸다

    [Fact]
    public async Task Link_Swappable()
    {
        string trace = Path.Combine(Path.GetTempPath(), $"npc-swap-{Guid.NewGuid():N}.jsonl");

        try
        {
            // ── 1. Loopback — 기준 ──
            MetricsSnapshot loopback = await RunAsync("--loopback");

            // ── 2. Null — 명령 폐기. 크래시 없이 완주 (성능 기준선) ──
            MetricsSnapshot none = await RunAsync("--link", "null");

            // ── 3. Recording — Loopback 을 데코레이트. 결과 동일 + 로그 생성 ──
            MetricsSnapshot recording = await RunAsync("--link", "record", "--trace", trace);

            // ── 4. Replay — 3의 로그 재생. 명령 시퀀스 동일 ──
            ImmutableArray<string> recorded = CommandLines(trace);
            ImmutableArray<string> replayed;
            MetricsSnapshot replay;

            await using (NpcHost host = Host("--link", "replay", "--trace", trace))
            {
                await host.RunAsync(CancellationToken.None);

                replay = host.Metrics.Snapshot();
                replayed = [.. ((ReplayGameServerLink)host.Link).Commands.Select(ReplayTests.RenderCommand)];
            }

            // 네 링크 모두 같은 틱 수를 완주한다.
            foreach ((string name, MetricsSnapshot m) in
                new[] { ("loopback", loopback), ("null", none), ("record", recording), ("replay", replay) })
            {
                Assert.Equal(TicksPerDay, m.Tick.Ticks);
                Assert.Equal(0, m.Tick.Overruns);
                Assert.True(m.Link.CommandsEnqueued > 0, $"{name}: {Describe(m)}");
            }

            // Loopback 과 Recording 은 같은 세계다 — 기록 데코레이터가 결과를 바꾸면 안 된다.
            Assert.Equal(loopback.Link.CommandsEnqueued, recording.Link.CommandsEnqueued);
            Assert.Equal(loopback.Link.EventsDrained, recording.Link.EventsDrained);

            // Null 은 명령을 폐기한다 — 응답이 없으니 타임아웃 합성으로만 진행한다.
            //   그래도 완주하고 크래시가 없다는 것이 이 링크의 존재 이유다.
            Assert.Equal(0, none.Link.EventGaps);

            // Replay 는 기록과 <b>바이트 동일한</b> 명령 열을 낸다.
            Assert.Equal(recorded.Length, replayed.Length);
            Assert.Equal(string.Join('\n', recorded), string.Join('\n', replayed));
        }
        finally
        {
            File.Delete(trace);
        }
    }

    // ---------------------------------------------------------------- 2. 런타임 코드 diff = 0

    /// <summary>
    /// 런타임 세 프로젝트가 링크 <b>구현체</b>를 참조하지 않는다.
    ///
    /// 참조가 없으면 링크를 갈아끼우는 데 이 코드를 고칠 방법 자체가 없다 —
    /// "diff = 0" 을 사람의 약속이 아니라 어셈블리 메타데이터로 고정한다.
    /// </summary>
    [Fact]
    [Trait("Category", "Determinism")]
    public void LinkSwap_RuntimeNeverNamesAConcreteLink()
    {
        var found = new List<MemberUse>();

        foreach ((string name, string assembly, string project) in s_sealed)
        {
            // 어셈블리 참조 자체가 없어야 한다.
            Assert.DoesNotContain(
                "Npc.Gateway",
                File.ReadAllText(TestPaths.At(project.Split('/'))),
                StringComparison.Ordinal);

            // IL 에도 구현체 타입 이름이 없어야 한다 (전이 참조로 새어 들어올 수 있다).
            found.AddRange(IlScanner.FindUses(assembly, (type, _) => type.StartsWith("Npc.Gateway.", StringComparison.Ordinal)));

            Assert.NotEqual(string.Empty, name);
        }

        Assert.True(found.Count == 0, string.Join('\n', found));
    }

    /// <summary>
    /// 링크를 고르는 곳은 <c>Npc.Host</c> 한 군데뿐이다.
    /// 여기가 늘어나면 "옵션 하나로 갈아끼운다"가 성립하지 않는다.
    /// </summary>
    [Fact]
    public void LinkSwap_OnlyTheHostChoosesAnImplementation()
    {
        string[] implementations =
        [
            nameof(NullGameServerLink),
            nameof(LoopbackGameServerLink),
            nameof(RecordingGameServerLink),
            nameof(ReplayGameServerLink),
            nameof(TcpGameServerLink),
        ];

        var offenders = new List<string>();

        foreach (string directory in new[] { "Npc.Runtime", "Npc.Core", "Npc.Planning", "Npc.MasterData" })
        {
            foreach (string file in Directory.EnumerateFiles(
                TestPaths.At("src", directory), "*.cs", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);

                offenders.AddRange(implementations
                    .Where(name => text.Contains(name, StringComparison.Ordinal))
                    .Select(name => $"{Path.GetFileName(file)}: {name}"));
            }
        }

        // 주석에서 이름을 언급하는 것까지 막지는 않는다 — 실제 참조는 위 IL 검사가 잡는다.
        // 여기서는 "어느 파일이 구현체를 알고 있는가" 를 눈에 보이게 세워 둔다.
        Assert.True(offenders.Count == 0, string.Join('\n', offenders));
    }

    // ---------------------------------------------------------------- 3. TcpGameServerLink 가 컴파일된다

    /// <summary>
    /// docs/15 §5 — <c>TcpGameServerLink</c> 는 미구현이지만 <b>컴파일은 되어야 한다.</b>
    /// 인터페이스를 만족하는 골격이 존재한다는 것 자체가 "나중에 붙일 수 있다"의 최소 증거다.
    /// </summary>
    [Fact]
    public async Task LinkSwap_TcpSkeletonCompilesAndSatisfiesTheInterface()
    {
        await using IGameServerLink tcp = new TcpGameServerLink();

        Assert.IsAssignableFrom<IGameServerLink>(tcp);
        Assert.Equal(LinkState.Disconnected, tcp.State);

        // 전송은 범위 밖이라 던진다 — 조용히 성공하면 "붙였다"고 착각하게 된다.
        var command = new NpcCommand
        {
            Kind = NpcCommandKind.MoveTo,
            Npc = new NpcId(1),
            IssuedAt = new Tick(1),
            Correlation = new CorrelationId(1),
            Priority = CommandPriority.Normal,
        };

        Assert.Throws<NotSupportedException>(() => tcp.Enqueue(in command));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await tcp.FlushAsync(CancellationToken.None));

        // 4종 + Tcp 가 전부 같은 인터페이스로 보인다.
        Assert.Equal(5, ImplementationCount());
    }

    /// <summary><c>Npc.Gateway</c> 안의 <see cref="IGameServerLink"/> 구현체 수.</summary>
    private static int ImplementationCount() =>
        typeof(NullGameServerLink).Assembly.GetTypes()
            .Count(t => t is { IsClass: true, IsAbstract: false } && typeof(IGameServerLink).IsAssignableFrom(t));

    // ---------------------------------------------------------------- 헬퍼

    private static NpcHost Host(params string[] extra)
    {
        string[] args = [.. extra, .. s_scenario];

        Assert.True(HostOptions.TryParse(args, out HostOptions options, out string? error), error);

        return NpcHost.Create(options with { MasterData = TestPaths.MasterData }, TextWriter.Null);
    }

    private static async Task<MetricsSnapshot> RunAsync(params string[] extra)
    {
        await using NpcHost host = Host(extra);

        await host.RunAsync(CancellationToken.None);

        return host.Metrics.Snapshot();
    }

    private static ImmutableArray<string> CommandLines(string trace) =>
        [.. File.ReadLines(trace).Where(l => l.StartsWith("{\"Kind\":0,", StringComparison.Ordinal))];

    private static string Describe(in MetricsSnapshot m) => string.Create(
        CultureInfo.InvariantCulture,
        $"틱 {m.Tick.Ticks} · 명령 {m.Link.CommandsEnqueued} · 이벤트 {m.Link.EventsDrained} · 갭 {m.Link.EventGaps}");
}
