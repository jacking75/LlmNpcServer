using Npc.Contracts;

namespace Npc.Runtime;

/// <summary>
/// NPC 별로 진행 중인 상관 ID. docs/02 §3.4.
///
/// 네트워크는 재정렬·지연된다(N5). 플랜을 스왑하면 <b>직전 스텝에 대한 응답이 뒤늦게 도착</b>하는데,
/// 그걸 새 스텝의 완료로 착각하면 플랜이 통째로 어긋난다.
/// 스왑 시 <see cref="Invalidate"/> 로 현재 ID 를 버리면 낡은 응답이 자동으로 걸러진다.
///
/// ID 는 <b>순증</b>이다. <c>Guid.NewGuid()</c> 를 쓰지 않는다 — 리플레이가 깨진다 (CLAUDE.md §2.3).
/// </summary>
public sealed class CorrelationTable
{
    /// <summary>진행 중인 명령이 없음을 뜻하는 값.</summary>
    public static readonly CorrelationId None = new(0);

    private readonly CorrelationId[] _current;
    private uint _next;

    /// <summary>NPC 수만큼 잡는다. 기동 시 1회.</summary>
    public CorrelationTable(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);

        _current = new CorrelationId[capacity];
        _next = 1;
    }

    /// <summary>지금까지 발급한 ID 수.</summary>
    public long Issued => _next - 1;

    /// <summary>이 NPC 에 대해 진행 중인 상관 ID. 없으면 <see cref="None"/>.</summary>
    public CorrelationId Current(int npc) => _current[npc];

    /// <summary>새 상관 ID 를 발급하고 이 NPC 의 현재 ID 로 삼는다. 할당 0.</summary>
    public CorrelationId Next(int npc)
    {
        // 0 은 "진행 중 아님"이라 건너뛴다. uint 한 바퀴는 42억 번 — 게임 7일에 도달하지 않는다.
        if (_next == 0)
        {
            _next = 1;
        }

        var id = new CorrelationId(_next++);
        _current[npc] = id;
        return id;
    }

    /// <summary>이 NPC 의 현재 ID 를 버린다. 플랜 스왑·인터럽트 시 부른다.</summary>
    public void Invalidate(int npc) => _current[npc] = None;

    /// <summary>이 응답이 지금 기다리고 있는 명령의 것인가.</summary>
    public bool IsCurrent(int npc, CorrelationId id) =>
        id != None && _current[npc] == id;

    /// <summary>이 응답이 낡은 것인가 (기다리는 명령이 없거나 다른 명령의 응답).</summary>
    public bool IsStale(int npc, CorrelationId id) => !IsCurrent(npc, id);

    /// <summary>전부 초기화. 리플레이 시작 시에만 쓴다.</summary>
    public void Reset()
    {
        Array.Clear(_current);
        _next = 1;
    }
}
