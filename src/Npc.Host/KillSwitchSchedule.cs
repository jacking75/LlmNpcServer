using System.Collections.Immutable;
using Npc.Contracts;
using Npc.Core;
using Npc.MasterData;
using Npc.Runtime;
using Npc.Sim;

namespace Npc.Host;

/// <summary>
/// 틱 기반 킬스위치 스케줄. docs/20 §11.4.
///
/// <para>
/// <b>킬스위치는 NPC 서버 안쪽 상태다.</b> 링크로 보낼 수단이 없고, 만들면 N 규칙을 어긴다
/// (패킷에 제어 신호를 실으면 그건 더 이상 fire-and-forget 명령 채널이 아니다).
/// 그래서 <b>같은 jsonl 을 양쪽에 준다</b> — 게임서버는 <c>KillSwitch</c> 줄을 무시하고,
/// NPC 서버는 <c>KillSwitch</c> 줄만 처리한다. 틱 번호는 게임서버가 보내는 <c>TickSync</c> 로
/// 동기화되어 있으므로 같은 틱에 발동한다.
/// </para>
///
/// <para>
/// <b>이벤트는 주입하지 않는다.</b> <c>ScenarioRunner.Tick(now, world)</c> 를 부르지 않는다 —
/// <c>--link tcp</c> 에서 세계를 미는 것은 게임서버다. 파서만 재사용한다.
/// </para>
///
/// <para>
/// <b><c>Npc.Runtime</c> 을 한 줄도 고치지 않았다.</b> docs/20 §11.4 는
/// <c>NpcServerLoop</c> 에 훅 한 줄을 넣는 것으로 적혀 있었는데,
/// <see cref="ITickObserver"/> 라는 이음매가 이미 있어 필요가 없었다 —
/// 이 타입이 그 인터페이스를 구현하고 원래 관측자를 감싼다.
/// <b>그래서 P6 합격 기준 1(본체 무변경)이 이 태스크에서도 깨지지 않는다.</b>
/// </para>
/// </summary>
internal sealed class KillSwitchSchedule : ITickObserver
{
    private readonly ImmutableArray<Entry> _entries;
    private readonly KillSwitchState _switches;
    private readonly ITickObserver? _inner;
    private readonly List<KillSwitchTarget> _fired = [];

    private int _next;

    private KillSwitchSchedule(
        ImmutableArray<Entry> entries, KillSwitchState switches, ITickObserver? inner)
    {
        _entries = entries;
        _switches = switches;
        _inner = inner;
    }

    /// <summary>예약된 스위치 수.</summary>
    public int Count => _entries.Length;

    /// <summary>발동한 순서. 데모·테스트가 읽는다.</summary>
    public IReadOnlyList<KillSwitchTarget> Fired => _fired;

    /// <summary>
    /// 시나리오 jsonl 에서 <c>KillSwitch</c> 줄만 골라 스케줄을 만든다.
    ///
    /// <b>다른 줄은 전부 무시한다</b> — <c>ZoneStateChanged</c>·<c>WeatherChanged</c> 는
    /// 게임서버가 낼 이벤트이고, 여기서 같이 내면 세계가 두 번 밀린다.
    /// </summary>
    /// <param name="path">시나리오 파일. null 이면 빈 스케줄.</param>
    /// <param name="data">마스터데이터. 파서가 존 id 를 푸는 데 쓴다.</param>
    /// <param name="switches">발동 대상 상태 객체.</param>
    /// <param name="inner">감쌀 관측자. 계측기를 여기 넣는다.</param>
    public static KillSwitchSchedule Load(
        string? path, MasterDataSet data, KillSwitchState switches, ITickObserver? inner = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(switches);

        if (path is null || !File.Exists(path))
        {
            return new KillSwitchSchedule([], switches, inner);
        }

        return Parse(File.ReadLines(path), data, switches, inner);
    }

    /// <summary>jsonl 줄들에서 읽는다. <see cref="ScenarioRunner"/> 의 파서를 그대로 쓴다.</summary>
    public static KillSwitchSchedule Parse(
        IEnumerable<string> lines,
        MasterDataSet data,
        KillSwitchState switches,
        ITickObserver? inner = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(switches);

        ScenarioRunner runner = ScenarioRunner.Parse(lines, data);
        var entries = ImmutableArray.CreateBuilder<Entry>();

        foreach (ScenarioStep step in runner.Steps)
        {
            if (step.KillSwitch is { } target)
            {
                entries.Add(new Entry(step.AtTick, target));
            }
        }

        // at_tick 오름차순. ScenarioRunner 가 이미 그 순서를 보장하지만,
        // 여기서 순서를 다시 가정하지 않는다 — 파서가 바뀌어도 ±0틱이 유지되어야 한다.
        entries.Sort(static (a, b) => a.AtTick.CompareTo(b.AtTick));

        return new KillSwitchSchedule(entries.ToImmutable(), switches, inner);
    }

    /// <summary>
    /// 이 틱에 예약된 스위치를 전부 발동한다. <b>±0틱</b>이다.
    ///
    /// 첨자 하나와 비교 한 번이라 틱 예산에 영향이 없다.
    /// </summary>
    public void Advance(Tick tick)
    {
        while (_next < _entries.Length && _entries[_next].AtTick <= tick.Value)
        {
            KillSwitchTarget target = _entries[_next].Target;

            _switches.Fire(target);
            _fired.Add(target);
            _next++;
        }
    }

    /// <inheritdoc />
    public void OnTickBegin(Tick tick)
    {
        Advance(tick);
        _inner?.OnTickBegin(tick);
    }

    /// <inheritdoc />
    public void OnTickEnd(Tick tick, int scanned, int drained) =>
        _inner?.OnTickEnd(tick, scanned, drained);

    private readonly record struct Entry(long AtTick, KillSwitchTarget Target);
}
