using Npc.Contracts;
using Npc.Core;
using Npc.MasterData;

namespace Npc.Runtime;

/// <summary>
/// 존별 지역 상태·기후. docs/14 §5 · docs/01 §6.
///
/// <b>플래그만으로는 버킷 키를 복원할 수 없다.</b> <c>context_buckets.json</c> 이
/// <c>Peace</c>·<c>Alert</c> 에 같은 <c>RegionPeaceful</c> 을, <c>War</c>·<c>Disaster</c> 에 같은
/// <c>RegionUnderAttack</c> 을, <c>Fair</c>·<c>Cold</c> 에 둘 다 "플래그 없음" 을 준다.
/// 정보는 <c>ZoneStateChanged.Code</c>·<c>WeatherChanged.Code</c> 로 들어오는데
/// <c>EventApplier</c> 가 플래그로 접어 버리면서 사라진다.
///
/// 그래서 <b>들어온 값을 존 단위로 그대로 보관한다.</b> 버킷 키 4차원 중 두 개가 여기서 나오고,
/// 그것이 프리베이크한 버킷을 실제로 찾아 쓰는 유일한 방법이다 (P4 게이트 히트율 98%).
///
/// 배열 둘뿐이고 첨자는 zone code 다. <b>할당 0</b> — 틱 루프의 이벤트 배수 구간에서 쓰인다.
/// </summary>
public sealed class ZoneStateTable
{
    private readonly RegionState[] _region;
    private readonly Climate[] _climate;

    /// <summary>표를 만든다. 기동 시 1회. 전 존이 <c>Peace</c>·<c>Fair</c> 로 시작한다.</summary>
    public ZoneStateTable(MasterDataSet data)
    {
        ArgumentNullException.ThrowIfNull(data);

        int capacity = 1;

        foreach (ZoneDef zone in data.Zones.Zones)
        {
            if (zone.Code.Value + 1 > capacity)
            {
                capacity = zone.Code.Value + 1;
            }
        }

        _region = new RegionState[capacity];
        _climate = new Climate[capacity];
        Capacity = capacity;
    }

    /// <summary>표 크기 (= 최대 zone code + 1).</summary>
    public int Capacity { get; }

    /// <summary>상태가 바뀐 횟수. 전환 계기의 수와 같아야 한다.</summary>
    public long Changes { get; private set; }

    /// <summary>이 존의 지역 상태. 모르는 존은 <see cref="RegionState.Peace"/>.</summary>
    public RegionState RegionOf(ZoneId zone) =>
        (uint)zone.Value < (uint)_region.Length ? _region[zone.Value] : RegionState.Peace;

    /// <summary>이 존의 기후. 모르는 존은 <see cref="Climate.Fair"/>.</summary>
    public Climate ClimateOf(ZoneId zone) =>
        (uint)zone.Value < (uint)_climate.Length ? _climate[zone.Value] : Climate.Fair;

    /// <summary>지역 상태를 기록한다. <b>바뀌었으면 true</b> — 전환 예약의 계기다.</summary>
    public bool Set(ZoneId zone, RegionState state)
    {
        if ((uint)zone.Value >= (uint)_region.Length || _region[zone.Value] == state)
        {
            return false;
        }

        _region[zone.Value] = state;
        Changes++;
        return true;
    }

    /// <summary>기후를 기록한다. <b>바뀌었으면 true</b>.</summary>
    public bool Set(ZoneId zone, Climate climate)
    {
        if ((uint)zone.Value >= (uint)_climate.Length || _climate[zone.Value] == climate)
        {
            return false;
        }

        _climate[zone.Value] = climate;
        Changes++;
        return true;
    }
}
