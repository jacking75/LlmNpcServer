using Npc.Contracts;
using Npc.Core;

namespace Npc.Runtime;

/// <summary>
/// 스냅샷 그림자 버퍼 (A-01).
///
/// <b>기동 시 1회 할당한다.</b> 틱 루프는 여기에 <see cref="Array.Copy(Array, Array, int)"/> 만
/// 하므로 복사 틱의 힙 할당이 0 이다 (CLAUDE.md §2.1). 직렬화는 별도 스레드가 이 버퍼를 읽는다 —
/// 단일 생산자·단일 소비자이고, 소유권은 <see cref="SnapshotPort"/> 의 <c>ReadyTick</c> 이 가른다.
///
/// 복사량은 NPC 5,000 기준 핫 배열 ~100KB + 인벤토리 ~1.6MB + Recent ~1.3MB 다.
/// memcpy 는 그 크기에서 0.1~0.2ms 라 20ms 예산 안이다.
/// </summary>
public sealed class ShadowBuffer
{
    /// <summary>NPC 수만큼 잡는다. 기동 시 1회.</summary>
    /// <param name="capacity">NPC 수.</param>
    /// <param name="inventoryStride">인벤토리 칸 수.</param>
    /// <param name="zoneCapacity">존 상태 표 크기.</param>
    public ShadowBuffer(int capacity, int inventoryStride, int zoneCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inventoryStride);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(zoneCapacity);

        Count = capacity;
        InventoryStride = inventoryStride;

        Flags = new WorldFlags[capacity];
        PlanId = new int[capacity];
        StepIndex = new byte[capacity];
        StepStatus = new byte[capacity];
        StepIssuedTick = new long[capacity];
        Lod = new byte[capacity];
        Pos = new WorldPos[capacity];
        Hp = new short[capacity];
        Stamina = new short[capacity];
        ZoneCode = new ushort[capacity];
        ArchetypeCode = new ushort[capacity];
        CurrentPoi = new ushort[capacity];
        HostilePlayer = new int[capacity];
        Occupant = new int[capacity];
        Instance = new ushort[capacity];
        HomePoi = new ushort[capacity];
        WorkPoi = new ushort[capacity];
        Inventory = new int[capacity * inventoryStride];
        Recent = new RingBuffer8<RecentEvent>[capacity];
        PlanAssignedTick = new long[capacity];
        PendingUrgency = new byte[capacity];
        LastEventSequence = new long[capacity];
        LastFailReason = new byte[capacity];
        StepRetries = new byte[capacity];

        ZoneRegion = new byte[zoneCapacity];
        ZoneClimate = new byte[zoneCapacity];
    }

    /// <summary>NPC 수.</summary>
    public int Count { get; }

    /// <summary>인벤토리 칸 수.</summary>
    public int InventoryStride { get; }

    /// <summary>이 사본이 어느 틱의 것인가.</summary>
    public long Tick { get; set; }

    /// <summary>게임서버가 알려준 마지막 틱. 복원 후 시계가 여기서 이어진다.</summary>
    public long SyncedTick { get; set; }

    /// <summary>다음에 발급할 상관 ID.</summary>
    public uint NextCorrelation { get; set; }

    /// <summary>월드 플래그.</summary>
    public WorldFlags[] Flags { get; }

    /// <summary>현재 플랜.</summary>
    public int[] PlanId { get; }

    /// <summary>스텝 첨자.</summary>
    public byte[] StepIndex { get; }

    /// <summary>스텝 상태.</summary>
    public byte[] StepStatus { get; }

    /// <summary>스텝 발행 틱.</summary>
    public long[] StepIssuedTick { get; }

    /// <summary>인지 LOD.</summary>
    public byte[] Lod { get; }

    /// <summary>위치.</summary>
    public WorldPos[] Pos { get; }

    /// <summary>HP.</summary>
    public short[] Hp { get; }

    /// <summary>스태미나.</summary>
    public short[] Stamina { get; }

    /// <summary>존.</summary>
    public ushort[] ZoneCode { get; }

    /// <summary>아키타입.</summary>
    public ushort[] ArchetypeCode { get; }

    /// <summary>현재 POI.</summary>
    public ushort[] CurrentPoi { get; }

    /// <summary>최근 적대 플레이어 (B-06). 0 = 없음.</summary>
    public int[] HostilePlayer { get; }

    /// <summary>슬롯에 앉은 인스턴스 정의 id (B-05). 0 = 빈 슬롯.</summary>
    public int[] Occupant { get; }

    /// <summary>채널·인스턴스 던전·레이어 (B-02).</summary>
    public ushort[] Instance { get; }

    /// <summary>집 POI.</summary>
    public ushort[] HomePoi { get; }

    /// <summary>일터 POI.</summary>
    public ushort[] WorkPoi { get; }

    /// <summary>인벤토리 (평탄).</summary>
    public int[] Inventory { get; }

    /// <summary>구조화 기억.</summary>
    public RingBuffer8<RecentEvent>[] Recent { get; }

    /// <summary>플랜 배정 틱.</summary>
    public long[] PlanAssignedTick { get; }

    /// <summary>인터럽트 긴급도.</summary>
    public byte[] PendingUrgency { get; }

    /// <summary>마지막 처리 시퀀스.</summary>
    public long[] LastEventSequence { get; }

    /// <summary>마지막 실패 사유.</summary>
    public byte[] LastFailReason { get; }

    /// <summary>스텝 재시도 수.</summary>
    public byte[] StepRetries { get; }

    /// <summary>존별 지역 상태.</summary>
    public byte[] ZoneRegion { get; }

    /// <summary>존별 기후.</summary>
    public byte[] ZoneClimate { get; }
}

/// <summary>
/// 스냅샷 요청·완료 신호 (A-01).
///
/// <b><c>Npc.Runtime</c> 은 파일을 모른다.</b> 틱 루프는 "요청이 서 있으면 복사하고 완료를 알린다"
/// 까지만 하고, 그것을 파일로 쓰는 것은 호스트다 (CLAUDE.md §3 — Runtime 에 NuGet 을 넣지 않는다).
///
/// 락이 없다. 생산자(틱 루프)와 소비자(쓰기 스레드)가 하나씩이고 소유권은
/// <see cref="ReadyTick"/> 하나가 가른다 — 0 이면 틱 루프의 것, 0 이 아니면 쓰기 스레드의 것이다.
/// </summary>
public sealed class SnapshotPort
{
    private int _requested;
    private long _readyTick;

    /// <summary>그림자 버퍼. 기동 시 1회 할당된다.</summary>
    public required ShadowBuffer Buffer { get; init; }

    /// <summary>복사 대상 상태.</summary>
    public required NpcStore Store { get; init; }

    /// <summary>존별 상태. 스냅샷이 같이 담는다.</summary>
    public required ZoneStateTable Zones { get; init; }

    /// <summary>상관 ID 카운터. 복원 시 겹치지 않게 건너뛸 기준이다.</summary>
    public required CorrelationTable Correlations { get; init; }

    /// <summary>사본이 준비된 틱. 0 이면 준비된 사본이 없다.</summary>
    public long ReadyTick => Volatile.Read(ref _readyTick);

    /// <summary>복사 요청이 서 있는가.</summary>
    public bool Requested => Volatile.Read(ref _requested) != 0;

    /// <summary>복사 횟수. 계측용.</summary>
    public long Copies { get; private set; }

    /// <summary>다음 틱 경계에 복사해 달라고 요청한다. 쓰기 스레드가 부른다.</summary>
    public void Request() => Volatile.Write(ref _requested, 1);

    /// <summary>사본을 다 썼다고 알린다. 쓰기 스레드가 부른다.</summary>
    public void Release() => Volatile.Write(ref _readyTick, 0);

    /// <summary>
    /// 지금 당장 복사한다 (A-02 정상 종료).
    ///
    /// <b>틱 루프가 멈춘 뒤에만 부른다.</b> 요청 플래그도 소비 상태도 보지 않고 덮어쓰므로,
    /// 루프가 돌고 있으면 쓰기 스레드가 읽는 중인 버퍼를 뭉갠다. 종료 시퀀스는 틱 루프를
    /// 먼저 세우고 나서 이것을 부르기 때문에 안전하다 — 그 순서가 <see cref="HostShutdown"/> 의 규약이다.
    /// </summary>
    public void ForceCapture(long tick, long syncedTick)
    {
        Store.CopyTo(Buffer);
        Zones.CopyTo(Buffer.ZoneRegion, Buffer.ZoneClimate);

        Buffer.Tick = tick;
        Buffer.SyncedTick = syncedTick;
        Buffer.NextCorrelation = Correlations.NextId;

        Copies++;
        Volatile.Write(ref _requested, 0);
        Volatile.Write(ref _readyTick, tick == 0 ? 1 : tick);
    }

    /// <summary>
    /// 틱 경계에서 부른다. 요청이 서 있고 직전 사본이 소비됐으면 복사한다.
    /// <b>할당 0</b> — 그림자 버퍼는 기동 시 잡혀 있고 <see cref="Array.Copy(Array, Array, int)"/> 만 쓴다.
    /// </summary>
    /// <returns>이번 틱에 복사했으면 true.</returns>
    public bool TryCapture(long tick, long syncedTick)
    {
        if (Volatile.Read(ref _requested) == 0 || Volatile.Read(ref _readyTick) != 0)
        {
            return false;
        }

        Store.CopyTo(Buffer);
        Zones.CopyTo(Buffer.ZoneRegion, Buffer.ZoneClimate);

        Buffer.Tick = tick;
        Buffer.SyncedTick = syncedTick;
        Buffer.NextCorrelation = Correlations.NextId;

        Copies++;
        Volatile.Write(ref _requested, 0);
        Volatile.Write(ref _readyTick, tick);
        return true;
    }
}
