using Npc.Contracts;

namespace Npc.TestGameServer.Link;

/// <summary>
/// 단일 생산자(링크 리시버 태스크) · 단일 소비자(틱 루프) 링 버퍼. docs/20 §7.1 · §7.2.
///
/// <para>
/// <c>Npc.Host/Program.cs</c> 의 <c>CommandRing</c> 과 <b>같은 모양</b>이다. 방향만 반대다 —
/// 저쪽은 틱 루프가 넣고 Sim 이 꺼내지만, 여기는 소켓에서 읽은 태스크가 넣고 틱 루프가 꺼낸다.
/// </para>
///
/// <para>
/// <c>ConcurrentQueue</c> 를 쓰지 않는 이유는 하나다 — 이 경로는 매 틱 전량이 지나가므로
/// 할당이 있으면 안 된다. 여기는 미리 잡은 배열뿐이다. <c>lock</c> 도 없다:
/// 생산자·소비자가 각자 자기 첨자만 쓰고, 상대 첨자는 <c>Volatile.Read</c> 로만 본다.
/// </para>
///
/// <para>
/// <b>링이 차면 버린다.</b> 예외를 던지지 않는다 — 명령은 유실된다고 가정하는 것이
/// 이 링크의 계약이고(docs/02 §1), NPC 서버는 <c>timeout_s</c> 만료로 스스로 재개한다.
/// </para>
/// </summary>
public sealed class CommandInbox
{
    /// <summary>수용량. 2의 거듭제곱이라 마스크로 감는다.</summary>
    public const int Capacity = 1 << 16;

    private const int Mask = Capacity - 1;

    private readonly NpcCommand[] _slots = new NpcCommand[Capacity];
    private long _head;   // 소비자 (틱 루프)
    private long _tail;   // 생산자 (링크 리시버)

    /// <summary>링이 가득 차 버린 명령 수. 0 이 아니면 게임서버가 밀린 것이다.</summary>
    public long Dropped { get; private set; }

    /// <summary>받은 명령 수. 버린 것은 세지 않는다.</summary>
    public long Accepted { get; private set; }

    /// <summary>지금 링에 남아 있는 수.</summary>
    public int Pending => (int)(Volatile.Read(ref _tail) - Volatile.Read(ref _head));

    /// <summary>리시버 태스크에서 부른다. 할당 0.</summary>
    /// <returns>담았으면 true. 링이 차서 버렸으면 false.</returns>
    public bool TryEnqueue(in NpcCommand command)
    {
        long tail = _tail;

        if (tail - Volatile.Read(ref _head) >= Capacity)
        {
            Dropped++;
            return false;
        }

        _slots[tail & Mask] = command;
        Accepted++;
        Volatile.Write(ref _tail, tail + 1);
        return true;
    }

    /// <summary>틱 루프에서 부른다. 넣은 순서 그대로 나온다.</summary>
    public bool TryDequeue(out NpcCommand command)
    {
        long head = _head;

        if (head >= Volatile.Read(ref _tail))
        {
            command = default;
            return false;
        }

        command = _slots[head & Mask];
        Volatile.Write(ref _head, head + 1);
        return true;
    }
}
