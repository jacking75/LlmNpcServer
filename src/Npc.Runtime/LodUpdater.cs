using Npc.Contracts;
using Npc.Core;

namespace Npc.Runtime;

/// <summary>
/// 인지 LOD 등급 산출. docs/11 §4 "LOD 등급 갱신".
///
/// <b>등급은 이벤트 수신 시에만 정한다.</b> 인지 스캔(<see cref="CognitionScheduler"/>)은
/// <c>NpcStore.Lod[]</c> 를 읽기만 한다 — 스캔 안에서 거리를 계산하면 NPC 5,000 에
/// 플레이어 20명이면 틱당 100,000회 거리 계산이고, 그것만으로 틱 예산이 날아간다.
/// 게임서버가 <c>PlayerProximity</c> 로 이미 계산해 보내 주는 값을 쓰는 것이 이 설계의 요점이다
/// (docs/02 §3.3).
///
/// <b>규칙이 둘이다</b> (docs/11 §4):
/// <list type="number">
///   <item><c>PlayerProximity</c> 거리별 등급 — <see cref="LodZeroDistance"/> · <see cref="LodOneDistance"/></item>
///   <item><c>ZoneStateChanged(War|Disaster)</c> — <b>그 존 전체를 LOD 1 로 승격</b></item>
/// </list>
///
/// <para>
/// <b>등급을 바꾸는 것과 밴드를 옮기는 것은 다른 일이다.</b> 여기서는 <c>Lod[]</c> 만 쓰고,
/// 밴드 멤버십은 <see cref="LodBandSet.Rebalance"/> 가 <b>틱당 64건</b>으로 나눠 따라온다.
/// 존 전체 승격이 한 틱에 밴드 배열을 다시 짜면 그 자체가 스파이크다 (docs/11 §4).
/// </para>
///
/// <b>할당 0.</b> 틱 루프의 이벤트 배수 구간에서 돈다 (CLAUDE.md §2.1).
/// </summary>
public sealed class LodUpdater
{
    /// <summary>이 거리(m) 안이면 LOD 0. docs/11 §4.</summary>
    public const int LodZeroDistance = 50;

    /// <summary>이 거리(m) 안이면 LOD 1. docs/11 §4.</summary>
    public const int LodOneDistance = 200;

    /// <summary>지역이 공격받는 존의 최저 등급. docs/11 §4.</summary>
    public const byte WarLod = 1;

    /// <summary>
    /// 플레이어가 떠난 뒤 <b>활성 존</b>의 등급. 비활성 존은 <see cref="NpcStore.InactiveLod"/> 다.
    ///
    /// 여기서 3 으로 내리지 않으면 한 번이라도 플레이어를 만난 NPC 가 영원히 스캔 대상으로 남고,
    /// NPC 를 늘릴수록 틱당 스캔이 선형으로 자란다 — <c>docs/11 §4</c> 의 O(1) 설계가 깨진다.
    /// </summary>
    public const byte ActiveZoneIdleLod = 2;

    private readonly NpcStore _store;

    /// <summary>갱신기를 만든다. 기동 시 1회.</summary>
    public LodUpdater(NpcStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    /// <summary>등급을 올린 횟수 (숫자가 작아진 것).</summary>
    public long Promotions { get; private set; }

    /// <summary>등급을 내린 횟수.</summary>
    public long Demotions { get; private set; }

    /// <summary>지역 상태 때문에 승격한 횟수. 공성 규모의 지표다.</summary>
    public long WarPromotions { get; private set; }

    /// <summary>
    /// 존 상태 표. 붙이면 플레이어가 떠날 때 <b>존 상태</b>로 활성 여부를 판정한다 (T4-13).
    /// 없으면 NPC 의 <c>RegionUnderAttack</c> 플래그로 판정한다 — 그것도 맞지만
    /// <c>Alert</c> 와 <c>Peace</c> 를 구분하지 못한다.
    /// </summary>
    public ZoneStateTable? ZoneStates { get; init; }

    /// <summary>거리(m) → 등급. docs/11 §4 의 세 구간.</summary>
    public static byte GradeOf(int distance) => distance switch
    {
        < LodZeroDistance => 0,
        < LodOneDistance => 1,
        _ => 2,
    };

    /// <summary>
    /// <c>PlayerProximity</c> 를 반영한다. <b>플래그는 바꾸지 않는다</b> —
    /// <c>PlayerNearby</c> 는 <see cref="EventApplier"/> 의 몫이다.
    /// </summary>
    /// <returns>등급이 바뀌었으면 true.</returns>
    public bool OnProximity(int npc, ProximityChange change, int distance)
    {
        if ((uint)npc >= (uint)_store.Count)
        {
            return false;
        }

        byte grade = change == ProximityChange.Leave
            ? IdleGradeOf(npc)
            : GradeOf(distance);

        return Set(npc, grade);
    }

    /// <summary>
    /// <c>ZoneStateChanged</c> 를 반영한다. 전쟁·재난이면 그 존 전체를 LOD 1 로 <b>승격만</b> 한다 —
    /// 이미 더 가까운 NPC(LOD 0)를 끌어내리지 않는다.
    /// </summary>
    /// <returns>승격한 NPC 수.</returns>
    public int OnZoneState(ZoneId zone, RegionState state)
    {
        if (state is not (RegionState.War or RegionState.Disaster))
        {
            return 0;
        }

        int promoted = 0;

        for (int npc = 0; npc < _store.Count; npc++)
        {
            if (_store.ZoneCode[npc] != zone.Value || _store.Lod[npc] <= WarLod)
            {
                continue;
            }

            _store.Lod[npc] = WarLod;
            Promotions++;
            WarPromotions++;
            promoted++;
        }

        return promoted;
    }

    /// <summary>
    /// 플레이어가 떠난 NPC 의 등급. 존이 활성이면 원거리(2), 평시면 비활성(3) 이다.
    /// </summary>
    private byte IdleGradeOf(int npc)
    {
        bool active = ZoneStates is { } states
            ? states.RegionOf(new ZoneId(_store.ZoneCode[npc])) is RegionState.War or RegionState.Disaster
            : (_store.Flags[npc] & WorldFlags.RegionUnderAttack) != 0;

        return active ? ActiveZoneIdleLod : NpcStore.InactiveLod;
    }

    private bool Set(int npc, byte grade)
    {
        byte current = _store.Lod[npc];

        if (current == grade)
        {
            return false;
        }

        _store.Lod[npc] = grade;

        if (grade < current)
        {
            Promotions++;
        }
        else
        {
            Demotions++;
        }

        return true;
    }
}
