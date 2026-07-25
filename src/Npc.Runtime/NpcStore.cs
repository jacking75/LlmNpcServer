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
