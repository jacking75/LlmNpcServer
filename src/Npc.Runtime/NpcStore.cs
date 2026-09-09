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

    /// <summary>자택 POI.</summary>
    public ushort[] HomePoi = [];

    /// <summary>일터 POI. 0 이면 일터 없음.</summary>
    public ushort[] WorkPoi = [];

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
    public void Allocate(int capacity, int inventoryStride)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inventoryStride);

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

        HomePoi = new ushort[capacity];
        WorkPoi = new ushort[capacity];
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
