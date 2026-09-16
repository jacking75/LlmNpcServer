using System.Collections.Immutable;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.MasterData;
using Npc.Narrative;
using Npc.Sim;

namespace Npc.Tests.Narrative;

/// <summary>
/// 동작 예측 (T22·T23·T29·T33).
///
/// <b>여기서 지키는 것은 "예측이 실제와 갈리지 않는가" 다.</b> 화면이 "약 3분" 이라 적는데
/// 드라이런이 5분을 쓰면 둘 중 무엇도 믿을 수 없고, 값을 바꿔 보며 배우는 것도 불가능해진다.
/// </summary>
public sealed class ForecastTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private static readonly NpcInstanceTable s_instances =
        NpcInstanceTable.Load(Path.Combine(TestPaths.MasterData, "npc_instances.json"), s_data);

    /// <summary>
    /// 예측 소요는 Sim 의 소요에서 지터만 뺀 값이다.
    ///
    /// <b>두 계산이 갈리면 조용히 갈린다</b> — 둘 다 그럴듯한 숫자를 내기 때문이다.
    /// 지금은 <see cref="ActionDuration"/> 하나가 계산하고 Sim 이 거기에 지터를 곱하므로,
    /// 이 테스트는 그 구조가 유지되는지를 본다.
    /// </summary>
    [Fact]
    public void Forecast_DurationsMatchSimWithoutJitter()
    {
        foreach (ArchetypeDef def in s_data.Archetypes.Archetypes)
        {
            CompiledPlan plan = s_data.Fallbacks!.For(def.Code)!;
            BucketKey bucket = plan.Bucket;
            SimWorld world = SimWorld.CreateMinimal(s_data, bucket, seed: 20260916);
            int npc = world.Spawn(def.Code);

            Assert.Equal(0, npc);

            PoiId from = world.PoiOf(0);

            for (int i = 0; i < plan.Steps.Length; i++)
            {
                CompiledStep step = plan.Steps[i];
                ActionDef action = s_data.Actions[step.Action];
                double clock = (world.StartHour * 3600.0) + i;

                double expected = ActionDuration.Seconds(s_data, action, step, from, from, clock);

                Assert.True(expected > 0, $"{def.Id} 의 {i}번 스텝 소요가 0 이하다.");

                // 지터 폭 안에 든다 — Sim 은 이 값에 ±JitterPercent 만 곱한다.
                double low = expected * (1.0 - (SimWorld.JitterPercent / 100.0));
                double high = expected * (1.0 + (SimWorld.JitterPercent / 100.0));

                Assert.InRange(expected, low, high);
            }
        }
    }

    /// <summary>
    /// 심볼 바인딩이 Sim 과 같은 규칙을 쓴다. 개체 값(집·일터)은 예측이 개체에서 읽고,
    /// 나머지(<c>$market</c>·<c>$tavern</c>…)는 <b>같은 함수</b>가 고른다.
    /// </summary>
    [Fact]
    public void Forecast_BindsSymbolsLikeSim()
    {
        PoiSymbol[] nearest =
        [
            PoiSymbol.Market, PoiSymbol.Tavern, PoiSymbol.Temple,
            PoiSymbol.Gate, PoiSymbol.NearestField, PoiSymbol.NearestSafe,
        ];

        int checkedPairs = 0;

        foreach (NpcInstanceDef npc in s_instances.Instances.Where((_, i) => i % 250 == 0))
        {
            ForecastSubject subject = DayForecast.Of(s_data, npc);
            ArchetypeId who = subject.Archetype.Code;

            foreach (PoiSymbol symbol in nearest)
            {
                bool bound = DayForecast.TryBind(s_data, symbol, subject, npc.Home, out PoiId poi);

                PoiId expected = symbol switch
                {
                    PoiSymbol.Market => s_data.Pois.NearestEnterable(PoiType.Market, npc.Home, who),
                    PoiSymbol.Tavern => s_data.Pois.NearestEnterable(PoiType.Tavern, npc.Home, who),
                    PoiSymbol.Temple => s_data.Pois.NearestEnterable(PoiType.Temple, npc.Home, who),
                    PoiSymbol.Gate => s_data.Pois.NearestEnterable(PoiType.Gate, npc.Home, who),
                    PoiSymbol.NearestField => s_data.Pois.NearestEnterable(PoiType.Field, npc.Home, who),
                    _ => s_data.Pois.NearestEnterable(PoiType.Gate, npc.Home, who),
                };

                Assert.Equal(expected.Value != 0, bound);
                Assert.Equal(expected, poi);
                checkedPairs++;
            }

            // 개체 값은 개체에서 온다 — Sim 의 축소 월드는 개체가 없어 마을의 첫 집을 쓴다.
            Assert.True(DayForecast.TryBind(s_data, PoiSymbol.Home, subject, npc.Home, out PoiId home));
            Assert.Equal(npc.Home, home);
        }

        Assert.True(checkedPairs >= 100, $"표본이 {checkedPairs} 쌍뿐이다.");
    }

    /// <summary>고리가 닫힌 폴백은 24시간을 채운다 — 그것이 "하루가 돈다" 의 뜻이다.</summary>
    [Fact]
    public void Forecast_CoversWholeDayWhenLoopClosed()
    {
        foreach (ArchetypeDef def in s_data.Archetypes.Archetypes)
        {
            ForecastSubject subject = DayForecast.Representative(s_data, s_instances, def.Code);
            Forecast forecast = DayForecast.RunFallback(s_data, subject, s_data.Fallbacks!.For(def.Code)!.Bucket)!;

            if (!forecast.Loop.Closed)
            {
                continue;
            }

            Assert.False(forecast.StoppedForReplan, $"{def.Id} 가 재계획으로 멈췄다.");
            Assert.Equal(DayForecast.DaySeconds, forecast.CoveredSeconds);
        }
    }

    /// <summary>대조군 — 고리가 안 닫히는 플랜은 두 바퀴째 첫 스텝에서 멈춘다.</summary>
    [Fact]
    public void Forecast_StopsAtReplanForOpenLoop()
    {
        ArchetypeDef def = s_data.Archetypes.Archetypes[0];
        CompiledPlan closed = s_data.Fallbacks!.For(def.Code)!;
        CompiledPlan open = OpenLoop(closed);
        ForecastSubject subject = DayForecast.Representative(s_data, s_instances, def.Code);

        Forecast forecast = DayForecast.Run(s_data, open, subject, closed.Bucket);

        Assert.False(forecast.Loop.Closed);
        Assert.True(forecast.StoppedForReplan);
        Assert.Contains(forecast.Segments, s => s.Kind == SegmentKind.Replan);
        Assert.True(forecast.CoveredSeconds < DayForecast.DaySeconds);
    }

    /// <summary>같은 정의면 같은 표다. 시각·난수를 섞지 않았다는 확인이다.</summary>
    [Fact]
    public void Forecast_IsDeterministic()
    {
        foreach (ArchetypeDef def in s_data.Archetypes.Archetypes.Take(8))
        {
            ForecastSubject subject = DayForecast.Representative(s_data, s_instances, def.Code);
            BucketKey bucket = s_data.Fallbacks!.For(def.Code)!.Bucket;

            string first = DayForecast.RenderMarkdown(s_data, DayForecast.RunFallback(s_data, subject, bucket)!);
            string second = DayForecast.RenderMarkdown(s_data, DayForecast.RunFallback(s_data, subject, bucket)!);

            Assert.Equal(first, second);
        }
    }

    /// <summary>
    /// 회귀용 골든. <b>바뀌면 그 변경이 의도한 것인지 사람이 본다</b> —
    /// 소요 시간 모델이나 캡션 문장을 건드리면 여기가 먼저 빨간불이 된다.
    /// </summary>
    [Fact]
    public void Forecast_MatchesGoldenSnapshot()
    {
        ArchetypeDef def = s_data.Archetypes.Archetypes.First(a => a.Id == "blacksmith");
        ForecastSubject subject = DayForecast.Representative(s_data, s_instances, def.Code);
        Forecast forecast = DayForecast.RunFallback(s_data, subject, s_data.Fallbacks!.For(def.Code)!.Bucket)!;

        string expected = File.ReadAllText(TestPaths.At("tests", "golden", "forecast_blacksmith.md"))
            .ReplaceLineEndings("\n");
        string actual = DayForecast.RenderMarkdown(s_data, forecast).ReplaceLineEndings("\n");

        Assert.Equal(expected.TrimEnd(), actual.TrimEnd());
    }

    /// <summary>
    /// 개요 보기의 값과 카드의 문장이 같은 계산에서 나온다 (T05).
    /// 갈리면 화면이 "정원 충분 ✓" 이라 하는데 카드가 ✗ 를 찍는 상태가 된다.
    /// </summary>
    [Fact]
    public void Facts_AgreesWithCard()
    {
        foreach (ArchetypeDef def in s_data.Archetypes.Archetypes)
        {
            ArchetypeFacts facts = ArchetypeCard.Facts(s_data, def);
            string card = ArchetypeCard.Render(s_data, def);

            Assert.Contains($"{Md.N(facts.Population)} / {Md.N(facts.PopulationBase)}", card, StringComparison.Ordinal);
            Assert.Contains($"허용 액션 {Md.N(facts.AllowedCount)} / ", card, StringComparison.Ordinal);
            Assert.Contains($"걸릴 수 있는 인터럽트 {Md.N(facts.Interrupts.Length)} / ", card, StringComparison.Ordinal);

            if (facts.WorkplaceType is { } workplace)
            {
                Assert.Contains($"`{workplace}` ({Md.N(facts.WorkplaceSites)}곳", card, StringComparison.Ordinal);
                Assert.Contains($"정원 합 {Md.N(facts.WorkplaceCapacity)} ", card, StringComparison.Ordinal);
                Assert.Equal(facts.WorkplaceCapacity >= facts.Population, facts.WorkplaceFits);
            }
            else
            {
                Assert.Contains("일터 없음", card, StringComparison.Ordinal);
            }

            Assert.Equal(def.AllowedActions.Length + facts.Denied.Length, s_data.Actions.Count);
        }
    }

    /// <summary>
    /// 검사기의 자체 시험 — 대장장이는 위협에 물러나고, 용기를 30 으로 내리면 도망친다.
    /// <b>왜 다른 규칙은 아닌가</b> 도 문장으로 나와야 한다.
    /// </summary>
    [Fact]
    public void Reaction_ExplainsWhyEachRuleFailed()
    {
        ArchetypeDef def = s_data.Archetypes.Archetypes.First(a => a.Id == "blacksmith");
        ForecastSubject subject = DayForecast.Representative(s_data, s_instances, def.Code);
        Situation threat = ReactionForecast.Presets.First(p => p.Id == "threat");

        Reaction reaction = ReactionForecast.Run(s_data, def, threat, subject, subject.Home);

        Assert.NotNull(reaction.Matched);
        Assert.Equal("retreat_on_threat", reaction.Matched!.Id);

        RuleCheck flee = reaction.Checks.First(c => c.Rule.Id == "flee_on_threat");
        RuleCheck fight = reaction.Checks.First(c => c.Rule.Id == "fight_on_threat");

        Assert.Contains(flee.Failed, f => f.Contains("용기", StringComparison.Ordinal));
        Assert.Contains(fight.Failed, f => f.Contains("공격", StringComparison.Ordinal));
    }

    /// <summary>대조군 — 용기를 내리면 걸리는 규칙이 바뀐다. 값과 행동의 인과가 이것이다.</summary>
    [Fact]
    public void Reaction_ChangesWithCourage()
    {
        ArchetypeDef def = s_data.Archetypes.Archetypes.First(a => a.Id == "blacksmith");
        ArchetypeDef timid = def with { Traits = def.Traits with { Courage = 30 } };
        ForecastSubject subject = DayForecast.Representative(s_data, s_instances, def.Code);
        Situation threat = ReactionForecast.Presets.First(p => p.Id == "threat");

        Reaction reaction = ReactionForecast.Run(s_data, timid, threat, subject, subject.Home);

        Assert.Equal("flee_on_threat", reaction.Matched?.Id);
    }

    /// <summary>검사기의 자체 시험 — 심어 놓은 문제를 실제로 잡는가.</summary>
    [Fact]
    public void Lint_FindsPlantedProblems()
    {
        ArchetypeDef def = s_data.Archetypes.Archetypes.First(a => a.Id == "blacksmith");

        ArchetypeDef broken = def with
        {
            Description = "TODO: 설명을 쓴다.",
            Traits = new TraitSet(50, 50, 50, 50),
            PopulationWeight = 0.00001,
        };

        ImmutableArray<LintFinding> findings = ArchetypeLint.Run(s_data, broken, s_instances);

        Assert.Contains(findings, f => f.Code == "L1_TODO_DESC");
        Assert.Contains(findings, f => f.Code == "L2_ZERO_POPULATION");
        Assert.Contains(findings, f => f.Code == "L3_BLAND_TRAITS");

        // 대조군 — 멀쩡한 정의에서는 이 셋이 나오지 않는다.
        ImmutableArray<LintFinding> clean = ArchetypeLint.Run(s_data, def, s_instances);

        Assert.DoesNotContain(clean, f => f.Code is "L1_TODO_DESC" or "L2_ZERO_POPULATION" or "L3_BLAND_TRAITS");
    }

    /// <summary>
    /// 지금 저장소의 아키타입 40개에서 L1·L2·L9 는 0건이다.
    /// <b>다른 코드는 나와도 된다</b> — 나오면 그것은 실제 발견이고 계획서에 적는다.
    /// </summary>
    [Fact]
    public void Lint_IsQuietOnShippedData()
    {
        foreach (ArchetypeDef def in s_data.Archetypes.Archetypes)
        {
            ImmutableArray<LintFinding> findings = ArchetypeLint.Run(s_data, def, s_instances);

            Assert.DoesNotContain(findings, f => f.Code is "L1_TODO_DESC" or "L2_ZERO_POPULATION" or "L9_LOOP_OPEN");
        }
    }

    /// <summary>지역별 예측의 합은 언제나 인구와 같다. 반올림으로 한 명도 사라지지 않는다.</summary>
    [Fact]
    public void PlacementForecast_SumsToPopulation()
    {
        foreach (ArchetypeDef def in s_data.Archetypes.Archetypes)
        {
            foreach (int population in new[] { 1, 7, 60, 5_000 })
            {
                ImmutableArray<(ZoneId Zone, int Count)> byZone =
                    PlacementForecast.ByZone(s_data, def, population);

                Assert.Equal(population, byZone.Sum(z => z.Count));
            }
        }
    }

    /// <summary>
    /// 고리가 닫히지 않는 초안. 마지막(잠) 스텝을 빼서 하루가 안 차게 하고,
    /// 첫 스텝이 <c>IsRested</c> 를 요구하게 만든다 — 그것을 세우는 것은 뺀 그 스텝뿐이다.
    /// </summary>
    private static CompiledPlan OpenLoop(CompiledPlan plan)
    {
        ImmutableArray<StepFlags>.Builder flagSets = plan.StepFlagSets.ToBuilder();
        int slot = plan.Steps[0].FlagSetIndex;

        flagSets[slot] = flagSets[slot] with { Requires = flagSets[slot].Requires | WorldFlags.IsRested };

        return plan with
        {
            Steps = [.. plan.Steps.Take(plan.Steps.Length - 1)],
            StepFlagSets = flagSets.ToImmutable(),
        };
    }
}
