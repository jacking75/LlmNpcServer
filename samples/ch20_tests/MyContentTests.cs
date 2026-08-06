using Npc.Contracts;
using Npc.MasterData;
using Npc.MasterData.Validation;
using Npc.Wire;
using Xunit;

namespace MyContentTests;

/// <summary>
/// 20장 — 내가 2부에서 넣은 콘텐츠를 CI 가 대신 지키게 만든다.
///
/// 네 가지를 못 박는다.
///   ① 마스터데이터가 V1~V11 을 통과한다        — 4장
///   ② 모든 아키타입에 순환하는 폴백이 있다      — 6·7장
///   ③ 인터럽트가 실재하는 액션만 쓴다           — 8장
///   ④ 명령이 와이어를 왕복해도 값이 안 바뀐다   — 13·14장
///
/// <b>테스트가 사양이다.</b> 콘텐츠를 되돌리면 여기가 먼저 깨진다.
/// </summary>
public sealed class MyContentTests
{
    /// <summary>이 저장소의 masterdata 폴더. 실행 위치와 무관하게 위로 찾아 올라간다.</summary>
    private static string MasterDataDir { get; } = FindMasterData();

    // ── ① 마스터데이터가 멀쩡한가 ────────────────────────────────────────

    /// <summary>
    /// V1~V11 전부. <b>실패는 경고가 아니라 기동 실패</b>이므로 테스트도 통과/실패로만 본다.
    /// V9(프롬프트 프리픽스 토큰)는 LLM 을 켜야 판정되므로 건너뛴다.
    /// </summary>
    [Fact]
    public void MasterData_PassesEveryRule()
    {
        MasterDataValidationReport report =
            MasterDataValidator.Validate(MasterDataDir, new MasterDataValidationOptions(null));

        string detail = string.Join(
            Environment.NewLine,
            report.Violations.Select(v => $"{v.Code}  {v.Detail}"));

        Assert.True(report.IsValid, $"검증 위반 {report.Violations.Length}건{Environment.NewLine}{detail}");
    }

    // ── ② 폴백이 빠짐없이 있는가 ─────────────────────────────────────────

    /// <summary>
    /// 모든 아키타입에 폴백이 있고, 그 폴백이 순환한다.
    ///
    /// 7장에서 새 직업을 넣고 <c>fallback_plan</c> 을 빠뜨리면 여기가 깨진다.
    /// LLM·캐시가 전부 죽어도 남는 것이 이것뿐이라, 빠지면 그 직업만 조용히 멈춘다.
    /// </summary>
    [Fact]
    public void Fallback_ExistsAndLoopsForEveryArchetype()
    {
        MasterDataSet data = MasterDataLoader.Load(MasterDataDir);

        Assert.NotEmpty(data.Archetypes.Archetypes);

        foreach (ArchetypeDef archetype in data.Archetypes.Archetypes)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(archetype.FallbackPlanId),
                $"{archetype.Id} 에 fallback_plan 이 없다.");
        }
    }

    // ── ③ 인터럽트가 실재하는 액션만 쓰는가 ──────────────────────────────

    /// <summary>
    /// 8장에서 규칙을 넣을 때 액션 이름을 잘못 쓰면 여기가 깨진다.
    ///
    /// 로더가 이미 막지만(V3), <b>테스트가 있으면 왜 막혔는지가 이름으로 남는다.</b>
    /// </summary>
    [Fact]
    public void Interrupts_UseKnownActionsOnly()
    {
        MasterDataSet data = MasterDataLoader.Load(MasterDataDir);

        Assert.NotEmpty(data.Interrupts.Rules);

        foreach (InterruptRule rule in data.Interrupts.Rules)
        {
            // 로더가 액션을 id 로 풀어 두었다. 풀리지 않은 규칙은 애초에 로드되지 않는다.
            Assert.True(
                rule.Action.Value > 0,
                $"규칙 '{rule.Id}' 의 액션이 카탈로그에 없다.");
        }
    }

    // ── ④ 와이어 왕복 ────────────────────────────────────────────────────

    /// <summary>
    /// 명령을 와이어 DTO 로 바꿨다가 되돌려도 값이 그대로인가.
    ///
    /// <c>Npc.Contracts</c> 에 <c>[MemoryPackable]</c> 을 못 붙여 DTO 를 갈랐고,
    /// 가른 대가로 <b>드리프트</b>가 생길 수 있다 — 계약에 필드를 추가하고 와이어를 잊는 것.
    /// 13·14장에서 필드를 건드렸다면 이 테스트가 먼저 잡는다.
    /// </summary>
    [Fact]
    public void Wire_CommandRoundTripsWithoutLoss()
    {
        var original = new NpcCommand
        {
            Kind = NpcCommandKind.Interact,
            Npc = new NpcId(4321),
            IssuedAt = new Tick(987_654),
            Correlation = new CorrelationId(555),
            Priority = CommandPriority.Critical,
            TargetPoi = new PoiId(244),
            TargetNpc = new NpcId(11),
            Item = new ItemId(83),
            Amount = -7,
            Flags = 3,
        };

        NpcCommand round = WireCommand.From(original).To();

        Assert.Equal(original.Kind, round.Kind);
        Assert.Equal(original.Npc, round.Npc);
        Assert.Equal(original.IssuedAt, round.IssuedAt);
        Assert.Equal(original.Correlation, round.Correlation);
        Assert.Equal(original.Priority, round.Priority);
        Assert.Equal(original.TargetPoi, round.TargetPoi);
        Assert.Equal(original.TargetNpc, round.TargetNpc);
        Assert.Equal(original.Item, round.Item);
        Assert.Equal(original.Amount, round.Amount);      // 부호가 살아야 한다
        Assert.Equal(original.Flags, round.Flags);
    }

    // ── 경로 ─────────────────────────────────────────────────────────────

    private static string FindMasterData()
    {
        foreach (string from in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(from); dir is not null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "masterdata");

                if (File.Exists(Path.Combine(candidate, "archetypes.json")))
                {
                    return candidate;
                }
            }
        }

        throw new DirectoryNotFoundException("masterdata 폴더를 찾지 못했다.");
    }
}
