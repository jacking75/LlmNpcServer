using Npc.Contracts;
using Npc.MasterData;

namespace Npc.Runtime;

/// <summary>
/// 런타임 스폰·디스폰을 받는 쪽 (B-05).
///
/// <para>
/// <b>슬롯 배정은 게임서버가 한다.</b> 우리는 "그 슬롯에 이 인스턴스를 앉혀라" 를 받아
/// 시드하고, 디스폰이 오면 비운다. 슬롯을 우리가 고르면 같은 슬롯 번호가 두 쪽에서 다른 NPC 를
/// 가리키게 되고, 그 순간 명령이 엉뚱한 NPC 에게 간다.
/// </para>
///
/// <para>
/// <b>용량은 기동 시 정해진다.</b> 틱 루프에서 배열을 늘릴 수 없으므로(CLAUDE.md §2.1)
/// <c>--npc-capacity</c> 만큼 미리 잡고, 넘으면 <b>무시하고 센다</b> — 조용히 넘기면
/// "게임서버에는 있는데 NPC 서버에는 없는 NPC" 가 생기고 그것은 아무 로그도 안 남긴다.
/// </para>
/// </summary>
public interface IDynamicRoster
{
    /// <summary>
    /// 빈 슬롯에 인스턴스를 앉힌다.
    /// </summary>
    /// <param name="slot">게임서버가 고른 슬롯.</param>
    /// <param name="instanceId"><c>npc_instances.json</c> 의 id. 0 이면 "모른다" 다.</param>
    /// <param name="now">현재 틱.</param>
    /// <returns>앉혔으면 true. 모르는 인스턴스거나 슬롯이 범위 밖이면 false.</returns>
    bool TryActivate(int slot, int instanceId, Tick now);

    /// <summary>슬롯을 비운다. 이미 비어 있으면 아무 일도 하지 않는다 (N7).</summary>
    /// <param name="slot">비울 슬롯.</param>
    /// <returns>실제로 비웠으면 true.</returns>
    bool Deactivate(int slot);
}

/// <summary>
/// <see cref="IDynamicRoster"/> 의 구현 (B-05).
///
/// <para>
/// <b>정적 시드와 같은 함수를 쓴다.</b> 기동 시 로스터를 앉히는 경로와 런타임 스폰 경로가
/// 갈라지면 "런타임에 스폰된 NPC 만 인벤토리가 비어 있다" 류의 버그가 생긴다 —
/// 둘 다 <see cref="EventApplier.Seed"/> + <see cref="PlanExecutor.AssignPlan"/> 를 지난다.
/// </para>
/// </summary>
public sealed class DynamicRoster : IDynamicRoster
{
    private readonly NpcInstanceTable _instances;
    private readonly NpcStore _store;
    private readonly EventApplier _applier;
    private readonly PlanExecutor _executor;
    private readonly int[] _fallbackOf;

    /// <summary>id → 인스턴스 첨자. <b>id 는 1 부터이고 연속이라고 가정하지 않는다.</b></summary>
    private readonly Dictionary<int, int> _byId;

    /// <summary>만든다. 기동 시 1회.</summary>
    /// <param name="instances">알려진 인스턴스 전부. <b>"누가 존재할 수 있는가" 다.</b></param>
    /// <param name="store">상태 저장소.</param>
    /// <param name="applier">시드에 쓴다.</param>
    /// <param name="executor">폴백 플랜 배정에 쓴다.</param>
    /// <param name="fallbackOf">아키타입 code → 폴백 플랜 id.</param>
    public DynamicRoster(
        NpcInstanceTable instances,
        NpcStore store,
        EventApplier applier,
        PlanExecutor executor,
        int[] fallbackOf)
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(applier);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(fallbackOf);

        _instances = instances;
        _store = store;
        _applier = applier;
        _executor = executor;
        _fallbackOf = fallbackOf;

        // 기동 시 1회 만든다. 틱 루프에서 Dictionary 를 순회하지 않는다 — 조회만 한다
        // (CLAUDE.md §2.1 이 금지한 것은 순회다. 순서 비결정성이 이유였다).
        _byId = new Dictionary<int, int>(instances.Count);

        for (int i = 0; i < instances.Count; i++)
        {
            _byId[instances[i].Id] = i;
        }
    }

    /// <summary>앉힌 횟수.</summary>
    public long Activated { get; private set; }

    /// <summary>비운 횟수.</summary>
    public long Deactivated { get; private set; }

    /// <summary>
    /// 거절한 횟수. <b>0 이 아니면 게임서버와 우리가 다른 세계를 보고 있다.</b>
    /// 용량 초과이거나 모르는 인스턴스다 — 둘 다 경보 대상이다.
    /// </summary>
    public long Rejected { get; private set; }

    /// <summary>이미 앉아 있어 아무 일도 안 한 횟수 (N7 멱등).</summary>
    public long AlreadyActive { get; private set; }

    /// <inheritdoc/>
    public bool TryActivate(int slot, int instanceId, Tick now)
    {
        if ((uint)slot >= (uint)_store.Count)
        {
            // 용량을 넘었다. 늘릴 수 없으므로 무시하고 센다.
            Rejected++;
            return false;
        }

        if (_store.Occupant[slot] == instanceId && instanceId != 0)
        {
            AlreadyActive++;
            return true;   // 멱등이다. 같은 스폰을 두 번 받아도 상태가 같다 (N7)
        }

        if (!_byId.TryGetValue(instanceId, out int index))
        {
            // 인스턴스 테이블에 없는 id 다. 1단계에서는 지원하지 않는다 —
            // 게임서버가 즉석에서 만든 NPC 는 아키타입을 알 방법이 없다.
            Rejected++;
            return false;
        }

        // 다른 인스턴스가 앉아 있으면 먼저 비운다. 안 그러면 인벤토리·플래그를 물려받는다.
        if (_store.Occupant[slot] != 0)
        {
            _store.ClearSlot(slot);
            Deactivated++;
        }

        NpcInstanceDef def = _instances[index];

        _applier.Seed(slot, def.Home, def.Zone, def.Archetype, def.Home, def.Workplace);

        _store.Occupant[slot] = def.Id;
        _store.StepStatus[slot] = (byte)StepStatus.Ready;
        _store.PlanAssignedTick[slot] = now.Value;

        _executor.AssignPlan(slot, new PlanId(_fallbackOf[def.Archetype.Value]));

        Activated++;

        return true;
    }

    /// <inheritdoc/>
    public bool Deactivate(int slot)
    {
        if ((uint)slot >= (uint)_store.Count || _store.Occupant[slot] == 0)
        {
            return false;   // 이미 비어 있다. 멱등이다 (N7)
        }

        _store.ClearSlot(slot);
        Deactivated++;

        return true;
    }
}
