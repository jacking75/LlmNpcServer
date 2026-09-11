using Npc.Contracts;
using Npc.Core;

namespace Npc.Runtime;

/// <summary>플랜 스텝의 실행 상태. docs/03 §6.</summary>
public enum StepStatus : byte
{
    /// <summary>발행 대기. 다음 틱에 명령이 나간다.</summary>
    Ready = 0,

    /// <summary>명령을 보내고 완료 이벤트를 기다리는 중.</summary>
    Waiting = 1,

    /// <summary>플랜이 끝났다 (loop=false).</summary>
    Done = 2,

    /// <summary>스폰 확인 전. 명령을 발행하지 않는다 (docs/02 §3.3).</summary>
    Unspawned = 3,

    /// <summary>완료 이벤트를 받았다. 다음 틱의 스텝 경계에서 전진한다.</summary>
    Completed = 4,

    /// <summary>실패 이벤트를 받았거나 타임아웃이 합성됐다. 스텝 경계에서 on_step_fail 정책을 적용한다.</summary>
    Failed = 5,
}

/// <summary>
/// 구조화 기억 한 줄. docs/11 §3 · 상위 계획 §4.1.
/// 자연어 요약이 아니라 값 타입이다 — 프롬프트 서픽스 300토큰 예산 안에 들어가야 한다.
/// </summary>
/// <param name="Kind">무슨 일이 있었나.</param>
/// <param name="At">언제.</param>
/// <param name="Subject">누가/무엇이 (NpcId·PlayerId·ItemId 등 Kind 에 따라 다르다).</param>
/// <param name="Salience">중요도. 낮은 것부터 밀려난다.</param>
public readonly record struct RecentEvent(GameEventKind Kind, Tick At, int Subject, byte Salience) : ISalient;

/// <summary>
/// NPC 상태의 SoA(struct of arrays) 저장소. docs/11 §3.
///
/// <b><c>class Npc</c> 를 5,000개 만들지 않는다.</b> 매 틱 전원을 훑으므로 캐시 지역성이 곧 성능이다.
/// 핫 배열만 합쳐 NPC 5,000 기준 ~115KB — L2 에 들어간다. 이게 틱 예산 20ms 를 지키는 근거다.
///
/// 인벤토리는 <c>int[][]</c> 대신 <b>평탄한 1차원 배열 + 스트라이드</b>다.
/// 5,000개의 작은 배열은 참조 추적이 늘고 캐시 라인이 흩어진다.
/// </summary>
public sealed class NpcStore
{
    /// <summary>NPC 수.</summary>
    public int Count { get; private set; }

    /// <summary>인벤토리 한 NPC 당 칸 수 (= 최대 item code + 1).</summary>
    public int InventoryStride { get; private set; }

    // --- 핫 (매 틱 접근) ---

    /// <summary>월드 상태 플래그. 8B × N.</summary>
    public WorldFlags[] Flags = [];

    /// <summary>현재 플랜. 4B × N.</summary>
    public int[] PlanId = [];

    /// <summary>플랜 안의 몇 번째 스텝인가. 스텝 상한이 10 이라 byte 로 충분하다.</summary>
    public byte[] StepIndex = [];

    /// <summary>스텝 실행 상태.</summary>
    public byte[] StepStatus = [];

    /// <summary>스텝 명령을 발행한 틱. 타임아웃 합성의 기준이다 (docs/02 §3.4).</summary>
    public long[] StepIssuedTick = [];

    /// <summary>인지 LOD 등급 0..3.</summary>
    public byte[] Lod = [];

    /// <summary>비활성 등급. 이벤트가 올 때만 본다 (docs/11 §4).</summary>
    public const byte InactiveLod = 3;

    // --- 웜 (이벤트 수신 시 갱신) ---

    /// <summary>위치.</summary>
    public WorldPos[] Pos = [];

    /// <summary>HP. IsInjured 판정의 입력.</summary>
    public short[] Hp = [];

    /// <summary>스태미나. IsExhausted 판정의 입력.</summary>
    public short[] Stamina = [];

    /// <summary>현재 존.</summary>
    public ushort[] ZoneCode = [];

    /// <summary>아키타입.</summary>
    public ushort[] ArchetypeCode = [];

    /// <summary>현재 POI. 거리 계산과 심볼 바인딩의 기준점이다.</summary>
    public ushort[] CurrentPoi = [];

    // --- 콜드 (재계획·바인딩 시에만) ---

    /// <summary>
    /// 최근에 적대로 판정된 플레이어 (B-06). <b>0 = 없음.</b>
    ///
    /// <para>
    /// <c>nearest:hostile_player</c> 바인딩이 읽는 값이다 — 인터럽트가 이 플레이어를
    /// <c>CombatAction.TargetPlayer</c> 로 찍는다.
    /// </para>
    ///
    /// <para>
    /// <b>참조 카운트를 세지 않는다.</b> 게임서버가 NPC 당 1건·에지 트리거로 보내므로
    /// "누군가 적대다" 하나만 알면 되고, 둘 중 하나가 떠난 판정은 게임서버가 소유한다
    /// (근접 규약과 같은 원칙).
    /// </para>
    /// </summary>
    public int[] HostilePlayer = [];

    /// <summary>
    /// 그 플레이어의 세력 code (B-02 · D-04). 0 = 게임서버가 안 알려 줬다.
    ///
    /// <b><see cref="HostilePlayer"/> 와 한 쌍이다.</b> <c>PlayerHostility</c> 이벤트가
    /// 같이 실어 주는 값이고, <c>CombatAction</c> 명령의 <c>Faction</c> 슬롯
    /// ("<b>대상</b>의 세력") 으로 그대로 나간다. 우리는 이 값으로 아무 판정도 하지 않는다.
    /// </summary>
    public ushort[] HostileFaction = [];

    /// <summary>
    /// 가장 최근에 이 NPC 와 무언가를 한 플레이어 (D-03). 0 = 없음.
    ///
    /// <para>
    /// <b>기억 저장소의 조회 키다.</b> 재계획 서픽스에 실을 <c>relationship_band</c> 는
    /// (NPC, 플레이어) 쌍에 달려 있는데, "어느 플레이어인가" 를 알 방법이 이것 말고 없다.
    /// </para>
    ///
    /// <para>
    /// <b><c>int</c> 하나인 것이 중요하다.</b> 재계획 워커가 다른 스레드에서 읽는다 —
    /// <c>Recent</c> 링 버퍼는 여러 필드라 찢어진 값을 볼 수 있지만 이 배열은 원자적이다.
    /// 한 틱 낡은 값을 봐도 밴드 판정이 흔들릴 뿐이다.
    /// </para>
    /// </summary>
    public int[] RecentPlayer = [];

    /// <summary>
    /// 이 슬롯에 앉은 인스턴스 정의 id (B-05). <b>0 = 빈 슬롯이다</b> —
    /// <c>npc_instances.json</c> 의 id 는 1 부터라 0 을 "없음" 으로 쓸 수 있다.
    ///
    /// <para>
    /// <b>슬롯과 인스턴스는 다른 것이다.</b> 슬롯은 배열 첨자이고 인스턴스는 "누구" 다.
    /// 정적 로스터에서는 둘이 1:1 로 붙어 있어 구분할 이유가 없었지만, 런타임 스폰·디스폰이
    /// 생기면 같은 슬롯에 다른 인스턴스가 앉을 수 있다 — 그때 이 배열이 없으면
    /// <b>이전 거주자의 인벤토리를 물려받은 NPC</b> 가 생긴다.
    /// </para>
    ///
    /// <para><b>정적 로스터 회차에서도 채운다.</b> 경로를 갈라 두면 한쪽만 나는 버그가 생긴다.</para>
    /// </summary>
    public int[] Occupant = [];

    /// <summary>
    /// 전역 id → 슬롯 (A-08). <see cref="Occupant"/> 의 역방향이다.
    ///
    /// <para>
    /// <b>와이어의 <c>NpcId</c> 는 전역 id 다</b> — <c>npc_instances.json</c> 의 <c>id</c>.
    /// 슬롯은 우리 안쪽 사정이라 게임서버가 알 이유가 없고, 존을 나눠 맡는 순간
    /// 양쪽의 슬롯 7번은 서로 다른 NPC 가 된다.
    /// </para>
    ///
    /// <para><b><see cref="Bind"/>·<see cref="ClearSlot"/> 로만 바꾼다.</b> 직접 쓰면 두 방향이 어긋난다.</para>
    /// </summary>
    public GlobalIdMap Ids { get; private set; } = new(0);

    /// <summary>
    /// 채널·인스턴스 던전·레이어 (B-02). 0 = 기본 월드.
    ///
    /// <b>게임서버가 정하고 우리는 되돌려 준다.</b> <c>NpcSpawned</c> 가 실어 주고,
    /// 이후 그 NPC 로 나가는 모든 명령에 그대로 찍힌다. NPC 서버는 이 값으로
    /// <b>아무 판단도 하지 않는다</b> — 무엇이 인스턴스인가는 게임서버의 개념이다.
    ///
    /// <b>콜드다.</b> 발행 경로에서만 읽으므로 핫 배열에 넣지 않는다.
    /// </summary>
    public ushort[] Instance = [];

    /// <summary>자택 POI.</summary>
    public ushort[] HomePoi = [];

    /// <summary>일터 POI. 0 이면 일터 없음.</summary>
    public ushort[] WorkPoi = [];

    /// <summary>
    /// 순찰 지점 (D-04). 첨자 = npc * <see cref="PatrolStride"/> + 순번. 0 이면 빈 칸.
    ///
    /// <b>콜드다.</b> 발행 경로에서만 읽으므로 핫 배열에 넣지 않는다.
    /// <b>스냅샷에 담지 않는다</b> — 상태가 아니라 설정이고, 설정의 출처는 마스터데이터다.
    /// </summary>
    public ushort[] PatrolRoute = [];

    /// <summary>이 NPC 의 순찰 지점 수 (D-04). 0 이면 순찰로 없음.</summary>
    public byte[] PatrolCount = [];

    /// <summary>
    /// 다음에 갈 순찰 지점의 순번 (D-04). <c>$patrol_route</c> 를 쓴 스텝을 낼 때마다 하나 나아간다.
    ///
    /// <para>
    /// <b>스텝 번호로는 안 된다.</b> 폴백 플랜은 <c>loop: true</c> 라 같은 스텝 번호가 영원히
    /// 돌아오고, 그러면 그 NPC 는 순찰로의 한 지점만 오간다 — "순찰" 이 아니라 "두 번째 일터" 다.
    /// </para>
    ///
    /// <para><b>난수가 아니다.</b> 순증 카운터라 리플레이가 일치한다 (CLAUDE.md §2.3).</para>
    /// </summary>
    public byte[] PatrolCursor = [];

    /// <summary>
    /// 세력 code (D-04). 0 = 미지정.
    ///
    /// <b>나가는 명령의 <c>Faction</c> 슬롯이 아니다.</b> 그 슬롯은 <b>대상</b>의 세력이고
    /// (<c>NpcCommand.Faction</c>), 이 배열은 이 NPC 자신의 세력이다.
    /// 읽는 것은 조회 API 와 앞으로의 대화 서비스(D-01)·기억 저장소(D-03)다.
    /// </summary>
    public ushort[] Faction = [];

    /// <summary>경계 반경 m (D-04). 0 이면 아키타입 기본값.</summary>
    public ushort[] AggroRadiusM = [];

    /// <summary>시간대 전환에 더할 게임 분 (D-04). 결정론 지터에 가산이다.</summary>
    public short[] ScheduleOffsetMin = [];

    /// <summary>NPC 당 순찰 지점 칸 수 (D-04). <c>NpcInstanceTable.MaxPatrolWaypoints</c> 와 같다.</summary>
    public const int PatrolStride = 4;

    /// <summary>인벤토리. 첨자 = npc * <see cref="InventoryStride"/> + itemCode.</summary>
    public int[] Inventory = [];

    /// <summary>구조화 기억.</summary>
    public RingBuffer8<RecentEvent>[] Recent = [];

    /// <summary>재계획 워커가 <c>Volatile.Write</c> 로 넣는 새 플랜. 0 이면 없음 (docs/03 §6).</summary>
    public int[] PendingPlanId = [];

    /// <summary>
    /// 지금 플랜을 배정한 틱. 재계획 점수의 "노후" 항이 본다 (docs/14 §2).
    ///
    /// <b>콜드 영역이다.</b> 재계획 경로에서만 읽으므로 핫 배열에 넣으면
    /// <see cref="HotBytesPerNpc"/> 가 8B 늘어 docs/11 §3 의 L2 목표가 깨진다.
    /// </summary>
    public long[] PlanAssignedTick = [];

    /// <summary>
    /// 인터럽트가 남긴 긴급도 0~100. 재계획 점수의 "긴급" 항이 본다 (docs/14 §2).
    ///
    /// <c>InterruptRule.Urgency</c> 가 0~100 이라 <see cref="byte"/> 로 충분하다.
    /// 새 플랜이 배정되면 소진된 것으로 보고 0 으로 되돌린다.
    /// </summary>
    public byte[] PendingUrgency = [];

    /// <summary>
    /// 마지막으로 처리한 이벤트 시퀀스. 멱등성 판정에 쓴다 (N7).
    /// NPC 단위가 아니라 링크 단위지만, NPC 별 중복 이벤트를 걸러야 해서 여기 둔다.
    /// </summary>
    public long[] LastEventSequence = [];

    /// <summary>마지막 스텝 실패 사유. on_step_fail 정책 적용과 로깅에 쓴다.</summary>
    public byte[] LastFailReason = [];

    /// <summary>현재 스텝을 몇 번 재시도했는가. on_step_fail = retry_once 가 본다.</summary>
    public byte[] StepRetries = [];

    /// <summary>핫 배열 한 NPC 당 바이트 수. docs/11 §3 의 ~100KB 근거.</summary>
    public const int HotBytesPerNpc =
        sizeof(ulong)    // Flags
        + sizeof(int)    // PlanId
        + sizeof(byte)   // StepIndex
        + sizeof(byte)   // StepStatus
        + sizeof(long)   // StepIssuedTick
        + sizeof(byte);  // Lod

    /// <summary>핫 배열 전체 크기(바이트).</summary>
    public long HotBytes => (long)HotBytesPerNpc * Flags.Length;

    /// <summary>
    /// 용량을 잡는다. 기동 시 1회. <b>이후 틱 루프에서 절대 재할당하지 않는다.</b>
    /// </summary>
    /// <param name="capacity">NPC 수.</param>
    /// <param name="inventoryStride">아이템 code 최대값 + 1.</param>
    /// <param name="maxGlobalId">
    /// 받아 줄 수 있는 가장 큰 전역 id (A-08). 보통 <c>npc_instances.json</c> 의 최대 id 다.
    /// 0 이면 <paramref name="capacity"/> 로 잡는다 — 슬롯 하나당 전역 id 하나인 회차다.
    /// </param>
    public void Allocate(int capacity, int inventoryStride, int maxGlobalId = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inventoryStride);
        ArgumentOutOfRangeException.ThrowIfNegative(maxGlobalId);

        Count = capacity;
        InventoryStride = inventoryStride;
        Ids = new GlobalIdMap(maxGlobalId > 0 ? maxGlobalId : capacity);

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
        HostileFaction = new ushort[capacity];
        RecentPlayer = new int[capacity];
        Occupant = new int[capacity];
        Instance = new ushort[capacity];
        HomePoi = new ushort[capacity];
        WorkPoi = new ushort[capacity];
        PatrolRoute = new ushort[capacity * PatrolStride];
        PatrolCount = new byte[capacity];
        PatrolCursor = new byte[capacity];
        Faction = new ushort[capacity];
        AggroRadiusM = new ushort[capacity];
        ScheduleOffsetMin = new short[capacity];
        Inventory = new int[(long)capacity * inventoryStride <= int.MaxValue
            ? capacity * inventoryStride
            : throw new ArgumentOutOfRangeException(nameof(capacity), "인벤토리 배열이 int 범위를 넘는다.")];
        Recent = new RingBuffer8<RecentEvent>[capacity];
        PendingPlanId = new int[capacity];
        PlanAssignedTick = new long[capacity];
        PendingUrgency = new byte[capacity];
        LastEventSequence = new long[capacity];
        LastFailReason = new byte[capacity];
        StepRetries = new byte[capacity];

        for (int i = 0; i < capacity; i++)
        {
            StepStatus[i] = (byte)Runtime.StepStatus.Unspawned;
            Hp[i] = 100;
            Stamina[i] = 100;
            LastEventSequence[i] = -1;

            // 초기 등급은 비활성이다. 플레이어가 다가와야 승격된다 (docs/11 §4).
            // 0 으로 두면 5,000마리가 전부 매 틱 판정 대상이 되어 틱 예산이 통째로 날아간다.
            Lod[i] = InactiveLod;
        }
    }

    /// <summary>
    /// 상태를 그림자 버퍼로 복사한다 (A-01). <b>틱 경계에서 부른다.</b>
    ///
    /// <see cref="Array.Copy(Array, Array, int)"/> 만 쓴다 — 할당 0. 복사 대상은
    /// <see cref="StateHash"/> 가 세는 집합에 <c>Recent</c>·바인딩 값·재계획 보조값을 더한 것이다.
    ///
    /// <b>담지 않는 것</b>: <c>PendingPlanId</c>(워커가 다시 건다) · LOD 밴드 멤버십
    /// (<see cref="Lod"/> 값으로 재구성한다) · 재계획 큐(인지 스캔이 재구성한다).
    /// </summary>
    public void CopyTo(ShadowBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (buffer.Count != Count || buffer.InventoryStride != InventoryStride)
        {
            throw new ArgumentException(
                $"그림자 버퍼 모양이 다르다: {buffer.Count}×{buffer.InventoryStride} "
                + $"vs {Count}×{InventoryStride}",
                nameof(buffer));
        }

        Array.Copy(Flags, buffer.Flags, Count);
        Array.Copy(PlanId, buffer.PlanId, Count);
        Array.Copy(StepIndex, buffer.StepIndex, Count);
        Array.Copy(StepStatus, buffer.StepStatus, Count);
        Array.Copy(StepIssuedTick, buffer.StepIssuedTick, Count);
        Array.Copy(Lod, buffer.Lod, Count);
        Array.Copy(Pos, buffer.Pos, Count);
        Array.Copy(Hp, buffer.Hp, Count);
        Array.Copy(Stamina, buffer.Stamina, Count);
        Array.Copy(ZoneCode, buffer.ZoneCode, Count);
        Array.Copy(ArchetypeCode, buffer.ArchetypeCode, Count);
        Array.Copy(CurrentPoi, buffer.CurrentPoi, Count);
        Array.Copy(HostilePlayer, buffer.HostilePlayer, Count);
        Array.Copy(HostileFaction, buffer.HostileFaction, Count);
        Array.Copy(RecentPlayer, buffer.RecentPlayer, Count);
        Array.Copy(PatrolCursor, buffer.PatrolCursor, Count);
        Array.Copy(Occupant, buffer.Occupant, Count);
        Array.Copy(Instance, buffer.Instance, Count);
        Array.Copy(HomePoi, buffer.HomePoi, Count);
        Array.Copy(WorkPoi, buffer.WorkPoi, Count);
        Array.Copy(Inventory, buffer.Inventory, Count * InventoryStride);
        Array.Copy(Recent, buffer.Recent, Count);
        Array.Copy(PlanAssignedTick, buffer.PlanAssignedTick, Count);
        Array.Copy(PendingUrgency, buffer.PendingUrgency, Count);
        Array.Copy(LastEventSequence, buffer.LastEventSequence, Count);
        Array.Copy(LastFailReason, buffer.LastFailReason, Count);
        Array.Copy(StepRetries, buffer.StepRetries, Count);
    }

    /// <summary>
    /// 그림자 버퍼에서 상태를 되돌린다 (A-01). <b>기동 중에만 부른다</b> — 틱 루프가 돌기 전이다.
    ///
    /// <c>Waiting</c> 이던 스텝은 <c>Ready</c> 로 되돌린다. 크래시 시점에 나가 있던 명령의
    /// 응답 이벤트는 영영 오지 않으므로 그대로 두면 <c>timeout_s</c> 를 다 기다린 뒤에야 움직인다.
    /// 재발행은 게임서버 관점에서 멱등이다 (N7).
    /// </summary>
    public void LoadFrom(ShadowBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (buffer.Count != Count || buffer.InventoryStride != InventoryStride)
        {
            throw new ArgumentException(
                $"그림자 버퍼 모양이 다르다: {buffer.Count}×{buffer.InventoryStride} "
                + $"vs {Count}×{InventoryStride}",
                nameof(buffer));
        }

        Array.Copy(buffer.Flags, Flags, Count);
        Array.Copy(buffer.PlanId, PlanId, Count);
        Array.Copy(buffer.StepIndex, StepIndex, Count);
        Array.Copy(buffer.StepStatus, StepStatus, Count);
        Array.Copy(buffer.StepIssuedTick, StepIssuedTick, Count);
        Array.Copy(buffer.Lod, Lod, Count);
        Array.Copy(buffer.Pos, Pos, Count);
        Array.Copy(buffer.Hp, Hp, Count);
        Array.Copy(buffer.Stamina, Stamina, Count);
        Array.Copy(buffer.ZoneCode, ZoneCode, Count);
        Array.Copy(buffer.ArchetypeCode, ArchetypeCode, Count);
        Array.Copy(buffer.CurrentPoi, CurrentPoi, Count);
        Array.Copy(buffer.HostilePlayer, HostilePlayer, Count);
        Array.Copy(buffer.HostileFaction, HostileFaction, Count);
        Array.Copy(buffer.RecentPlayer, RecentPlayer, Count);
        Array.Copy(buffer.PatrolCursor, PatrolCursor, Count);
        Array.Copy(buffer.Occupant, Occupant, Count);
        Array.Copy(buffer.Instance, Instance, Count);
        Array.Copy(buffer.HomePoi, HomePoi, Count);
        Array.Copy(buffer.WorkPoi, WorkPoi, Count);
        Array.Copy(buffer.Inventory, Inventory, Count * InventoryStride);
        Array.Copy(buffer.Recent, Recent, Count);
        Array.Copy(buffer.PlanAssignedTick, PlanAssignedTick, Count);
        Array.Copy(buffer.PendingUrgency, PendingUrgency, Count);
        Array.Copy(buffer.LastEventSequence, LastEventSequence, Count);
        Array.Copy(buffer.LastFailReason, LastFailReason, Count);
        Array.Copy(buffer.StepRetries, StepRetries, Count);

        Array.Clear(PendingPlanId);

        // A-08 — 전역 id 역방향 표는 파생물이라 스냅샷에 담지 않는다. 담으면 Occupant 와
        // 어긋난 스냅샷이 존재할 수 있게 된다. 여기서 다시 세운다.
        RebuildIds();
    }

    /// <summary>
    /// 진행 중이던 스텝을 재발행 대기로 되돌린다 (A-01 복원 5단계).
    /// </summary>
    /// <returns>되돌린 수.</returns>
    public int ReissueWaitingSteps()
    {
        int reissued = 0;

        for (int i = 0; i < Count; i++)
        {
            if (StepStatus[i] == (byte)Runtime.StepStatus.Waiting)
            {
                StepStatus[i] = (byte)Runtime.StepStatus.Ready;
                StepIssuedTick[i] = 0;
                reissued++;
            }
        }

        return reissued;
    }

    /// <summary>한 NPC 의 인벤토리. 할당 0.</summary>
    public Span<int> InventoryOf(int npc) =>
        Inventory.AsSpan(npc * InventoryStride, InventoryStride);

    /// <summary>한 NPC 의 순찰 지점 칸 (D-04). 할당 0.</summary>
    /// <param name="npc">슬롯.</param>
    public Span<ushort> PatrolRouteOf(int npc) =>
        PatrolRoute.AsSpan(npc * PatrolStride, PatrolStride);

    /// <summary>
    /// 이 NPC 가 지금 갈 순찰 지점 (D-04). 순찰로가 없으면 0 이다 —
    /// 그때는 바인더가 일터로 떨어뜨린다.
    /// </summary>
    /// <param name="npc">슬롯.</param>
    public PoiId PatrolPointOf(int npc)
    {
        int count = PatrolCount[npc];

        if (count == 0)
        {
            return default;
        }

        return new PoiId(PatrolRoute[(npc * PatrolStride) + (PatrolCursor[npc] % count)]);
    }

    /// <summary>
    /// 순찰로를 한 지점 나아간다 (D-04). <c>$patrol_route</c> 를 쓴 스텝을 낸 직후에 부른다.
    ///
    /// <para>
    /// <b>지점 수로 나눈 나머지를 저장한다.</b> 순증만 시키면 <c>byte</c> 가 256 에서 돌아
    /// 지점 수가 256 의 약수가 아닐 때 순서가 한 번 튄다 — 3지점 순찰로가 그렇다.
    /// </para>
    /// </summary>
    /// <param name="npc">슬롯.</param>
    public void AdvancePatrol(int npc)
    {
        int count = PatrolCount[npc];

        if (count != 0)
        {
            PatrolCursor[npc] = (byte)((PatrolCursor[npc] + 1) % count);
        }
    }

    /// <summary>이 슬롯에 인스턴스가 앉아 있는가 (B-05).</summary>
    public bool IsOccupied(int slot) => (uint)slot < (uint)Count && Occupant[slot] != 0;

    /// <summary>
    /// 슬롯에 전역 id 를 묶는다 (A-08). <b>두 방향을 같이 쓴다.</b>
    ///
    /// 이미 다른 id 가 앉아 있으면 그쪽을 먼저 푼다 — 안 그러면 옛 id 로 온 이벤트가
    /// 새 거주자에게 간다.
    /// </summary>
    /// <param name="slot">슬롯.</param>
    /// <param name="globalId">전역 id. 0 은 "없음" 이라 묶을 수 없다.</param>
    /// <returns>묶었으면 true.</returns>
    public bool Bind(int slot, int globalId)
    {
        if ((uint)slot >= (uint)Count)
        {
            return false;
        }

        if (Occupant[slot] != 0 && Occupant[slot] != globalId)
        {
            Ids.Unbind(Occupant[slot]);
        }

        if (!Ids.Bind(globalId, slot))
        {
            return false;
        }

        Occupant[slot] = globalId;

        return true;
    }

    /// <summary>
    /// 전역 id 의 슬롯 (A-08). 모르면 <see cref="GlobalIdMap.NotFound"/>.
    /// <b>할당 0</b> — 이벤트 배수 구간에서 이벤트마다 돈다.
    /// </summary>
    /// <param name="globalId">전역 id.</param>
    public int SlotOf(int globalId) => Ids.SlotOf(globalId);

    /// <summary>
    /// 이 슬롯의 전역 id (A-08). 비어 있으면 0.
    /// <b>명령 발행 경로가 매 스텝 부른다.</b>
    /// </summary>
    /// <param name="slot">슬롯.</param>
    public int GlobalOf(int slot) => (uint)slot < (uint)Count ? Occupant[slot] : 0;

    /// <summary>
    /// <see cref="Occupant"/> 에서 역방향 표를 다시 세운다 (A-08).
    ///
    /// <b>스냅샷 복원이 부른다.</b> 스냅샷은 <see cref="Occupant"/> 를 담지만 역방향은
    /// 파생물이라 담지 않는다 — 담으면 둘이 어긋난 스냅샷이 존재할 수 있게 된다.
    /// </summary>
    public void RebuildIds()
    {
        Ids.Clear();

        for (int slot = 0; slot < Count; slot++)
        {
            if (Occupant[slot] != 0)
            {
                Ids.Bind(Occupant[slot], slot);
            }
        }
    }

    /// <summary>
    /// 비어 있는 첫 슬롯 (A-08). 없으면 -1.
    ///
    /// <b>슬롯 배정은 우리 몫이다.</b> 게임서버는 전역 id 만 말하고, 그것을 어느 배열 칸에
    /// 앉힐지는 이쪽 사정이다 — 게임서버가 우리 첨자를 고르면 샤드마다 다른 첨자 공간을
    /// 게임서버가 관리해야 한다.
    /// </summary>
    public int FreeSlot()
    {
        for (int slot = 0; slot < Count; slot++)
        {
            if (Occupant[slot] == 0)
            {
                return slot;
            }
        }

        return -1;
    }

    /// <summary>지금 앉아 있는 슬롯 수 (B-05). 메트릭용이다 — 틱 루프에서 부르지 않는다.</summary>
    public int OccupiedSlots()
    {
        int count = 0;

        for (int i = 0; i < Count; i++)
        {
            if (Occupant[i] != 0)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// 슬롯을 비운다 (B-05). <b>다음 거주자가 이전 거주자의 상태를 물려받지 않게</b> 한다.
    ///
    /// <para>
    /// <see cref="Allocate"/> 직후와 같은 값으로 되돌린다 — 그렇게 하지 않으면
    /// "스폰 순서에 따라 다르게 행동하는 NPC" 가 생기고, 그것은 재현이 거의 불가능하다.
    /// </para>
    ///
    /// <para><b>틱 루프에서 부를 수 있다.</b> 할당이 없다 — 쓰기만 한다.</para>
    /// </summary>
    /// <param name="slot">비울 슬롯.</param>
    public void ClearSlot(int slot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, Count);

        Ids.Unbind(Occupant[slot]);
        Occupant[slot] = 0;
        HostilePlayer[slot] = 0;
        HostileFaction[slot] = 0;
        RecentPlayer[slot] = 0;
        Flags[slot] = default;
        PlanId[slot] = 0;
        StepIndex[slot] = 0;
        StepStatus[slot] = (byte)Runtime.StepStatus.Unspawned;
        StepIssuedTick[slot] = 0;
        Lod[slot] = InactiveLod;
        Pos[slot] = default;
        Hp[slot] = 100;
        Stamina[slot] = 100;
        ZoneCode[slot] = 0;
        ArchetypeCode[slot] = 0;
        CurrentPoi[slot] = 0;
        Instance[slot] = 0;
        HomePoi[slot] = 0;
        WorkPoi[slot] = 0;
        PatrolRouteOf(slot).Clear();
        PatrolCount[slot] = 0;
        PatrolCursor[slot] = 0;
        Faction[slot] = 0;
        AggroRadiusM[slot] = 0;
        ScheduleOffsetMin[slot] = 0;
        Recent[slot] = default;
        PendingPlanId[slot] = 0;
        PlanAssignedTick[slot] = 0;
        PendingUrgency[slot] = 0;
        LastFailReason[slot] = 0;
        StepRetries[slot] = 0;

        // 시퀀스는 되돌리지 않는다 — 링크는 프로세스 수명 동안 순증하므로(N6) 0 으로 되돌리면
        // 재스폰 뒤의 이벤트가 전부 "중복" 으로 읽힌다.
        Inventory.AsSpan(slot * InventoryStride, InventoryStride).Clear();
    }

    /// <summary>한 NPC 의 인벤토리 (읽기 전용).</summary>
    public ReadOnlySpan<int> ReadInventoryOf(int npc) =>
        Inventory.AsSpan(npc * InventoryStride, InventoryStride);

    /// <summary>
    /// 결정론 상태 해시. 멱등성 테스트(N7)와 리플레이 일치 검사가 쓴다.
    /// 부동소수 좌표는 비트 패턴 그대로 섞는다 — 반올림 차이를 놓치지 않기 위해서다.
    /// </summary>
    public ulong StateHash()
    {
        ulong hash = 1469598103934665603UL;   // FNV-1a 64 offset basis

        for (int i = 0; i < Count; i++)
        {
            hash = Mix(hash, (ulong)Flags[i]);
            hash = Mix(hash, (ulong)PlanId[i]);
            hash = Mix(hash, StepIndex[i]);
            hash = Mix(hash, StepStatus[i]);
            hash = Mix(hash, (ulong)StepIssuedTick[i]);
            hash = Mix(hash, Lod[i]);
            hash = Mix(hash, (ulong)(uint)BitConverter.SingleToInt32Bits(Pos[i].X));
            hash = Mix(hash, (ulong)(uint)BitConverter.SingleToInt32Bits(Pos[i].Y));
            hash = Mix(hash, (ulong)(uint)BitConverter.SingleToInt32Bits(Pos[i].Z));
            hash = Mix(hash, (ulong)(ushort)Hp[i]);
            hash = Mix(hash, (ulong)(ushort)Stamina[i]);
            hash = Mix(hash, ZoneCode[i]);
            hash = Mix(hash, CurrentPoi[i]);
            hash = Mix(hash, (ulong)(uint)HostilePlayer[i]);
            hash = Mix(hash, HostileFaction[i]);
            hash = Mix(hash, (ulong)(uint)RecentPlayer[i]);
            hash = Mix(hash, PatrolCursor[i]);
            hash = Mix(hash, (ulong)(uint)Occupant[i]);
            hash = Mix(hash, Instance[i]);

            ReadOnlySpan<int> inventory = ReadInventoryOf(i);
            for (int slot = 0; slot < inventory.Length; slot++)
            {
                if (inventory[slot] != 0)
                {
                    hash = Mix(hash, (ulong)slot);
                    hash = Mix(hash, (ulong)inventory[slot]);
                }
            }
        }

        return hash;

        static ulong Mix(ulong hash, ulong value)
        {
            hash ^= value;
            return hash * 1099511628211UL;   // FNV-1a 64 prime
        }
    }
}
