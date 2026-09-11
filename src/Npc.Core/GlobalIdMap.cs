namespace Npc.Core;

/// <summary>
/// 전역 NPC id → 로컬 슬롯 (A-08).
///
/// <para>
/// <b>왜 있는가.</b> 와이어의 <c>NpcId</c> 는 <c>npc_instances.json</c> 의 <c>id</c> 다 —
/// 그것이 계약이 처음부터 적어 둔 의미다. 반면 런타임의 SoA 배열 첨자는 <b>슬롯</b>이고,
/// 슬롯은 프로세스마다·회차마다 다르다. 두 NPC 서버가 존을 나눠 맡으면 양쪽의 슬롯 7번은
/// 서로 다른 NPC 이고, 그 상태로 명령이 오가면 <b>대장장이에게 밭을 갈라고 명령</b>하게 된다.
/// </para>
///
/// <para>
/// <b>역방향만 들고 있다.</b> 슬롯 → 전역은 소유자가 이미 배열로 갖고 있다
/// (<c>NpcStore.Occupant</c> · <c>SimWorld.DefinitionOf</c>). 여기서 또 들면 두 배열이
/// 어긋나는 날이 오고, 그날 디버깅은 매우 어렵다.
/// </para>
///
/// <para>
/// <b>기동 시 한 번 잡고 자라지 않는다.</b> 조회는 틱 루프의 이벤트 배수 구간에서 도는데
/// 그 안에서 배열을 늘릴 수 없다 (CLAUDE.md §2.1). 용량 밖 id 는 <see cref="NotFound"/> 이고,
/// 부르는 쪽이 그것을 세서 경보한다 — 조용히 넘기면 "게임서버에는 있는데 우리에겐 없는 NPC" 가
/// 아무 흔적도 없이 생긴다.
/// </para>
/// </summary>
public sealed class GlobalIdMap
{
    /// <summary>모르는 전역 id. 슬롯 첨자와 절대 겹치지 않는 값이어야 한다.</summary>
    public const int NotFound = -1;

    private readonly int[] _slotOf;

    /// <summary>
    /// 만든다. 기동 시 1회.
    /// </summary>
    /// <param name="maxGlobalId">
    /// 받아 줄 수 있는 가장 큰 전역 id. 보통 <c>npc_instances.json</c> 의 최대 id 다.
    /// </param>
    public GlobalIdMap(int maxGlobalId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxGlobalId);

        _slotOf = new int[maxGlobalId + 1];

        Clear();
    }

    /// <summary>받아 줄 수 있는 가장 큰 전역 id.</summary>
    public int MaxGlobalId => _slotOf.Length - 1;

    /// <summary>지금 묶여 있는 수.</summary>
    public int Bound { get; private set; }

    /// <summary>
    /// 전역 id 의 슬롯. 모르면 <see cref="NotFound"/>. <b>할당 0 · 분기 둘.</b>
    /// </summary>
    /// <param name="globalId">전역 id.</param>
    public int SlotOf(int globalId) =>
        (uint)globalId < (uint)_slotOf.Length ? _slotOf[globalId] : NotFound;

    /// <summary>묶는다. 이미 그 id 가 다른 슬롯에 있으면 새 슬롯으로 옮긴다.</summary>
    /// <param name="globalId">전역 id. 0 은 "없음" 이라 묶을 수 없다.</param>
    /// <param name="slot">슬롯.</param>
    /// <returns>묶었으면 true. 용량 밖이거나 id 가 0 이면 false.</returns>
    public bool Bind(int globalId, int slot)
    {
        if ((uint)globalId >= (uint)_slotOf.Length || globalId == 0 || slot < 0)
        {
            return false;
        }

        if (_slotOf[globalId] == NotFound)
        {
            Bound++;
        }

        _slotOf[globalId] = slot;

        return true;
    }

    /// <summary>푼다. 이미 없으면 아무 일도 하지 않는다 (N7 멱등).</summary>
    /// <param name="globalId">전역 id.</param>
    /// <returns>실제로 풀었으면 true.</returns>
    public bool Unbind(int globalId)
    {
        if ((uint)globalId >= (uint)_slotOf.Length || _slotOf[globalId] == NotFound)
        {
            return false;
        }

        _slotOf[globalId] = NotFound;
        Bound--;

        return true;
    }

    /// <summary>전부 푼다. 스냅샷 복원이 표를 다시 세울 때 쓴다.</summary>
    public void Clear()
    {
        Array.Fill(_slotOf, NotFound);
        Bound = 0;
    }
}
