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

    /// <summary>다음에 발급할 ID 원값. 스냅샷이 담는다 (A-01).</summary>
    public uint NextId => _next;

    /// <summary>
    /// 다음 ID 를 앞으로 건너뛴다 (A-01 복원).
    ///
    /// <b>크래시 전에 나가 있던 명령과 겹치지 않게 한다.</b> 스냅샷 이후 크래시까지 발급된 ID 는
    /// 스냅샷에 없지만 게임서버에는 남아 있다. 같은 번호를 다시 쓰면 낡은 응답이
    /// 새 명령의 완료로 읽힌다 — 여유분만큼 건너뛰면 그 창이 닫힌다.
    /// </summary>
    /// <param name="from">스냅샷이 담은 다음 ID.</param>
    /// <param name="gap">건너뛸 폭. 기본 65,536.</param>
    public void JumpTo(uint from, uint gap = 65_536)
    {
        uint next = from + gap;

        _next = next == 0 ? 1 : next;
    }

    /// <summary>전부 초기화. 리플레이 시작 시에만 쓴다.</summary>
    public void Reset()
    {
        Array.Clear(_current);
        _next = 1;
    }
}
