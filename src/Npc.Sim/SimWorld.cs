using System.Threading.Channels;
using Npc.Contracts;
using Npc.MasterData;

namespace Npc.Sim;

/// <summary>Sim 설정. docs/11 §6.</summary>
/// <param name="Seed">난수 시드. 고정하면 같은 시나리오가 같은 결과를 낸다.</param>
/// <param name="TimeScale">게임 시간 배속. 게임 초 ↔ 틱 환산에 쓴다.</param>
/// <param name="FailRate">액션이 실패할 확률 0~1. 폴백 경로가 실제로 도는지 확인한다.</param>
/// <param name="DropRate">명령을 조용히 버릴 확률 0~1. 타임아웃 합성을 검증한다.</param>
/// <param name="PlayerBots">존을 랜덤 워크하는 가상 플레이어 수.</param>
/// <param name="TransformPeriodTicks">이동 중 NpcTransform 발행 주기.</param>
public sealed record SimOptions(
    int Seed = 20260725,
    int TimeScale = 600,
    double FailRate = 0,
    double DropRate = 0,
    int PlayerBots = 0,
    int TransformPeriodTicks = 10);

/// <summary>
/// 게임서버 대역. docs/02 §5 · docs/11 §6.
///
/// <b>게임서버를 만드는 것이 아니다.</b> NPC 서버를 혼자 돌려보기 위한 우리 쪽 가짜 구현이며,
/// 실제 게임서버가 할 일을 최소한으로 흉내낸다. 패스파인딩도 전투 판정도 하지 않는다.
///
/// 모든 이벤트에 순증 <c>Sequence</c> 를 붙인다 (N6). 시각은 <c>Tick</c> 뿐이다 (N4).
/// 난수는 전부 시드 고정이라 같은 입력이면 같은 세계가 나온다.
/// </summary>
public sealed partial class SimWorld : IAsyncDisposable
{
    private readonly MasterDataSet _data;
    private readonly Channel<GameEvent> _events;
    private readonly bool[] _spawned;
    private readonly ushort[] _poi;
    private readonly ushort[] _zone;
    private readonly ushort[] _archetype;

    /// <summary>NPC 가 속한 채널·인스턴스 (B-02). 0 = 기본 월드.</summary>
    private readonly ushort[] _instance;

    private readonly int[] _inventory;
    private readonly int _stride;
    private long _sequence;

    /// <summary>월드를 만든다. 기동 시 1회.</summary>
    /// <param name="data">마스터데이터.</param>
    /// <param name="capacity">NPC 수용량.</param>
    /// <param name="options">설정.</param>
    public SimWorld(MasterDataSet data, int capacity, SimOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);

        _data = data;
        Options = options ?? new SimOptions();
        Capacity = capacity;
        _stride = data.Items.MaxCode + 1;

        _events = Channel.CreateUnbounded<GameEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        _spawned = new bool[capacity];
        _poi = new ushort[capacity];
        _zone = new ushort[capacity];
        _archetype = new ushort[capacity];
        _instance = new ushort[capacity];
        _inventory = new int[(long)capacity * _stride <= int.MaxValue
            ? capacity * _stride
            : throw new ArgumentOutOfRangeException(nameof(capacity), "인벤토리 배열이 int 범위를 넘는다.")];
        ObservedByPlayer = new bool[capacity];
    }

    /// <summary>설정.</summary>
    public SimOptions Options { get; }

    /// <summary>NPC 수용량.</summary>
    public int Capacity { get; }

    /// <summary>NPC 서버가 읽는 이벤트 스트림.</summary>
    public ChannelReader<GameEvent> Events => _events.Reader;

    /// <summary>마지막으로 발행한 시퀀스 번호.</summary>
    public long Sequence => _sequence;

    /// <summary>발행한 이벤트 수.</summary>
    public long EventsEmitted { get; private set; }

    /// <summary>처리한 명령 수.</summary>
    public long CommandsHandled { get; private set; }

    /// <summary>마스터데이터.</summary>
    public MasterDataSet Data => _data;

    /// <summary>
    /// 플레이어가 보고 있는 NPC. <see cref="PlayerBots"/> 가 갱신하고 <see cref="TransformEmitter"/> 가 읽는다.
    /// NPC 서버의 LOD 등급과 같은 정보를 Sim 쪽에서 표현한 것이다 —
    /// Sim 은 플레이어 봇을 직접 들고 있으므로 근접 여부를 안다.
    /// </summary>
    public bool[] ObservedByPlayer { get; }

    /// <summary>이 NPC 가 스폰됐는가.</summary>
    public bool IsSpawned(int npc) => (uint)npc < (uint)Capacity && _spawned[npc];

    /// <summary>이 NPC 가 지금 있는 POI.</summary>
    public PoiId PoiOf(int npc) => new(_poi[npc]);

    /// <summary>이 NPC 가 지금 있는 존.</summary>
    public ZoneId ZoneOf(int npc) => new(_zone[npc]);

    /// <summary>이 NPC 의 아키타입.</summary>
    public ArchetypeId ArchetypeOf(int npc) => new(_archetype[npc]);

    /// <summary>이 NPC 가 속한 채널·인스턴스 (B-02).</summary>
    public InstanceId InstanceOf(int npc) => new(_instance[npc]);

    /// <summary>
    /// 되돌아온 명령의 인스턴스가 스폰 때 알려준 값과 달랐던 횟수 (B-02).
    ///
    /// <b>0 이 아니면 파이프 어딘가에서 값이 떨어졌다.</b> v1 링크에서는 확장 슬롯이
    /// 실리지 않으므로 <see cref="InstanceEchoChecked"/> 가 0 이고 이 값도 0 이다 —
    /// 두 수를 같이 봐야 "통과했다" 와 "아예 안 봤다" 가 갈린다.
    /// </summary>
    public long InstanceMismatches { get; private set; }

    /// <summary>인스턴스를 실제로 대조한 명령 수 (B-02).</summary>
    public long InstanceEchoChecked { get; private set; }

    /// <summary>
    /// NPC 를 채널·인스턴스에 넣는다 (B-02). <b>스폰 전에 부른다.</b>
    ///
    /// 게임서버가 정하는 값이다 — NPC 서버는 <c>NpcSpawned</c> 로 이 값을 받아
    /// 이후 명령에 그대로 되돌려준다.
    /// </summary>
    public void SetInstance(int npc, InstanceId instance)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(npc);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(npc, Capacity);

        _instance[npc] = instance.Value;
    }

    /// <summary>이 NPC 의 인벤토리.</summary>
    public Span<int> InventoryOf(int npc) => _inventory.AsSpan(npc * _stride, _stride);

    /// <summary>NPC 를 미리 배치한다. 스폰 이벤트는 Spawn 명령을 받았을 때 나간다.</summary>
    public void Place(int npc, ArchetypeId archetype, PoiId poi)
    {
        _archetype[npc] = archetype.Value;
        _poi[npc] = poi.Value;
        _zone[npc] = poi.Value == 0 ? (ushort)0 : _data.Pois[poi].Zone.Value;
    }

    /// <summary>NPC 를 그 POI 로 옮긴다. 이동 시뮬이 도착 시 부른다.</summary>
    public void MoveTo(int npc, PoiId poi)
    {
        _poi[npc] = poi.Value;
        _zone[npc] = poi.Value == 0 ? (ushort)0 : _data.Pois[poi].Zone.Value;
    }

    /// <summary>
    /// NPC 서버가 보낸 명령을 처리한다. docs/02 §5.
    /// <b>결과는 전부 이벤트로 돌아간다</b> — 이 메서드는 아무것도 반환하지 않는다 (N1).
    /// </summary>
    public void ApplyCommand(in NpcCommand command, Tick now)
    {
        CommandsHandled++;

        int npc = command.Npc.Value;
        if ((uint)npc >= (uint)Capacity)
        {
            return;
        }

        // B-02 — 되돌아온 인스턴스를 대조한다. Spawn 은 제외다: 그 명령이 나갈 때
        // NPC 서버는 아직 NpcSpawned 를 못 받았으므로 인스턴스를 모른다.
        if (command.Kind != NpcCommandKind.Spawn && _instance[npc] != 0 && _spawned[npc])
        {
            InstanceEchoChecked++;

            if (command.Instance.Value != _instance[npc])
            {
                InstanceMismatches++;
            }
        }

        switch (command.Kind)
        {
            case NpcCommandKind.Spawn:
                _spawned[npc] = true;
                if (command.Zone.Value != 0)
                {
                    _zone[npc] = command.Zone.Value;
                }

                if (command.Archetype.Value != 0 || _archetype[npc] == 0)
                {
                    _archetype[npc] = command.Archetype.Value;
                }

                Emit(new GameEvent
                {
                    Kind = GameEventKind.NpcSpawned,
                    Sequence = 0,
                    OccurredAt = now,
                    Npc = command.Npc,
                    Correlation = command.Correlation,
                    Poi = new PoiId(_poi[npc]),
                    Zone = new ZoneId(_zone[npc]),
                    Pos = PositionOf(npc),

                    // B-02 — 인스턴스는 게임서버가 정한다. NPC 서버는 이 값을 기억했다가
                    // 이후 명령에 되돌려준다.
                    Instance = new InstanceId(_instance[npc]),
                });
                return;

            case NpcCommandKind.Despawn:
                _spawned[npc] = false;
                Emit(new GameEvent
                {
                    Kind = GameEventKind.NpcDespawned,
                    Sequence = 0,
                    OccurredAt = now,
                    Npc = command.Npc,
                    Correlation = command.Correlation,
                });
                return;

            case NpcCommandKind.Stop:
            case NpcCommandKind.FaceTo:
            case NpcCommandKind.SetAggro:
                Complete(command, now);
                return;

            case NpcCommandKind.SetVisualState:
            case NpcCommandKind.PlayAnimation:
            case NpcCommandKind.Speak:
                // 연출은 즉시 끝난 것으로 본다.
                Complete(command, now);
                return;

            default:
                // MoveTo / Interact / InventoryChange / CombatAction 은
                // MovementSim · InteractionSim 이 처리한다 (T1-47, T1-48).
                Unhandled(command, now);
                return;
        }
    }

    /// <summary>한 틱. 하위 시뮬들이 여기서 이벤트를 낸다.</summary>
    public void Tick(Tick now)
    {
        Emit(new GameEvent
        {
            Kind = GameEventKind.TickSync,
            Sequence = 0,
            OccurredAt = now,
        });
    }

    /// <summary>순증 시퀀스를 붙여 이벤트를 낸다 (N6). 시퀀스 필드의 값은 여기서 덮어쓴다.</summary>
    public void Emit(in GameEvent ev)
    {
        GameEvent stamped = ev with { Sequence = ++_sequence };

        _events.Writer.TryWrite(stamped);
        EventsEmitted++;
    }

    /// <summary>액션이 즉시 완료됐다고 알린다.</summary>
    public void Complete(in NpcCommand command, Tick now) => Emit(new GameEvent
    {
        Kind = GameEventKind.NpcActionCompleted,
        Sequence = 0,
        OccurredAt = now,
        Npc = command.Npc,
        Correlation = command.Correlation,
    });

    /// <summary>액션이 실패했다고 알린다.</summary>
    public void Fail(in NpcCommand command, Tick now, ActionFailReason reason) => Emit(new GameEvent
    {
        Kind = GameEventKind.NpcActionFailed,
        Sequence = 0,
        OccurredAt = now,
        Npc = command.Npc,
        Correlation = command.Correlation,
        Code = (byte)reason,
    });

    /// <summary>이 NPC 의 현재 좌표.</summary>
    public WorldPos PositionOf(int npc) =>
        _poi[npc] == 0 ? default : _data.Pois[new PoiId(_poi[npc])].Pos;

    /// <summary>이벤트 스트림을 닫는다.</summary>
    public ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    /// <summary>T1-47·T1-48 이 붙기 전까지 처리되지 않는 명령. 거절로 답한다.</summary>
    private void Unhandled(in NpcCommand command, Tick now)
    {
        if (Handler is { } handler && handler(in command, now))
        {
            return;
        }

        Fail(in command, now, ActionFailReason.Rejected);
    }

    /// <summary>
    /// 하위 시뮬이 명령을 가로채는 자리. 처리했으면 true 를 돌려준다.
    /// MovementSim·InteractionSim 이 여기에 붙는다.
    /// </summary>
    public CommandHandler? Handler { get; set; }

    /// <summary>명령 처리 위임.</summary>
    public delegate bool CommandHandler(in NpcCommand command, Tick now);
}
