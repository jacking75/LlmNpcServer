using System.Collections.Immutable;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.Core.Validation;
using Npc.MasterData;
using Npc.Narrative;

namespace Npc.Tests.Narrative;

/// <summary>
/// F-03 설명 생성기.
///
/// <b>카드가 검증기와 다른 판정을 내면 카드는 쓸모가 없다.</b> 검수자가 "괜찮다" 는 카드를
/// 보고 승인했는데 기동이 거절되면, 그 다음부터는 아무도 카드를 보지 않는다.
/// 여기서 강제하는 것이 그 일치다.
/// </summary>
public sealed class NarrativeTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    /// <summary>
    /// 카드가 <b>모델하지 않는</b> 검증 코드. 자원 수지는 스텝 하나의 성질이 아니라
    /// 플랜 전체의 합이라 별도 표로 낸다 — 목록을 여기 고정해 두면 나중에 조용히 늘지 않는다.
    /// </summary>
    private static readonly ImmutableArray<string> NotTraced = ["V3.RESOURCE_IMBALANCE"];

    [Fact]
    public void Card_RendersEveryArchetype()
    {
        foreach (ArchetypeDef def in s_data.Archetypes.Archetypes)
        {
            string card = ArchetypeCard.Render(s_data, def.Code);

            Assert.StartsWith("# " + Lexicon.Archetype(def.Id), card, StringComparison.Ordinal);
            Assert.Contains("## 허용 액션", card, StringComparison.Ordinal);
            Assert.Contains("## 걸릴 수 있는 인터럽트", card, StringComparison.Ordinal);
            Assert.Contains("## 폴백 하루", card, StringComparison.Ordinal);
            Assert.Contains("## 버킷", card, StringComparison.Ordinal);

            // 폴백을 못 찾으면 카드가 그 사실을 적는다. 적었다면 V7 위반이거나 로더가 안 읽은 것이다.
            Assert.DoesNotContain("을 찾지 못했다", card, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// <b>같은 입력이면 바이트 동일이다.</b> 시각도 난수도 섞지 않았다는 것을 이렇게 확인한다 —
    /// 섞이면 Studio·MCP·CLI 가 같은 정의에 다른 카드를 내고, 골든 회귀를 걸 수 없다.
    /// </summary>
    [Fact]
    public void Card_IsDeterministic()
    {
        foreach (ArchetypeDef def in s_data.Archetypes.Archetypes)
        {
            Assert.Equal(ArchetypeCard.Render(s_data, def.Code), ArchetypeCard.Render(s_data, def.Code));
        }

        Assert.Equal(InterruptExplain.Render(s_data), InterruptExplain.Render(s_data));
    }

    /// <summary>다른 마스터데이터를 읽어도 카드가 같으면 카드가 데이터를 안 읽고 있다는 뜻이다.</summary>
    [Fact]
    public void Card_DiffersBetweenArchetypes()
    {
        var cards = new HashSet<string>(StringComparer.Ordinal);

        foreach (ArchetypeDef def in s_data.Archetypes.Archetypes)
        {
            Assert.True(
                cards.Add(ArchetypeCard.Render(s_data, def.Code)),
                $"{def.Id} 의 카드가 다른 아키타입과 같다.");
        }
    }

    [Fact]
    public void Card_RejectsUnknownArchetype()
    {
        Assert.Throws<ArgumentException>(() => ArchetypeCard.Render(s_data, "no_such_archetype"));
    }

    /// <summary>
    /// <b>F-03 의 핵심 단언.</b> 폴백 40개 × 전 버킷에 대해 카드의 판정과 3단 검증기의 판정이
    /// 같은가 — 같은 스텝, 같은 코드인가.
    /// </summary>
    [Fact]
    public void PlanExplain_AgreesWithCoherenceValidator()
    {
        Assert.NotNull(s_data.Fallbacks);

        int compared = 0;
        int disagreedOnlyByUntraced = 0;

        foreach (FallbackPlanEntry entry in s_data.Fallbacks!.Plans)
        {
            for (int slot = 0; slot < BucketKey.PerArchetype; slot++)
            {
                BucketKey bucket = BucketKey.FromIndex((entry.Archetype.Value * BucketKey.PerArchetype) + slot);

                ValidationResult expected =
                    CoherenceValidator.Validate(entry.Document, bucket, entry.Archetype, s_data);

                (int step, string code) = PlanExplain.FirstFailure(
                    s_data, entry.Plan, bucket, entry.Archetype);

                compared++;

                if (expected.IsValid)
                {
                    Assert.True(
                        code.Length == 0,
                        $"{entry.Id}@{bucket}: 검증기는 통과인데 카드가 {code} 를 스텝 {step} 에 그렸다.");
                    continue;
                }

                if (NotTraced.Contains(expected.Code))
                {
                    // 카드가 스텝 표에 그리지 않는 코드다. 대신 다른 실패를 지어내지는 않아야 한다.
                    Assert.True(
                        code.Length == 0,
                        $"{entry.Id}@{bucket}: 검증기는 {expected.Code} 인데 카드가 {code} 를 그렸다.");

                    disagreedOnlyByUntraced++;
                    continue;
                }

                Assert.Equal(expected.Code, code);
                Assert.Equal(expected.StepIndex, step);
            }
        }

        Assert.True(compared >= s_data.Archetypes.Count * BucketKey.PerArchetype);

        // 예외 목록이 실제로 얼마나 쓰였는지 남긴다 — 0 이면 목록을 지울 때가 된 것이다.
        Assert.InRange(disagreedOnlyByUntraced, 0, compared);
    }

    /// <summary>
    /// 카드의 ✗ 표시와 <see cref="PlanExplain.FirstFailure"/> 가 같은 것을 본다.
    /// 표와 판정이 갈리면 둘 중 무엇도 믿을 수 없다.
    /// </summary>
    [Fact]
    public void PlanExplain_TableMatchesFirstFailure()
    {
        foreach (FallbackPlanEntry entry in s_data.Fallbacks!.Plans)
        {
            for (int slot = 0; slot < BucketKey.PerArchetype; slot++)
            {
                BucketKey bucket = BucketKey.FromIndex((entry.Archetype.Value * BucketKey.PerArchetype) + slot);

                string table = PlanExplain.Steps(s_data, entry.Plan, bucket, entry.Archetype);
                (int step, string code) = PlanExplain.FirstFailure(s_data, entry.Plan, bucket, entry.Archetype);

                if (code.Length == 0 || step < 0)
                {
                    continue;
                }

                Assert.Contains("✗ `" + code + "`", table, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void PlanExplain_RendersWholePlan()
    {
        FallbackPlanEntry entry = s_data.Fallbacks!.Plans[0];
        string text = PlanExplain.Render(s_data, entry.Plan);

        Assert.Contains("## 인벤토리 수지", text, StringComparison.Ordinal);
        Assert.Contains("## 예상 소요", text, StringComparison.Ordinal);
        Assert.Equal(text, PlanExplain.Render(s_data, entry.Plan));
    }

    [Fact]
    public void Interrupts_ListEveryRuleInMatchOrder()
    {
        ImmutableArray<InterruptRule> ordered = InterruptExplain.Ordered(s_data);

        Assert.Equal(s_data.Interrupts.Count, ordered.Length);

        for (int i = 1; i < ordered.Length; i++)
        {
            Assert.True(
                ordered[i - 1].Priority > ordered[i].Priority
                || (ordered[i - 1].Priority == ordered[i].Priority
                    && string.CompareOrdinal(ordered[i - 1].Id, ordered[i].Id) < 0),
                $"{ordered[i - 1].Id} 다음에 {ordered[i].Id} 가 왔다 — 매칭 순서와 다르다.");
        }

        string text = InterruptExplain.Render(s_data);

        foreach (InterruptRule rule in ordered)
        {
            Assert.Contains("`" + rule.Id + "`", text, StringComparison.Ordinal);
        }

        // 충돌 절은 항상 있다 — 없으면 "확인했는데 없었다" 와 "확인하지 않았다" 를 구별할 수 없다.
        Assert.Contains("## 우선순위 충돌", text, StringComparison.Ordinal);
    }

    /// <summary>인터럽트 문장에 비트마스크 숫자가 새면 사람이 못 읽는다.</summary>
    [Fact]
    public void Interrupts_NeverPrintRawBitmasks()
    {
        string text = InterruptExplain.Render(s_data);

        Assert.DoesNotContain("WorldFlags)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("0x", text, StringComparison.Ordinal);
    }

    [Fact]
    public void InstanceCard_RendersFromTheInstanceTable()
    {
        NpcInstanceTable instances = NpcInstanceTable.Load(
            Path.Combine(TestPaths.MasterData, "npc_instances.json"), s_data);

        string card = InstanceCard.Render(s_data, instances, 0);

        Assert.StartsWith("# NPC ", card, StringComparison.Ordinal);
        Assert.Contains("집 ↔ 일터", card, StringComparison.Ordinal);
        Assert.Equal(card, InstanceCard.Render(s_data, instances, 0));

        // 마지막 아키타입의 명부. code 가 뒤쪽이면 --npcs 를 작게 잡을 때 한 마리도 안 뜬다.
        var last = new ArchetypeId((ushort)(s_data.Archetypes.Count - 1));
        string roster = InstanceCard.RenderRoster(s_data, instances, last);

        Assert.Contains("인스턴스", roster, StringComparison.Ordinal);
    }

    [Fact]
    public void InstanceCard_RejectsOutOfRange()
    {
        NpcInstanceTable instances = NpcInstanceTable.Load(
            Path.Combine(TestPaths.MasterData, "npc_instances.json"), s_data);

        Assert.Throws<ArgumentOutOfRangeException>(() => InstanceCard.Render(s_data, instances, -1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => InstanceCard.Render(s_data, instances, instances.Count));
    }

    /// <summary>표 셀에 <c>|</c> 가 그대로 들어가면 markdown 표가 깨진다.</summary>
    [Fact]
    public void Md_EscapesPipesInCells()
    {
        Assert.Equal("a\\|b", Md.Cell("a|b"));
        Assert.Equal("—", Md.Cell(" "));
        Assert.Equal("15분", Md.Seconds(900));
        Assert.Equal("2시간", Md.Seconds(7200));
        Assert.Equal("1시간 30분", Md.Seconds(5400));
        Assert.Equal("30초", Md.Seconds(30));
        Assert.Equal("—", Md.Seconds(0));
    }

    /// <summary>
    /// 플래그 이름은 <b>영어 그대로</b> 둔다. 마스터데이터의 id 이고 검증 오류 메시지·
    /// <c>/metrics</c>·프롬프트가 전부 이 이름을 쓴다 — 카드에서만 번역하면 대조가 안 된다.
    /// </summary>
    [Fact]
    public void Card_KeepsFlagNamesInEnglish()
    {
        string card = ArchetypeCard.Render(s_data, s_data.Archetypes.Archetypes[0].Code);

        Assert.Contains("`AtWorkplace", card, StringComparison.Ordinal);
    }
}
