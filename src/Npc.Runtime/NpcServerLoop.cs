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
    private long _lastSequence;

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


    /// <summary>
    /// 틱 관측자. 없으면 계측하지 않는다.
    /// 관측자가 루프 통계를 읽어야 해서 조립 순서상 루프가 먼저 생긴다 — 그래서 init 이 아니다.
    /// </summary>
    public ITickObserver? Observer { get; set; }

    /// <summary>이 틱에 도달하면 루프를 끝낸다. 0 이면 무제한. 부하·게이트 테스트가 쓴다.</summary>
    public long StopAtTick { get; init; }

    /// <summary>시간대 전환 지터. 없으면 전환이 한 틱에 몰린다 (docs/14 §5).</summary>
    public BucketTransition? Transition { get; init; }

    /// <summary>
    /// 재계획 워커와의 인계 통로 (docs/14 §4). <b>없으면 워커가 없다는 뜻이다</b> —
    /// <c>--tier none</c> 회차가 그렇고, 그때 힙은 채워지기만 하고 비지 않는다
    /// (그 성질을 보는 것이 P4 게이트 항목 10 이다).
    ///
    /// <b>붙어 있으면 힙을 만지는 것은 틱 루프뿐이다.</b> 워커는 이 통로만 본다 —
    /// 그러지 않으면 힙에 데이터 레이스가 생긴다 (<see cref="ReplanHandoff"/> 주석).
    /// </summary>
    public ReplanHandoff? Handoff { get; init; }

    /// <summary>한 틱에 통로로 옮길 상한. 0 이면 <see cref="ReplanHandoff.DefaultPumpPerTick"/>.</summary>
    public int PumpPerTick { get; init; }

    /// <summary>
    /// 생존 신호 수신자 (A-03). 없으면 계측하지 않는다.
    ///
    /// <b>이벤트 대기에서 깨어날 때마다</b> 한 번 친다 — 틱마다가 아니다. 게임서버가
    /// <c>TickSync</c> 를 멈추면 틱은 안 돌지만 루프는 살아 있고, 그 구별이 liveness 와
    /// readiness 를 가르는 지점이다 (전자는 200, 후자는 503).
    /// </summary>
    public ILoopProbe? Probe { get; set; }

    /// <summary>
    /// 스냅샷 통로 (A-01). 없으면 스냅샷을 뜨지 않는다.
    ///
    /// 틱 <b>끝</b>에서만 복사한다 — 스텝 경계이자 스왑이 끝난 뒤라 "이 틱이 끝난 상태" 가
    /// 그대로 담긴다. 복사 자체는 <c>Array.Copy</c> 뿐이라 할당 0 이다.
    /// </summary>
    public SnapshotPort? Snapshots { get; set; }

    /// <summary>처리한 틱 수.</summary>
    public long TicksProcessed { get; private set; }

    /// <summary>
    /// 명령까지 내보낸 틱 수. <b>결정론의 기준선이다</b> (docs/15 §3).
    ///
    /// <see cref="TicksProcessed"/> 는 <see cref="RunTick"/> 이 끝나면 오르지만 그 시점에
    /// 이번 틱의 명령은 아직 링크 큐 안에 있다. 게임서버 대역이 그 숫자만 보고 다음 틱을 밀면
    /// <b>어떤 명령이 다음 틱에 반영되는지가 스레드 스케줄에 달린다</b> —
    /// 같은 시나리오를 두 번 돌려도 이벤트 수가 달라진다(실측 30,416 vs 30,649).
    /// 이 값은 <c>FlushAsync</c> 뒤에 오르므로 여기에 맞춰 페이싱하면 그 창이 닫힌다.
    /// </summary>
    public long TicksCommitted { get; private set; }

    /// <summary>배수한 이벤트 수.</summary>
    public long EventsDrained { get; private set; }

    /// <summary>이벤트 상한에 걸려 다음 틱으로 넘긴 횟수.</summary>
    public long EventBacklogs { get; private set; }

    /// <summary>
    /// N6 — 시퀀스가 끊긴 횟수. 모든 이벤트가 여기 한 곳을 지나므로 검출도 여기서 한다.
    /// 링크 구현체마다 넣으면 구현체를 바꿀 때마다 검출이 사라진다.
    /// 재주입(N7)으로 같은 시퀀스가 두 번 오는 것은 갭이 아니다.
    /// </summary>
    public long EventGaps { get; private set; }

    /// <summary>
    /// 틱 루프. 이벤트가 올 때마다 깨어나 배수하고, 시계가 진행되면 한 틱을 돈다.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        // 대기에 들어가기 전에 한 번 친다. 이벤트가 영영 오지 않는 회차에서도
        // "루프 스레드는 떴다" 가 참이어야 startup 프로브가 통과한다.
        Probe?.Beat();

        while (await _link.Events.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            Probe?.Beat();

            // 게임서버가 알려준 시각을 반영한다 (A-10). 틱 경계이자 배수 전이다 —
            // 이벤트를 먼저 먹이면 낡은 시각으로 판단한 뒤 시계만 뒤늦게 뛴다.
            _clock.TryApplyPendingOrigin();

            // ── 1. 이벤트 배수 (멱등, N7) ────────────────────────────
            int drained = DrainEvents();

            // ── 2. 틱 진행 (TickSync 를 받았을 때만) ─────────────────
            while (_clock.TryAdvance(out Tick tick))
            {
                RunTick(tick, drained);
                drained = 0;

                // 틱마다 내보낸다. 바깥에서 한 번만 하면 이번 틱의 명령이 큐에 남은 채
                // TicksProcessed 가 먼저 올라 페이싱이 그 창을 보게 된다.
                await _link.FlushAsync(ct).ConfigureAwait(false);
                TicksCommitted = tick.Value;

                if (StopAtTick > 0 && tick.Value >= StopAtTick)
                {
                    return;
                }
            }

            // 인터럽트가 강제한 명령은 틱 밖(배수 중)에서 나온다 — 그것들도 내보내야 한다.
            await _link.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>틱 한 번을 손으로 돌린다. 테스트와 드라이런이 쓴다.</summary>
    public void RunTick(Tick tick, int drained = 0)
    {
        Observer?.OnTickBegin(tick);

        _bands.Rebalance();

        // 워커가 되돌린 낡은 요청을 먼저 힙에 넣는다 — 이번 스캔이 그것까지 보고 순서를 잡는다.
        Handoff?.DrainReturns(_replanQueue);

        _cognition.Scan(tick, _replanQueue);

        // 힙에서 통로로 옮긴다. <b>힙을 만지는 것은 여기까지가 틱 루프의 전부다</b> —
        // 워커는 통로만 본다 (ReplanHandoff 주석).
        Handoff?.Pump(_replanQueue, PumpPerTick);

        _executor.Step(tick, _link);

        // 시간대 전환 예약은 스왑 적용 전에 걸어야 이번 틱의 스텝 경계에서 갈아탄다.
        Transition?.Tick(_clock, _swapper);

        _swapper.ApplyPendingSwaps(_executor);

        TicksProcessed++;

        // 스냅샷 복사는 맨 끝이다 (A-01).
        Snapshots?.TryCapture(tick.Value, _clock.SyncedTick);

        Observer?.OnTickEnd(tick, _cognition.LastScanned, drained);
    }

    /// <summary>
    /// 수신 이벤트를 반영한다. 인터럽트는 여기서 즉시 처리된다.
    ///
    /// <b><c>TickSync</c> 를 만나면 거기서 끊는다</b> (docs/15 §3). 게임서버 대역이 틱마다
    /// <c>TickSync</c> 를 마지막에 내므로 이 경계가 곧 틱 경계다. 끊지 않으면 생산자가
    /// 앞서 나간 만큼 다음 틱의 이벤트까지 한 배수에 들어와,
    /// <b>아직 돌리지 않은 틱의 결과가 이번 틱의 판단에 반영된다.</b>
    /// 얼마나 들어오는지는 스레드 스케줄이 정하므로 같은 시나리오가 실행마다 갈라진다.
    /// </summary>
    private int DrainEvents()
    {
        int drained = 0;

        while (drained < MaxEventsPerTick && _link.Events.TryRead(out GameEvent ev))
        {
            if (ev.Sequence > _lastSequence)
            {
                if (_lastSequence != 0 && ev.Sequence != _lastSequence + 1)
                {
                    EventGaps++;
                }

                _lastSequence = ev.Sequence;
            }

            _applier.Apply(in ev);

            // 존 상태·기후가 바뀌면 그 존만 버킷 전환을 예약한다 (docs/14 §5 · T4-13).
            // 모든 이벤트가 지나는 곳은 여기 하나뿐이라 검출도 여기서 한다.
            Transition?.Observe(in ev, _clock);

            // 존 이벤트는 NPC 를 지목하지 않는다 (ev.Npc = 0). 그 존 전원을 평가해야
            // "War 수신 → 1틱 내 즉시 반응" 이 성립한다 (docs/14 §7 · T4-23).
            if (ev.Kind is GameEventKind.ZoneStateChanged or GameEventKind.WeatherChanged)
            {
                _interrupts.HandleZone(in ev, _clock.Current, _executor, _link, _replanQueue);
            }
            else
            {
                _interrupts.Handle(in ev, _clock.Current, _executor, _link, _replanQueue);
            }

            drained++;

            if (ev.Kind == GameEventKind.TickSync)
            {
                break;
            }
        }

        if (drained >= MaxEventsPerTick)
        {
            EventBacklogs++;
        }

        EventsDrained += drained;
        return drained;
    }
}
