using System.Collections.Immutable;
using System.Diagnostics;
using Npc.Contracts;
using Npc.Gateway;

namespace Npc.Conformance;

/// <summary>
/// 소켓에 붙어 관찰만 한다 (B-07).
///
/// <para>
/// <b>NPC 서버 대신 붙는다.</b> 같은 <see cref="TcpGameServerLink"/> 를 쓰므로 핸드셰이크·
/// 프레이밍·인증은 실제 NPC 서버와 <b>같은 코드</b>다 — 여기서 붙으면 NPC 서버도 붙는다.
/// </para>
///
/// <para>
/// <b>판정하지 않는다.</b> 관찰 기록만 만들고 판정은 <see cref="Report"/> 가 한다.
/// 그래야 고의 위반을 합성한 기록으로 검사 자체를 시험할 수 있다.
/// </para>
/// </summary>
public sealed class Observer
{
    private readonly TcpGameServerLink _link;
    private readonly List<Observed> _events = [];
    private readonly List<Issued> _commands = [];
    private readonly Stopwatch _clock = new();
    private readonly Lock _gate = new();

    /// <summary>관찰자를 만든다.</summary>
    /// <param name="link">붙일 링크. <b>연결 전이어야 한다</b> — 관찰은 첫 프레임부터다.</param>
    /// <param name="npcCount">기대 NPC 수.</param>
    /// <param name="zoneCount">기대 존 수.</param>
    public Observer(TcpGameServerLink link, int npcCount, int zoneCount)
    {
        ArgumentNullException.ThrowIfNull(link);

        _link = link;
        NpcCount = npcCount;
        ZoneCount = zoneCount;

        // 리시버 태스크가 부른다. 프레임 번호는 여기서만 알 수 있다.
        _link.EventObserver = (ev, frame) =>
        {
            lock (_gate)
            {
                _events.Add(new Observed(ev, (int)frame, _clock.ElapsedMilliseconds));
            }
        };
    }

    /// <summary>기대 NPC 수.</summary>
    public int NpcCount { get; }

    /// <summary>기대 존 수.</summary>
    public int ZoneCount { get; }

    /// <summary>지금까지 받은 이벤트 수.</summary>
    public int EventCount
    {
        get
        {
            lock (_gate)
            {
                return _events.Count;
            }
        }
    }

    /// <summary>벽시계를 시작한다. <b>연결 직후에 부른다.</b></summary>
    public void Start() => _clock.Restart();

    /// <summary>벽시계를 멈춘다.</summary>
    public void Stop() => _clock.Stop();

    /// <summary>
    /// 우리가 명령을 냈다고 기록한다. <b>발행 경로가 직접 부른다</b> —
    /// 링크는 무엇을 보냈는지 기억하지 않는다 (N1: fire-and-forget).
    /// </summary>
    /// <param name="command">보낸 명령.</param>
    /// <param name="timeoutTicks">이 틱 안에 응답이 와야 한다.</param>
    public void RecordIssued(in NpcCommand command, long timeoutTicks)
    {
        lock (_gate)
        {
            _commands.Add(new Issued(
                command.Correlation, command.Kind, command.Npc, command.IssuedAt.Value, timeoutTicks));
        }
    }

    /// <summary>
    /// 지금까지의 관찰을 굳힌다. <b>벽시계는 실제로 잰 값만 싣는다</b> —
    /// 구동 회차(테스트가 틱을 미는 회차)에서는 0 을 실어 속도 검사가 미판정이 되게 한다.
    /// </summary>
    /// <param name="paced">게임서버가 스스로 페이싱했는가. false 면 속도를 판정하지 않는다.</param>
    public Observation Snapshot(bool paced)
    {
        lock (_gate)
        {
            long lastTick = 0;

            foreach (Observed observed in _events)
            {
                lastTick = Math.Max(lastTick, observed.Event.OccurredAt.Value);
            }

            LinkStats stats = _link.Stats;

            return new Observation
            {
                ProtocolVersion = _link.NegotiatedVersion,
                ContractMinor = _link.NegotiatedContractMinor,
                Features = _link.NegotiatedFeatures,
                NegotiationDetail = _link.NegotiationDetail,
                Connected = _link.State is LinkState.Connected or LinkState.Degraded,
                NpcCount = NpcCount,
                ZoneCount = ZoneCount,
                Events = [.. _events],
                Commands = [.. _commands],
                WallClockMillis = paced ? _clock.ElapsedMilliseconds : 0,
                EventGaps = stats.EventGapsDetected,
                CommandsDropped = stats.CommandsDropped,
                LastTick = lastTick,
            };
        }
    }

    /// <summary>관찰을 끊는다. 링크는 호출부가 정리한다.</summary>
    public void Detach() => _link.EventObserver = null;

    /// <summary>지금까지 받은 것 (읽기 전용 사본).</summary>
    public ImmutableArray<Observed> Events
    {
        get
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }
    }
}
