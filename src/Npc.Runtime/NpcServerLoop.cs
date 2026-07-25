using Npc.Contracts;
using Npc.Planning;

namespace Npc.Runtime;

/// <summary>
/// 틱 관측자. 틱 소요 시간 측정은 <b>Npc.Runtime 밖에서</b> 한다.
/// 게임 로직이 벽시계를 보면 리플레이가 깨지므로(CLAUDE.md §2.3),
/// 시계가 필요한 계측은 호스트가 구현한다.
/// </summary>
public interface ITickObserver
{
    /// <summary>틱 처리 시작.</summary>
    void OnTickBegin(Tick tick);

    /// <summary>틱 처리 끝.</summary>
    /// <param name="tick">방금 끝난 틱.</param>
    /// <param name="scanned">이번 틱에 인지 판정한 NPC 수.</param>
    /// <param name="drained">이번 틱에 배수한 이벤트 수.</param>
    void OnTickEnd(Tick tick, int scanned, int drained);
}

/// <summary>
/// NPC 서버 틱 루프. docs/11 §5 · docs/02 §4.
///
/// <b>이 루프 안에 LLM 이 없다.</b> 재계획 워커는 별도 <c>BackgroundService</c> 이고,
/// 완성된 플랜을 <see cref="PlanSwapper.Request"/> 로 넣을 뿐이다.
///
/// <b>루프 안의 <c>await</c> 은 두 개뿐이다</b> — 이벤트 대기와 <c>FlushAsync</c>.
/// 그 밖의 어떤 비동기 호출도 틱을 밀어낸다 (CLAUDE.md §2.1).
/// </summary>
public sealed class NpcServerLoop
{
    /// <summary>한 틱에 배수할 이벤트 상한. 이벤트 폭주가 틱을 삼키지 않게 한다.</summary>
    public const int MaxEventsPerTick = 4_096;

    /// <summary>틱 예산(ms). 넘으면 오버런으로 센다. docs/11 §5.</summary>
    public const int TickBudgetMs = 20;

    /// <summary>틱 주파수(Hz).</summary>
    public const int TickHz = Tick.PerSecond;

    private readonly IGameServerLink _link;
    private readonly GameClock _clock;
    private readonly EventApplier _applier;
    private readonly InterruptMatcher _interrupts;
    private readonly CognitionScheduler _cognition;
    private readonly PlanExecutor _executor;
    private readonly PlanSwapper _swapper;
    private readonly LodBandSet _bands;
    private readonly ReplanQueue _replanQueue;

    /// <summary>루프를 조립한다. 기동 시 1회.</summary>
    public NpcServerLoop(
        IGameServerLink link,
        GameClock clock,
        EventApplier applier,
        InterruptMatcher interrupts,
        CognitionScheduler cognition,
        PlanExecutor executor,
        PlanSwapper swapper,
        LodBandSet bands,
        ReplanQueue replanQueue)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(applier);
        ArgumentNullException.ThrowIfNull(interrupts);
        ArgumentNullException.ThrowIfNull(cognition);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(swapper);
        ArgumentNullException.ThrowIfNull(bands);
        ArgumentNullException.ThrowIfNull(replanQueue);

        _link = link;
        _clock = clock;
        _applier = applier;
        _interrupts = interrupts;
        _cognition = cognition;
        _executor = executor;
        _swapper = swapper;
        _bands = bands;
        _replanQueue = replanQueue;
    }

    /// <summary>틱 관측자. 없으면 계측하지 않는다.</summary>
    public ITickObserver? Observer { get; init; }

    /// <summary>이 틱에 도달하면 루프를 끝낸다. 0 이면 무제한. 부하·게이트 테스트가 쓴다.</summary>
    public long StopAtTick { get; init; }

    /// <summary>처리한 틱 수.</summary>
    public long TicksProcessed { get; private set; }

    /// <summary>배수한 이벤트 수.</summary>
    public long EventsDrained { get; private set; }

    /// <summary>이벤트 상한에 걸려 다음 틱으로 넘긴 횟수.</summary>
    public long EventBacklogs { get; private set; }

    /// <summary>
    /// 틱 루프. 이벤트가 올 때마다 깨어나 배수하고, 시계가 진행되면 한 틱을 돈다.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (await _link.Events.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            // ── 1. 이벤트 배수 (멱등, N7) ────────────────────────────
            int drained = DrainEvents();

            // ── 2. 틱 진행 (TickSync 를 받았을 때만) ─────────────────
            while (_clock.TryAdvance(out Tick tick))
            {
                RunTick(tick, drained);
                drained = 0;

                if (StopAtTick > 0 && tick.Value >= StopAtTick)
                {
                    await _link.FlushAsync(ct).ConfigureAwait(false);
                    return;
                }
            }

            await _link.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>틱 한 번을 손으로 돌린다. 테스트와 드라이런이 쓴다.</summary>
    public void RunTick(Tick tick, int drained = 0)
    {
        Observer?.OnTickBegin(tick);

        _bands.Rebalance();
        _cognition.Scan(tick, _replanQueue);
        _executor.Step(tick, _link);
        _swapper.ApplyPendingSwaps(_executor);

        TicksProcessed++;
        Observer?.OnTickEnd(tick, _cognition.LastScanned, drained);
    }

    /// <summary>수신 이벤트를 전부 반영한다. 인터럽트는 여기서 즉시 처리된다.</summary>
    private int DrainEvents()
    {
        int drained = 0;

        while (drained < MaxEventsPerTick && _link.Events.TryRead(out GameEvent ev))
        {
            _applier.Apply(in ev);
            _interrupts.Handle(in ev, _clock.Current, _executor, _link, _replanQueue);
            drained++;
        }

        if (drained >= MaxEventsPerTick)
        {
            EventBacklogs++;
        }

        EventsDrained += drained;
        return drained;
    }
}
