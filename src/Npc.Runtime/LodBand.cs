namespace Npc.Runtime;

/// <summary>인지 LOD 밴드 하나. docs/11 §4.</summary>
/// <param name="Lod">등급 0~3.</param>
/// <param name="Period">몇 틱마다 한 바퀴 도는가. 0 이면 이벤트 시에만 본다.</param>
/// <param name="Name">사람이 읽는 이름.</param>
public readonly record struct LodBand(int Lod, int Period, string Name);

/// <summary>
/// LOD 밴드 멤버십. docs/11 §4.
///
/// 등급은 <c>PlayerProximity</c> 이벤트로만 바뀐다 — <b>스캔 안에서 거리를 계산하지 않는다.</b>
/// 등급이 바뀌면 밴드 멤버십 배열을 갱신해야 하는데, 매 틱 재구축하면 안 되므로
/// <b>틱당 최대 64건만 이동</b>하고 나머지는 다음 틱으로 넘긴다.
///
/// 배열만 쓰고 할당이 0 이다. 이동은 swap-remove 라 순서가 결정론적이다.
/// </summary>
public sealed class LodBandSet
{
    /// <summary>틱당 최대 밴드 이동 수. docs/11 §4.</summary>
    public const int MaxBandMigrationsPerTick = 64;

    /// <summary>밴드 수.</summary>
    public const int BandCount = 4;

    /// <summary>docs/11 §4 의 밴드 정의.</summary>
    public static readonly LodBand[] Bands =
    [
        new(Lod: 0, Period: 1, Name: "시야내"),
        new(Lod: 1, Period: 10, Name: "동일존"),
        new(Lod: 2, Period: 100, Name: "원거리"),
        new(Lod: 3, Period: 0, Name: "비활성"),
    ];

    private readonly NpcStore _store;
    private readonly int[][] _members;    // [lod][slot] = npc
    private readonly int[] _counts;       // [lod]
    private readonly int[] _bandOf;       // [npc] = 현재 소속 밴드
    private readonly int[] _slotOf;       // [npc] = 밴드 배열 안 위치
    private int _cursor;

    /// <summary>밴드 집합을 만든다. 기동 시 1회. 전원이 초기 등급의 밴드에 들어간다.</summary>
    public LodBandSet(NpcStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        _members = new int[BandCount][];
        _counts = new int[BandCount];
        _bandOf = new int[store.Count];
        _slotOf = new int[store.Count];

        for (int band = 0; band < BandCount; band++)
        {
            _members[band] = new int[store.Count];
        }

        for (int npc = 0; npc < store.Count; npc++)
        {
            int band = Clamp(store.Lod[npc]);
            _bandOf[npc] = band;
            _slotOf[npc] = _counts[band];
            _members[band][_counts[band]++] = npc;
        }
    }

    /// <summary>지금까지 이동한 총 건수.</summary>
    public long Migrations { get; private set; }

    /// <summary>이 밴드의 인원.</summary>
    public int CountOf(int lod) => _counts[Clamp(lod)];

    /// <summary>이 밴드의 멤버. 할당 0.</summary>
    public ReadOnlySpan<int> MembersOf(int lod)
    {
        int band = Clamp(lod);
        return _members[band].AsSpan(0, _counts[band]);
    }

    /// <summary>이 NPC 가 지금 속한 밴드.</summary>
    public int BandOf(int npc) => _bandOf[npc];

    /// <summary>밴드 이동을 기다리는 NPC 가 있는가.</summary>
    public bool HasPendingMigration()
    {
        for (int npc = 0; npc < _store.Count; npc++)
        {
            if (_bandOf[npc] != Clamp(_store.Lod[npc]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 이번 틱의 스캔 구간. 밴드 멤버를 <c>Period</c> 개의 슬라이스로 나누고 그중 하나를 본다.
    /// 그래서 이 밴드의 NPC 한 마리는 <c>Period</c> 틱마다 정확히 한 번 판정된다.
    /// </summary>
    public (int Start, int End) SliceOf(int lod, long tick)
    {
        int band = Clamp(lod);
        int period = Bands[band].Period;
        int count = _counts[band];

        if (period <= 0 || count == 0)
        {
            return (0, 0);
        }

        if (period == 1)
        {
            return (0, count);
        }

        long slice = ((tick % period) + period) % period;
        int start = (int)(count * slice / period);
        int end = (int)(count * (slice + 1) / period);

        return (start, end);
    }

    /// <summary>
    /// 등급이 바뀐 NPC 를 밴드 사이로 옮긴다. <b>틱당 최대 64건.</b>
    /// 커서를 들고 다니므로 어느 NPC도 굶지 않는다.
    /// </summary>
    /// <returns>이번 틱에 옮긴 수.</returns>
    public int Rebalance()
    {
        if (_store.Count == 0)
        {
            return 0;
        }

        int moved = 0;

        for (int scanned = 0; scanned < _store.Count && moved < MaxBandMigrationsPerTick; scanned++)
        {
            int npc = _cursor;
            _cursor = _cursor + 1 >= _store.Count ? 0 : _cursor + 1;

            int target = Clamp(_store.Lod[npc]);
            int current = _bandOf[npc];

            if (target == current)
            {
                continue;
            }

            Move(npc, current, target);
            moved++;
            Migrations++;
        }

        return moved;
    }

    private void Move(int npc, int from, int to)
    {
        // swap-remove. 마지막 원소를 빈 자리로 당긴다.
        int slot = _slotOf[npc];
        int last = --_counts[from];
        int moved = _members[from][last];

        _members[from][slot] = moved;
        _slotOf[moved] = slot;

        _slotOf[npc] = _counts[to];
        _members[to][_counts[to]++] = npc;
        _bandOf[npc] = to;
    }

    private static int Clamp(int lod) => lod < 0 ? 0 : lod >= BandCount ? BandCount - 1 : lod;
}
