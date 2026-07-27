using System.Collections.Immutable;
using Npc.Contracts;
using Npc.Core;
using Npc.MasterData;
using Npc.Planning;
using Npc.Runtime;
using Npc.Sim;

namespace Npc.Tests.Determinism;

/// <summary>
/// T5-06 — 시간 봉인 감사. CLAUDE.md §2.3 · docs/15 §3.
///
/// 게임 로직이 벽시계를 읽으면 리플레이가 어긋난다. 시간은 <c>Tick</c>(long) 하나뿐이다.
///
/// <b>화이트리스트는 어셈블리 단위로 <c>Npc.Host</c> 하나만 둔다</b> (docs/15 T5-06).
/// 개별 파일을 예외로 뚫기 시작하면 감사가 무의미해진다 — 다음 위반도 "이건 계측이라서"로 들어온다.
/// </summary>
[Trait("Category", "Determinism")]
public sealed class ClockAuditTests
{
    /// <summary>
    /// 감사 대상 — 게임 로직 전부. <c>Npc.Llm</c> 은 여기 없다:
    /// LLM 호출은 지연을 재야 하고(<c>CompileStats.LatencyMs</c>) 리플레이는 기록된 플랜을 쓴다.
    /// </summary>
    public static ImmutableArray<string> Assemblies =>
    [
        typeof(NpcCommand).Assembly.Location,      // Npc.Contracts
        typeof(BucketKey).Assembly.Location,       // Npc.Core
        typeof(MasterDataSet).Assembly.Location,   // Npc.MasterData
        typeof(PlanStore).Assembly.Location,       // Npc.Planning
        typeof(NpcStore).Assembly.Location,        // Npc.Runtime
        typeof(SimWorld).Assembly.Location,        // Npc.Sim
    ];

    /// <summary>
    /// 화이트리스트. <b>여기에 두 번째 항목이 생기면 그 자체가 리뷰 대상이다.</b>
    /// <c>Npc.Host</c> 는 틱 지연 계측(<c>NpcMeter</c>)과 10Hz 페이싱(<c>Program.PumpAsync</c>)
    /// 때문에 벽시계를 읽는다 — 둘 다 게임 로직이 아니라 호스팅이다.
    /// </summary>
    public static ImmutableArray<string> Whitelist => ["Npc.Host"];

    /// <summary>금지된 벽시계 API. 타입 전체를 막는 것과 멤버만 막는 것을 구분한다.</summary>
    private static bool IsWallClock(string type, string member) => type switch
    {
        // DateTime 자체는 자료형으로 쓸 수 있다 — 막는 것은 "지금"을 읽는 세 개다.
        "System.DateTime" or "System.DateTimeOffset" =>
            member is "get_Now" or "get_UtcNow" or "get_Today",

        // Stopwatch 는 존재 자체가 벽시계다.
        "System.Diagnostics.Stopwatch" => true,

        // Guid 는 식별자로 쓸 수 있다 — 막는 것은 순증 CorrelationId 를 대신하는 NewGuid 다.
        "System.Guid" => member is "NewGuid",

        // 부팅 후 경과 밀리초. Stopwatch 를 우회하는 흔한 길이다.
        "System.Environment" => member is "get_TickCount" or "get_TickCount64",

        // .NET 8 의 시간 추상화. 주입하면 결정론적일 수 있지만 게임 로직에는 Tick 만 있으면 된다.
        "System.TimeProvider" => member is "get_System" or "GetUtcNow" or "GetLocalNow" or "GetTimestamp",

        _ => false,
    };

    [Fact]
    public void Determinism_NoWallClock()
    {
        var found = new List<MemberUse>();

        foreach (string assembly in Assemblies)
        {
            found.AddRange(IlScanner.FindUses(assembly, IsWallClock));
        }

        Assert.True(found.Count == 0, Describe(found));
    }

    /// <summary>
    /// 화이트리스트가 <c>Npc.Host</c> 하나여야 한다 (T5-06 완료 조건).
    /// 감사 대상 목록과 화이트리스트가 겹치면 감사가 스스로를 면제한 것이다.
    /// </summary>
    [Fact]
    public void Whitelist_IsExactlyTheHost()
    {
        Assert.Equal("Npc.Host", Assert.Single(Whitelist));

        foreach (string assembly in Assemblies)
        {
            Assert.DoesNotContain(
                Path.GetFileNameWithoutExtension(assembly), Whitelist, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// 화이트리스트에 근거가 있는지 확인한다.
    /// <b><c>Npc.Host</c> 가 실제로 벽시계를 읽지 않는다면 화이트리스트를 지워야 한다</b> —
    /// 근거 없는 예외가 남아 있으면 언젠가 다른 것이 그 문으로 들어온다.
    /// </summary>
    [Fact]
    public void Whitelist_HasAReason()
    {
        string host = typeof(Npc.Host.HostOptions).Assembly.Location;

        ImmutableArray<MemberUse> uses = IlScanner.FindUses(host, IsWallClock);

        Assert.NotEmpty(uses);
    }

    /// <summary>스캐너가 벽시계 API 를 실제로 잡는지 — 감사 대상이 아닌 어셈블리로 대조한다.</summary>
    [Fact]
    public void Scanner_FindsWallClockWhereItExists()
    {
        // 이 테스트 어셈블리 자체가 DateTime.UtcNow·Stopwatch·Guid.NewGuid 를 쓴다.
        ImmutableArray<MemberUse> uses =
            IlScanner.FindUses(typeof(ClockAuditTests).Assembly.Location, IsWallClock);

        Assert.NotEmpty(uses);

        // 자료형으로만 쓰는 것은 잡지 않는다.
        Assert.All(uses, u => Assert.DoesNotContain("::ToString", u.Target, StringComparison.Ordinal));
    }

    private static string Describe(IReadOnlyCollection<MemberUse> uses) =>
        uses.Count == 0
            ? string.Empty
            : $"{uses.Count}건 발견 — 게임 로직의 시간은 Tick(long) 뿐이다:\n"
              + string.Join('\n', uses.Select(u => "  " + u));
}
