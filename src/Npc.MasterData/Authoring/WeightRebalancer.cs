using System.Collections.Immutable;

namespace Npc.MasterData.Authoring;

/// <summary>가중치를 어디서 뗄 것인가.</summary>
public enum RebalanceStrategy
{
    /// <summary>인구가 가장 많은 아키타입 하나에서 전부 뗀다. 표가 가장 적게 흔들린다.</summary>
    Largest = 0,

    /// <summary>같은 일터 타입을 쓰는 아키타입들에서 비례로 뗀다. 직군의 총량이 유지된다.</summary>
    SameTrade = 1,

    /// <summary>전원에게서 비례로 뗀다. 상대 비율이 그대로다.</summary>
    Proportional = 2,
}

/// <summary>가중치 한 줄의 변화.</summary>
/// <param name="Archetype">아키타입 id.</param>
/// <param name="From">현재 가중치.</param>
/// <param name="To">제안 가중치.</param>
/// <param name="PopulationFrom">현재 인구.</param>
/// <param name="PopulationTo">제안 인구.</param>
public readonly record struct WeightChange(
    string Archetype, double From, double To, int PopulationFrom, int PopulationTo)
{
    /// <summary>인구 증감.</summary>
    public int Delta => PopulationTo - PopulationFrom;
}

/// <summary>재배분 제안 하나.</summary>
/// <param name="Strategy">어떤 방식인가.</param>
/// <param name="Changes">바뀌는 줄 (아키타입 code 오름차순). 새 아키타입도 포함한다.</param>
/// <param name="Sum">제안 적용 후 가중치 합. V5 가 1.0 ±0.001 을 요구한다.</param>
public readonly record struct RebalanceProposal(
    RebalanceStrategy Strategy, ImmutableArray<WeightChange> Changes, double Sum)
{
    /// <summary>V5 를 만족하는가.</summary>
    public bool IsValid => Math.Abs(Sum - 1.0) <= 0.001;
}

/// <summary>
/// 새 아키타입의 <c>population_weight</c> 를 넣을 때 <b>어디서 뗄지</b> 제안한다 (F-04).
///
/// <b>고르는 것은 사람이다.</b> 인구 분포는 세계관 결정이지 계산 결과가 아니다 —
/// 도구가 자동으로 정하면 아무도 그 숫자의 근거를 모르게 된다. 여기가 하는 일은
/// 세 가지 안과 <b>그 결과 인구표</b>를 보여 주는 것까지다.
///
/// <para>
/// V5 는 합이 1.0 ±0.001 이어야 한다. 부동소수 반올림으로 마지막 자리가 어긋나는 것을
/// 막기 위해 <b>가장 많이 떼는 줄에서 잔차를 흡수</b>한다.
/// </para>
/// </summary>
public static class WeightRebalancer
{
    /// <summary>가중치 소수 자릿수. 파일에 적히는 형식과 같아야 표와 파일이 어긋나지 않는다.</summary>
    public const int Digits = 4;

    /// <summary>
    /// 쓸 수 있는 안을 전부 만든다. 순서는 <see cref="RebalanceStrategy"/> ordinal 이다.
    ///
    /// <b>쓸 수 없는 안은 조용히 뺀다.</b> 아직 없는 아키타입은 일터 타입을 모르므로
    /// <see cref="RebalanceStrategy.SameTrade"/> 를 만들 수 없다 —
    /// <paramref name="workplacePoiType"/> 를 주면 그때도 만든다. 안이 하나도 없으면 던진다.
    /// </summary>
    /// <param name="data">현재 마스터데이터.</param>
    /// <param name="newArchetypeId">새 아키타입 id. 기존 id 면 그 줄의 가중치를 바꾸는 것으로 본다.</param>
    /// <param name="weight">새 아키타입에 줄 가중치.</param>
    /// <param name="population">인구표를 만들 기준 NPC 수.</param>
    /// <param name="workplacePoiType">새 아키타입의 일터 타입. 같은 계열 안이 이것을 본다.</param>
    public static ImmutableArray<RebalanceProposal> Propose(
        MasterDataSet data,
        string newArchetypeId,
        double weight,
        int population = 5_000,
        string? workplacePoiType = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrEmpty(newArchetypeId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(weight);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(weight, 1.0);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(population);

        var proposals = ImmutableArray.CreateBuilder<RebalanceProposal>(3);

        foreach (RebalanceStrategy strategy in Enum.GetValues<RebalanceStrategy>())
        {
            try
            {
                proposals.Add(Propose(data, newArchetypeId, weight, population, strategy, workplacePoiType));
            }
            catch (InvalidOperationException)
            {
                // 재원이 없거나 대상이 없는 안이다. 다른 안을 낸다.
            }
        }

        if (proposals.Count == 0)
        {
            throw new InvalidOperationException(
                $"'{newArchetypeId}' 에 {weight:F4} 를 줄 재원이 어느 방식으로도 없다.");
        }

        return proposals.ToImmutable();
    }

    /// <summary>한 가지 안만.</summary>
    /// <param name="data">현재 마스터데이터.</param>
    /// <param name="newArchetypeId">새 아키타입 id.</param>
    /// <param name="weight">새 아키타입에 줄 가중치.</param>
    /// <param name="population">기준 NPC 수.</param>
    /// <param name="strategy">방식.</param>
    /// <param name="workplacePoiType">새 아키타입의 일터 타입. 같은 계열 안이 이것을 본다.</param>
    public static RebalanceProposal Propose(
        MasterDataSet data,
        string newArchetypeId,
        double weight,
        int population,
        RebalanceStrategy strategy,
        string? workplacePoiType = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrEmpty(newArchetypeId);

        bool exists = data.Archetypes.TryGet(newArchetypeId, out ArchetypeDef existing);
        double have = exists ? existing.PopulationWeight : 0;
        double need = weight - have;

        if (need <= 0)
        {
            // 줄이는 방향이면 뗄 곳이 없다 — 남는 몫을 어디에 줄지는 다른 결정이다.
            throw new ArgumentException(
                $"'{newArchetypeId}' 의 가중치를 {have:F4} → {weight:F4} 로 줄이는 것은 재배분이 아니다. "
                + "남는 몫을 어디에 줄지는 사람이 정한다.",
                nameof(weight));
        }

        ImmutableArray<ArchetypeDef> donors = Donors(
            data,
            newArchetypeId,
            workplacePoiType ?? (exists ? existing.WorkplacePoiType : null),
            strategy);

        if (donors.IsEmpty)
        {
            throw new InvalidOperationException(
                $"{strategy} 방식으로 뗄 아키타입이 없다. 다른 안을 쓴다.");
        }

        double pool = donors.Sum(d => d.PopulationWeight);

        if (pool <= need)
        {
            throw new InvalidOperationException(
                $"{strategy} 방식의 재원이 {pool:F4} 뿐인데 {need:F4} 가 필요하다. 다른 안을 쓴다.");
        }

        var changes = ImmutableArray.CreateBuilder<WeightChange>(donors.Length + 1);
        double taken = 0;

        for (int i = 0; i < donors.Length; i++)
        {
            ArchetypeDef donor = donors[i];
            double share = donor.PopulationWeight / pool * need;
            double next = Round(donor.PopulationWeight - share);

            taken += donor.PopulationWeight - next;

            changes.Add(Change(donor.Id, donor.PopulationWeight, next, population));
        }

        // 반올림 잔차를 가장 크게 떼는 줄이 흡수한다. 안 하면 합이 1.0000 에서 미세하게 벗어난다.
        double residual = Round(need - taken);

        if (residual != 0)
        {
            int biggest = LargestChangeIndex(changes);
            WeightChange c = changes[biggest];
            double adjusted = Round(c.To - residual);

            changes[biggest] = Change(c.Archetype, c.From, adjusted, population);
        }

        changes.Add(Change(newArchetypeId, have, Round(weight), population));

        ImmutableArray<WeightChange> ordered =
        [
            .. changes.OrderBy(c => c.Archetype, StringComparer.Ordinal),
        ];

        return new RebalanceProposal(strategy, ordered, Sum(data, ordered));
    }

    /// <summary>제안을 적용한 뒤의 가중치 합.</summary>
    private static double Sum(MasterDataSet data, ImmutableArray<WeightChange> changes)
    {
        double sum = 0;

        foreach (ArchetypeDef def in data.Archetypes.Archetypes)
        {
            WeightChange? change = Find(changes, def.Id);

            sum += change?.To ?? def.PopulationWeight;
        }

        // 새로 추가되는 줄 (기존 표에 없다).
        foreach (WeightChange change in changes)
        {
            if (!data.Archetypes.TryGet(change.Archetype, out _))
            {
                sum += change.To;
            }
        }

        return Round(sum);
    }

    private static WeightChange? Find(ImmutableArray<WeightChange> changes, string id)
    {
        foreach (WeightChange change in changes)
        {
            if (string.Equals(change.Archetype, id, StringComparison.Ordinal))
            {
                return change;
            }
        }

        return null;
    }

    private static ImmutableArray<ArchetypeDef> Donors(
        MasterDataSet data,
        string newArchetypeId,
        string? trade,
        RebalanceStrategy strategy)
    {
        ImmutableArray<ArchetypeDef> others =
        [
            .. data.Archetypes.Archetypes.Where(
                a => !string.Equals(a.Id, newArchetypeId, StringComparison.Ordinal)),
        ];

        switch (strategy)
        {
            case RebalanceStrategy.Largest:
                ArchetypeDef? largest = others
                    .OrderByDescending(a => a.PopulationWeight)
                    .ThenBy(a => a.Code.Value)
                    .FirstOrDefault();

                return largest is null ? [] : [largest];

            case RebalanceStrategy.SameTrade:
                // 일터 타입을 모르면 이 안을 낼 수 없다. 호출부가 주면 새 아키타입도 된다.
                return trade is null
                    ? []
                    : [.. others.Where(a => string.Equals(a.WorkplacePoiType, trade, StringComparison.Ordinal))];

            default:
                return others;
        }
    }

    private static int LargestChangeIndex(ImmutableArray<WeightChange>.Builder changes)
    {
        int index = 0;
        double most = 0;

        for (int i = 0; i < changes.Count; i++)
        {
            double taken = changes[i].From - changes[i].To;

            if (taken > most)
            {
                most = taken;
                index = i;
            }
        }

        return index;
    }

    private static WeightChange Change(string id, double from, double to, int population) =>
        new(id, Round(from), Round(to), Population(from, population), Population(to, population));

    private static int Population(double weight, int population) =>
        (int)Math.Round(weight * population, MidpointRounding.AwayFromZero);

    private static double Round(double value) => Math.Round(value, Digits, MidpointRounding.AwayFromZero);
}
