using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.MasterData;
using Npc.Sim;
using Npc.TestBed.Protocol;

namespace Npc.TestGameServer.Client;

/// <summary>
/// 클라이언트 제어 적용. docs/20 §8.3.
///
/// <para>
/// 데모에서 사람이 세계를 흔들어 보는 자리다 — 존을 전쟁으로 바꾸고, 시간을 건너뛰고,
/// 고장을 주입하고, NPC 를 지운다. <b>NPC 서버는 이 존재를 모른다.</b> 링크로 나가는 것은
/// 평소와 같은 이벤트뿐이고, 그것이 "게임서버가 바뀌어도 NPC 서버는 안 바뀐다" 는
/// 주장의 실물이다 (docs/20 §1).
/// </para>
///
/// <para>
/// <b>모르는 <see cref="ControlKind"/> 는 무시하고 센다.</b> 낡은 클라이언트가 붙었을 때
/// 게임서버가 죽으면 데모가 못 돈다. 센 값은 <see cref="Ignored"/> 에 남아 원인을 말해 준다.
/// </para>
///
/// <para><b>틱 스레드 전용이다.</b> 월드에 이벤트를 내므로 기록자가 하나여야 한다.</para>
/// </summary>
public sealed class ControlHandler
{
    /// <summary>고장 주입률의 분모. <see cref="Control.Amount"/> 는 만분율(‱)이다.</summary>
    public const int RatePerUnit = 10_000;

    /// <summary>런타임 드롭 판정의 소금. <see cref="FaultInjector"/> 의 것과 달라야 독립이다.</summary>
    private const int DropSalt = 0x0C_7D_0D;

    /// <summary>런타임 실패 판정의 소금.</summary>
    private const int FailSalt = 0x0C_7F_A1;

    private readonly GameWorld _world;
    private readonly MasterDataSet _data;

    /// <summary>
    /// 원래 핸들러. 기동 시 <c>GameWorld.Create</c> 가 붙인 <see cref="FaultInjector"/> 다.
    ///
    /// <b>떼어내지 않고 앞에 선다.</b> 떼면 <c>--fail-rate</c>·<c>--drop-rate</c> 로 시작한
    /// 회차가 <see cref="ControlHandler"/> 를 만드는 순간 조용히 무고장이 된다.
    /// </summary>
    private readonly SimWorld.CommandHandler _inner;

    private uint _dropThreshold;
    private uint _failThreshold;

    /// <summary>
    /// 처리기를 만든다. <b>여기서 <see cref="SimWorld.Handler"/> 앞에 선다</b> —
    /// 런타임 고장 주입(<see cref="ControlKind.SetFaultRate"/>)이 그 자리를 필요로 한다.
    /// </summary>
    public ControlHandler(GameWorld world, MasterDataSet data)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(data);

        _world = world;
        _data = data;

        _inner = world.World.Handler
            ?? throw new ArgumentException("월드에 명령 핸들러가 없다. GameWorld.Create 로 만들어야 한다.", nameof(world));

        world.World.Handler = Gate;
    }

    /// <summary>적용한 제어 수.</summary>
    public long Applied { get; private set; }

    /// <summary>모르는 <see cref="ControlKind"/> 라 무시한 수.</summary>
    public long Ignored { get; private set; }

    /// <summary>아는 종류지만 인자가 틀려 무시한 수 (모르는 존·범위 밖 코드·없는 NPC).</summary>
    public long Rejected { get; private set; }

    /// <summary>
    /// 아직 소비되지 않은 건너뛸 틱 수. 게임 루프가 <see cref="TakeTickSkip"/> 로 가져간다.
    ///
    /// <b>여기서 직접 틱을 밀지 않는다.</b> 틱을 미는 것은 루프의 일이고,
    /// 제어 처리기가 그 권한을 가지면 한 틱이 두 번 도는 회차가 생긴다.
    /// </summary>
    public long PendingTickSkip { get; private set; }

    /// <summary>런타임 실패 주입률(‱). 시작 옵션의 <c>--fail-rate</c> 와 <b>겹쳐서</b> 걸린다.</summary>
    public int FailRatePerUnit => (int)(_failThreshold / (1_000_000 / RatePerUnit));

    /// <summary>런타임 드롭 주입률(‱).</summary>
    public int DropRatePerUnit => (int)(_dropThreshold / (1_000_000 / RatePerUnit));

    /// <summary>런타임 주입으로 조용히 버린 명령 수.</summary>
    public long Dropped { get; private set; }

    /// <summary>런타임 주입으로 실패를 답한 명령 수.</summary>
    public long Failed { get; private set; }

    /// <summary>
    /// 제어 하나를 적용한다. <b>틱 스레드에서 부른다.</b> docs/20 §8.3.
    /// </summary>
    /// <returns>실제로 적용했으면 true. 무시했으면 false.</returns>
    public bool Apply(in Control control, Tick now)
    {
        switch ((ControlKind)control.Kind)
        {
            case ControlKind.SetZoneState:
                return Zone(control.ZoneCode, control.Code, GameEventKind.ZoneStateChanged, now, region: true);

            case ControlKind.SetWeather:
                return Zone(control.ZoneCode, control.Code, GameEventKind.WeatherChanged, now, region: false);

            case ControlKind.SkipTime:
                return SkipTime(control.Amount);

            case ControlKind.SetFaultRate:
                return SetFaultRate(control.Code, control.Amount);

            case ControlKind.Despawn:
                return Despawn(control.Amount, now);

            case ControlKind.Spawn:
                return Spawn(control.Amount, now);

            default:
                // 낡은 클라이언트가 모르는 값을 보냈다. 게임서버는 계속 돈다.
                Ignored++;
                return false;
        }
    }

    /// <summary>
    /// 쌓인 건너뛸 틱 수를 가져가고 0 으로 되돌린다. <b>게임 루프가 부른다.</b>
    /// </summary>
    public long TakeTickSkip()
    {
        long skip = PendingTickSkip;

        PendingTickSkip = 0;

        return skip;
    }

    // ---------------------------------------------------------------- 개별 제어

    /// <summary>
    /// 존 상태·기후를 바꾼다.
    ///
    /// <b>이벤트만 낸다.</b> 존 전체의 플랜이 갈리는 것은 NPC 서버가 이 이벤트를 받고
    /// 버킷 키를 다시 푸는 결과이지, 게임서버가 시키는 일이 아니다 (docs/14 §5).
    /// </summary>
    private bool Zone(ushort zoneCode, byte code, GameEventKind kind, Tick now, bool region)
    {
        var zone = new ZoneId(zoneCode);
        bool known = false;

        // ZoneTable 의 첨자는 모르는 code 에 예외를 던진다. 여기서는 사용자 입력이라
        // 예외가 아니라 거절이 맞다 — 낡은 클라이언트 하나가 게임서버를 죽이면 안 된다.
        foreach (ZoneDef def in _data.Zones.Zones)
        {
            if (def.Code == zone)
            {
                known = true;
                break;
            }
        }

        bool valid = region
            ? Enum.IsDefined((RegionState)code)
            : Enum.IsDefined((Climate)code);

        if (!known || !valid)
        {
            Rejected++;
            return false;
        }

        _world.World.Emit(new GameEvent
        {
            Kind = kind,
            Sequence = 0,
            OccurredAt = now,
            Zone = zone,
            Code = code,
        });

        Applied++;
        return true;
    }

    /// <summary>
    /// 게임 시각을 <paramref name="gameMinutes"/> 만큼 건너뛴다. docs/20 §8.3.
    ///
    /// <para>
    /// <b>부작용이 있다.</b> 틱을 점프시키면 <c>MovementSim._arriveAt</c> 과
    /// <c>InteractionSim._completeAt</c> 이 <b>전부 만료되어</b> 진행 중인 이동·작업이
    /// 다음 틱에 한꺼번에 끝난다. 도착 이벤트가 몰려 나가고, 그 순간 NPC 들이
    /// 목적지로 순간이동한 것처럼 보인다.
    /// </para>
    ///
    /// <para>
    /// <b>데모 편의 기능이고 결정론 리플레이 대상이 아니다.</b> 시나리오 파일로 돌리는
    /// 회차에서는 쓰지 않는다 — 같은 시나리오가 건너뛴 만큼 다르게 흐른다.
    /// </para>
    /// </summary>
    private bool SkipTime(int gameMinutes)
    {
        if (gameMinutes <= 0)
        {
            Rejected++;
            return false;
        }

        // 게임 분 → 게임 초 → 틱. docs/20 §8.3 의 식 그대로다.
        PendingTickSkip += (long)gameMinutes * 60 * Tick.PerSecond / _world.World.Options.TimeScale;

        Applied++;
        return true;
    }

    /// <summary>
    /// 런타임 고장 주입률을 바꾼다. <paramref name="which"/> 0=실패 · 1=드롭.
    ///
    /// <b>시작 옵션과 겹쳐서 걸린다.</b> 이 처리기는 <see cref="FaultInjector"/> 를 떼지 않고
    /// 그 앞에 서기 때문이다 — 떼면 <c>--fail-rate</c> 로 시작한 회차가 조용히 무고장이 된다.
    /// 데모 기본값이 0 이라 실제로는 이 값이 전부다.
    /// </summary>
    private bool SetFaultRate(byte which, int perUnit)
    {
        if (which > 1 || perUnit < 0 || perUnit > RatePerUnit)
        {
            Rejected++;
            return false;
        }

        // ‱ → 백만분율. FaultInjector 와 같은 분모를 쓴다.
        var threshold = (uint)((long)perUnit * (1_000_000 / RatePerUnit));

        if (which == 0)
        {
            _failThreshold = threshold;
        }
        else
        {
            _dropThreshold = threshold;
        }

        Applied++;
        return true;
    }

    /// <summary>
    /// NPC 하나를 지운다. <c>TargetGone</c> 경로 확인용이다 (docs/20 §8.3).
    ///
    /// NPC 서버 쪽에서는 그 NPC 를 상대로 하던 플랜 스텝이 실패하고 재계획이 걸려야 한다.
    /// </summary>
    /// <summary>
    /// 슬롯을 다시 spawn 한다 (B-05).
    ///
    /// <b>이미 스폰돼 있으면 거절한다.</b> 같은 슬롯에 두 번 스폰하면 NPC 서버는 그것을
    /// 멱등하게 무시하지만(N7), 여기서 세는 편이 "제어가 먹혔는가" 를 보는 데 낫다.
    /// </summary>
    private bool Spawn(int npc, Tick now)
    {
        if ((uint)npc >= (uint)_world.World.Capacity || _world.World.IsSpawned(npc))
        {
            Rejected++;
            return false;
        }

        _world.World.ApplyCommand(
            new NpcCommand
            {
                Kind = NpcCommandKind.Spawn,
                Npc = new NpcId(npc),
                IssuedAt = now,
                Correlation = default,
                Priority = CommandPriority.Critical,
            },
            now);

        Applied++;
        return true;
    }

    private bool Despawn(int npc, Tick now)
    {
        if (!_world.World.IsSpawned(npc))
        {
            Rejected++;
            return false;
        }

        _world.World.ApplyCommand(
            new NpcCommand
            {
                Kind = NpcCommandKind.Despawn,
                Npc = new NpcId(npc),
                IssuedAt = now,
                Correlation = default,
                Priority = CommandPriority.Critical,
            },
            now);

        Applied++;
        return true;
    }

    // ---------------------------------------------------------------- 런타임 고장 주입

    /// <summary>
    /// <see cref="SimWorld.Handler"/> 앞에 서는 관문.
    ///
    /// 주입률이 0 이면 곧바로 원래 핸들러로 넘긴다 — 데모를 안 만지는 동안은 비용이 없다.
    /// </summary>
    private bool Gate(in NpcCommand command, Tick now)
    {
        // 드롭은 응답을 아예 주지 않는다. 실패 이벤트조차 보내지 않는 것이 핵심이다 —
        // 그래야 NPC 서버가 타임아웃을 합성해야만 진행된다 (docs/02 §1).
        if (_dropThreshold > 0 && Roll(in command, DropSalt) < _dropThreshold)
        {
            Dropped++;
            return true;
        }

        if (_failThreshold > 0 && Roll(in command, FailSalt) < _failThreshold)
        {
            Failed++;
            _world.World.Fail(in command, now, ActionFailReason.PreconditionFailed);
            return true;
        }

        return _inner(in command, now);
    }

    /// <summary>0..999,999 의 결정론 난수. <see cref="FaultInjector"/> 와 같은 방식이다.</summary>
    private uint Roll(in NpcCommand command, int salt)
    {
        uint mixed = PlanHash.Mix(
            PlanHash.Mix(_world.World.Options.Seed, salt)
            ^ PlanHash.Mix(command.Correlation.Value)
            ^ PlanHash.Mix((uint)command.Npc.Value));

        return mixed % 1_000_000;
    }
}
