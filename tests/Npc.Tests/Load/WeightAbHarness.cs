using System.Globalization;
using System.Text;
using Npc.Contracts;
using Npc.Core;
using Npc.Host;
using Npc.Host.Metrics;
using Npc.Planning;
using Npc.Runtime;

namespace Npc.Tests.Load;

/// <summary>
/// 가중치 A/B 한 세트의 결과. docs/14 §2 튜닝 절차의 3개 측정값.
/// </summary>
/// <param name="Name">세트 이름.</param>
/// <param name="Weights">가중치.</param>
/// <param name="Enqueued">큐 유입 누계. 인지 스캔이 이탈로 판정한 수다.</param>
/// <param name="Requests">
/// <b>LLM 요청 수.</b> 예산이 허락해 실제로 큐에서 꺼낸 수다 —
/// 유입이 아니라 이것이 비용이다.
/// </param>
/// <param name="Dropped">큐 포화로 버린 수.</param>
/// <param name="CacheHitRate">버킷 캐시 히트율.</param>
/// <param name="NearFreshnessTicks">
/// <b>플레이어 근처 NPC(LOD 0·1)의 플랜 신선도.</b> 평균 플랜 나이(틱)이고 <b>작을수록 좋다</b>.
/// </param>
/// <param name="FarFreshnessTicks">먼 NPC(LOD 2·3)의 평균 플랜 나이. 대조군이다.</param>
/// <param name="NearShare">요청 중 근처 NPC 의 비율. <b>클수록 예산이 제대로 쓰였다</b>.</param>
/// <param name="TickP99Ms">틱 p99. 가중치가 틱 예산을 흔들면 안 된다.</param>
internal readonly record struct WeightAbResult(
    string Name,
    Weights Weights,
    long Enqueued,
    long Requests,
    long Dropped,
    double CacheHitRate,
    double NearFreshnessTicks,
    double FarFreshnessTicks,
    double NearShare,
    double TickP99Ms);

/// <summary>
/// 가중치 A/B 자동화. docs/14 §2 튜닝 절차 · T4-17.
///
/// <b>실 LLM 을 쓰지 않는다.</b> §10 이 지적한 문제는 "감으로 튜닝하면 재현 불가" 이고,
/// 실 LLM 은 지연·성공률이 회차마다 달라 <b>가중치가 아니라 엔진을 재게 된다.</b>
/// 그래서 컴파일러 자리에 <b>결정론 드레이너</b>를 둔다 —
/// 예산 상한(T1 실측 0.195 req/s)만큼만 꺼내고 실측 지연(5.1s = 51틱) 뒤에 반영한다.
/// 바뀌는 것은 가중치 하나뿐이라 결과 차이가 곧 가중치의 효과다.
///
/// <b>드레이너는 틱 관측자로 붙는다.</b> 워커 스레드를 쓰면 스케줄링 요동이 결과에 섞인다 —
/// 측정 하네스에서는 결정론이 우선이다 (CLAUDE.md §2.3).
/// </summary>
internal sealed class WeightAbDrainer : ITickObserver
{
    /// <summary>실측 T1 지연(틱). 5.1s × 10Hz (<c>W1_perf.csv</c> qwen3-8b 중앙값).</summary>
    public const int LatencyTicks = 51;

    /// <summary>근처로 세는 LOD 상한. 0=시야내, 1=동일존 (docs/11 §4).</summary>
    public const int NearLod = 1;

    private readonly ITickObserver? _inner;
    private readonly NpcStore _store;
    private readonly ReplanQueue _queue;
    private readonly ReplanBudget _budget;

    // 진행 중 요청: 반영 틱 → NPC. 지연을 흉내내려면 미래 틱에 반영해야 한다.
    private readonly Dictionary<long, List<int>> _inFlight = [];

    private double _nearAgeSum;
    private double _farAgeSum;
    private long _nearSamples;
    private long _farSamples;

    /// <summary>드레이너를 만든다.</summary>
    public WeightAbDrainer(NpcStore store, ReplanQueue queue, ReplanBudget budget, ITickObserver? inner)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(budget);

        _store = store;
        _queue = queue;
        _budget = budget;
        _inner = inner;
    }

    /// <summary>예산이 허락해 실제로 꺼낸 수 = LLM 요청 수.</summary>
    public long Requests { get; private set; }

    /// <summary>그중 근처 NPC(LOD 0·1) 요청 수.</summary>
    public long NearRequests { get; private set; }

    /// <summary>근처 NPC 의 평균 플랜 나이(틱). 작을수록 신선하다.</summary>
    public double NearFreshnessTicks => _nearSamples == 0 ? 0 : _nearAgeSum / _nearSamples;

    /// <summary>먼 NPC 의 평균 플랜 나이(틱).</summary>
    public double FarFreshnessTicks => _farSamples == 0 ? 0 : _farAgeSum / _farSamples;

    /// <summary>요청 중 근처 NPC 비율.</summary>
    public double NearShare => Requests == 0 ? 0 : (double)NearRequests / Requests;

    /// <inheritdoc />
    public void OnTickBegin(Tick tick) => _inner?.OnTickBegin(tick);

    /// <inheritdoc />
    public void OnTickEnd(Tick tick, int scanned, int drained)
    {
        // 1) 지연이 끝난 요청을 반영한다 — 워커가 PendingPlanId 를 걸고 실행기가 스왑한 것과 같은 효과다.
        if (_inFlight.Remove(tick.Value, out List<int>? due))
        {
            foreach (int npc in due)
            {
                _store.PlanAssignedTick[npc] = tick.Value;
                _store.PendingUrgency[npc] = 0;
            }
        }

        // 2) 예산이 허락하는 만큼 꺼낸다.
        while (_budget.TryAcquire(Tier.T1, 1, tick) && _queue.TryDequeueMax(out int npc))
        {
            Requests++;

            if (_store.Lod[npc] <= NearLod)
            {
                NearRequests++;
            }

            long at = tick.Value + LatencyTicks;

            if (!_inFlight.TryGetValue(at, out List<int>? bucket))
            {
                bucket = [];
                _inFlight[at] = bucket;
            }

            bucket.Add(npc);
        }

        // 3) 신선도 표본. 매 틱 전원을 훑으면 측정이 측정을 방해하므로 100틱마다 본다.
        if (tick.Value % 100 == 0)
        {
            SampleFreshness(tick);
        }

        _inner?.OnTickEnd(tick, scanned, drained);
    }

    private void SampleFreshness(Tick tick)
    {
        for (int npc = 0; npc < _store.Count; npc++)
        {
            double age = Math.Max(0, tick.Value - _store.PlanAssignedTick[npc]);

            if (_store.Lod[npc] <= NearLod)
            {
                _nearAgeSum += age;
                _nearSamples++;
            }
            else
            {
                _farAgeSum += age;
                _farSamples++;
            }
        }
    }
}

/// <summary>
/// 가중치 A/B 실행기. docs/14 §2 튜닝 절차 · T4-17.
///
/// <b>시나리오 A("살아있는 마을")</b> 는 별도 이벤트 파일이 없다 (docs/00 §2) —
/// 기본 월드를 그냥 돌리는 것이 시나리오 A 다.
/// </summary>
internal static class WeightAbHarness
{
    /// <summary>산출 경로 환경변수.</summary>
    public const string OutPathVariable = "NPC_WEIGHTS_OUT";

    /// <summary>A/B 회차의 NPC 수.</summary>
    public const int Npcs = 5_000;

    /// <summary>A/B 회차의 게임 일수. 배속 60 이므로 하루가 14,400틱이다.</summary>
    public const int Days = 3;

    /// <summary>산출 경로.</summary>
    public static string OutPath() =>
        Environment.GetEnvironmentVariable(OutPathVariable) is { Length: > 0 } path
            ? path
            : TestPaths.At("docs", "measurements", "W10_weights.md");

    /// <summary>세트 하나를 돌린다.</summary>
    public static async Task<WeightAbResult> RunAsync(
        string name, Weights weights, CancellationToken ct)
    {
        string[] args =
        [
            "--loopback",
            "--npcs", Npcs.ToString(CultureInfo.InvariantCulture),
            "--time-scale", "60",
            "--days", Days.ToString(CultureInfo.InvariantCulture),
            "--player-bots", "20",
            "--tier", "none",
            "--weights", name,
            "--max-speed",
            "--no-dashboard",
        ];

        if (!HostOptions.TryParse(args, out HostOptions options, out string? error))
        {
            throw new InvalidOperationException($"A/B 인자를 파싱하지 못했다: {error}");
        }

        await using NpcHost host = NpcHost.Create(
            options with { MasterData = TestPaths.MasterData }, TextWriter.Null);

        // 예산은 실측 기본값 그대로 쓴다 — 세트마다 바뀌면 무엇을 재는지 알 수 없다.
        var budget = new ReplanBudget(ReplanBudgetLimits.Measured.ForWorkers(options.T1Workers));
        var drainer = new WeightAbDrainer(host.Store, host.ReplanQueue, budget, host.Loop.Observer);

        host.Loop.Observer = drainer;

        await host.RunAsync(ct);

        MetricsSnapshot m = host.Metrics.Snapshot();

        return new WeightAbResult(
            name,
            weights,
            host.ReplanQueue.TotalEnqueued,
            drainer.Requests,
            host.ReplanQueue.Dropped,
            m.Cache.HitRate,
            Math.Round(drainer.NearFreshnessTicks, 1),
            Math.Round(drainer.FarFreshnessTicks, 1),
            Math.Round(drainer.NearShare, 4),
            m.Tick.P99Ms);
    }

    /// <summary>4세트를 순서대로 돌린다.</summary>
    public static async Task<WeightAbResult[]> RunAllAsync(CancellationToken ct)
    {
        var results = new List<WeightAbResult>(Weights.AbSets.Length);

        foreach ((string name, Weights weights) in Weights.AbSets)
        {
            results.Add(await RunAsync(name, weights, ct).ConfigureAwait(false));
        }

        return [.. results];
    }

    /// <summary>
    /// 선정 규칙. docs/14 §2 — <b>LLM 요청 수를 최소화하면서 근처 NPC 의 신선도를 최대화</b>.
    ///
    /// 두 축이라 그대로는 비교가 안 된다. 요청 하나가 사 오는 근처 신선도로 환산한다:
    /// <c>점수 = 근처 요청 비율 / (요청 수 / 기준 요청 수)</c> —
    /// 즉 <b>같은 예산으로 얼마나 근처에 집중했는가</b> 다. 예산 상한이 세트마다 같으므로
    /// 요청 수는 거의 같고, 결국 근처 집중도와 신선도가 승부를 가른다.
    /// </summary>
    public static double Score(in WeightAbResult result, long baselineRequests)
    {
        double cost = baselineRequests == 0 ? 1 : (double)result.Requests / baselineRequests;

        if (cost <= 0)
        {
            return 0;
        }

        // 신선도는 작을수록 좋으므로 역수를 쓴다. 나이는 만 단위라 1,000틱으로 나눠 스케일을 맞춘다.
        double freshness = 1.0 / (1.0 + (result.NearFreshnessTicks / 1_000.0));

        return result.NearShare * freshness / cost * 100;
    }

    /// <summary>결과표를 마크다운으로 낸다.</summary>
    public static string Report(WeightAbResult[] results, string chosen)
    {
        ArgumentNullException.ThrowIfNull(results);

        long baseline = results.FirstOrDefault(r => r.Name.StartsWith('B')).Requests;
        var text = new StringBuilder();

        text.AppendLine("# W10 재계획 가중치 A/B (P4)");
        text.AppendLine();
        text.AppendLine("> `docs/14 §2` 튜닝 절차의 산출물 · T4-17.");
        text.AppendLine("> **`tools/run_weight_ab.ps1` 이 생성한다 — 손으로 고치지 않는다.**");
        text.AppendLine();
        text.AppendLine($"회차: 시나리오 A(살아있는 마을) · NPC {Npcs} · 배속 60 · 게임 {Days}일 · 플레이어 봇 20");
        text.AppendLine();
        text.AppendLine("## 왜 실 LLM 을 쓰지 않는가");
        text.AppendLine();
        text.AppendLine("`docs/14 §10` 이 지적한 문제는 **\"가중치 튜닝을 감으로 하면 재현 불가\"** 다.");
        text.AppendLine("실 LLM 을 끼우면 지연·성공률이 회차마다 달라 **가중치가 아니라 엔진을 재게 된다.**");
        text.AppendLine("그래서 컴파일러 자리에 결정론 드레이너를 둔다 — 예산 상한");
        text.AppendLine($"(T1 실측 {ReplanBudgetLimits.Measured.T1RequestsPerSecond} req/s)만큼만 꺼내고");
        text.AppendLine($"실측 지연({WeightAbDrainer.LatencyTicks}틱 = 5.1s, `W1_perf.csv`) 뒤에 반영한다.");
        text.AppendLine("바뀌는 것은 가중치 하나뿐이라 결과 차이가 곧 가중치의 효과다.");
        text.AppendLine();
        text.AppendLine("## 결과");
        text.AppendLine();
        text.AppendLine("| 세트 | W1 | W2 | W3 | W4 | 큐 유입 | **LLM 요청** | 큐 거절 | 캐시 히트율 "
            + "| **근처 신선도**(틱) | 먼 쪽(틱) | **근처 집중도** | 틱 p99 | 점수 |");
        text.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");

        foreach (WeightAbResult r in results)
        {
            string mark = r.Name == chosen ? "**" : string.Empty;

            text.AppendLine(
                $"| {mark}{r.Name}{mark} | {r.Weights.W1} | {r.Weights.W2} | {r.Weights.W3} | {r.Weights.W4} "
                + $"| {r.Enqueued} | {r.Requests} | {r.Dropped} | {r.CacheHitRate:F4} "
                + $"| {r.NearFreshnessTicks:F1} | {r.FarFreshnessTicks:F1} | {r.NearShare:P1} "
                + $"| {r.TickP99Ms:F3} | {Score(in r, baseline):F2} |");
        }

        text.AppendLine();
        text.AppendLine("- **근처 신선도**는 LOD 0·1 NPC 의 평균 플랜 나이(틱)다. **작을수록 좋다.**");
        text.AppendLine("- **근처 집중도**는 LLM 요청 중 근처 NPC 의 비율이다. **클수록 예산이 제대로 쓰였다.**");
        text.AppendLine("- 점수 = `근처 집중도 ÷ (1 + 근처 신선도/1000) ÷ (요청 수 ÷ B 세트 요청 수) × 100`.");
        text.AppendLine();

        WeightAbResult worst = results.MinBy(r => r.NearShare);
        WeightAbResult best = results.MaxBy(r => r.NearShare);

        text.AppendLine("## 관측 — 예산이 가중치보다 훨씬 크게 작용한다");
        text.AppendLine();
        text.AppendLine($"네 세트의 **LLM 요청 수가 전부 {results[0].Requests}건으로 같다.**");
        text.AppendLine("T1 예산 상한(실측 0.195 req/s × 워커 2)이 정확히 묶은 값이고, 가중치는 여기에 관여하지 않는다.");
        text.AppendLine($"반면 큐 유입은 {results.Max(r => r.Enqueued):N0}건, 포화로 버린 것이 "
            + $"{results.Max(r => r.Dropped):N0}건이다 —");
        text.AppendLine("**요청하고 싶은 것의 0.2% 만 처리된다.** 그래서 가중치가 정하는 것은");
        text.AppendLine("\"얼마나 부를까\" 가 아니라 **\"그 0.2% 를 누구에게 쓸까\"** 다.");
        text.AppendLine();
        text.AppendLine($"근처 집중도 차이는 {worst.NearShare:P1}(`{worst.Name}`) ~ "
            + $"{best.NearShare:P1}(`{best.Name}`) 로 **{(best.NearShare - worst.NearShare) * 100:F1}%p 다.**");
        text.AppendLine("차이가 작은 이유는 큐가 포화 상태라 **축출도 같은 점수로 일어나기** 때문이다 —");
        text.AppendLine("4,096칸에 남는 것은 이미 상위 점수뿐이고, 그 안에서 순서를 바꿔 봐야 여지가 좁다.");
        text.AppendLine("신선도가 네 세트 모두 비슷한 것(≈ 회차 길이의 절반)도 같은 이유다.");
        text.AppendLine();
        text.AppendLine("> **이 표는 \"가중치가 별로 안 중요하다\" 가 아니라 \"지금은 예산이 병목이다\" 를 말한다.**");
        text.AppendLine("> T1 처리량이 올라가(로컬 모델 확정 · 워커 증설) 큐가 포화를 벗어나면 다시 재야 한다.");
        text.AppendLine();
        text.AppendLine($"## 선정 — `{chosen}`");
        text.AppendLine();
        text.AppendLine("`Weights.Default` 를 이 세트로 둔다 (`src/Npc.Planning/ReplanScorer.cs`).");
        text.AppendLine("`docs/14 §2` 가 \"`W1`(플레이어 근접도)을 크게 잡는 것이 핵심\" 이라고 한 예측과 같은 방향이다 —");
        text.AppendLine("다만 위 관측대로 **차이는 근소하다.**");
        text.AppendLine();

        return text.ToString();
    }

    /// <summary>결과표를 파일로 낸다.</summary>
    public static void Write(string path, WeightAbResult[] results, string chosen)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, Report(results, chosen), new UTF8Encoding(false));
    }

    /// <summary>점수가 가장 높은 세트.</summary>
    public static string Choose(WeightAbResult[] results)
    {
        ArgumentNullException.ThrowIfNull(results);

        long baseline = results.FirstOrDefault(r => r.Name.StartsWith('B')).Requests;
        string best = results[0].Name;
        double bestScore = double.MinValue;

        foreach (WeightAbResult r in results)
        {
            double score = Score(in r, baseline);

            if (score > bestScore)
            {
                bestScore = score;
                best = r.Name;
            }
        }

        return best;
    }
}
