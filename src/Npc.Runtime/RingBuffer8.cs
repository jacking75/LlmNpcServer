using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Npc.Runtime;

/// <summary>중요도를 가진 항목. <see cref="RingBuffer8{T}"/> 가 이 값으로 밀어낼 것을 고른다.</summary>
public interface ISalient
{
    /// <summary>중요도 0~255. 클수록 오래 남는다.</summary>
    byte Salience { get; }
}

/// <summary>8칸 인라인 배열. 힙 할당이 없다.</summary>
/// <typeparam name="T">칸에 담을 값 타입.</typeparam>
[InlineArray(SlotCount)]
public struct Slots8<T>
{
    /// <summary>슬롯 수. <see cref="RingBuffer8{T}.Capacity"/> 와 같아야 한다.</summary>
    public const int SlotCount = 8;

    private T _element0;
}

/// <summary>
/// 고정 8슬롯 기억 버퍼. docs/11 §3 · 상위 계획 §4.1.
///
/// <b>salience 낮은 것부터 밀려난다.</b> 링 버퍼라는 이름과 달리 가장 오래된 것을 버리지 않는다 —
/// "플레이어에게 맞았다"가 "누가 지나갔다"보다 오래 남아야 NPC 의 기억이 그럴듯해진다.
///
/// 값 타입이고 인라인 배열만 쓰므로 <b>할당이 0</b>이다. NPC 5,000개를 배열로 들고 있어도 GC 압력이 없다.
/// </summary>
/// <typeparam name="T">기억 항목.</typeparam>
public struct RingBuffer8<T>
    where T : struct, ISalient
{
    /// <summary>슬롯 수.</summary>
    public const int Capacity = Slots8<byte>.SlotCount;

    private Slots8<T> _items;
    private Slots8<byte> _age;   // 삽입 순서. 같은 salience 면 오래된 것을 먼저 버린다.
    private byte _count;
    private byte _clock;

    /// <summary>담긴 항목 수.</summary>
    public readonly int Count => _count;

    /// <summary>가득 찼는가.</summary>
    public readonly bool IsFull => _count >= Capacity;

    /// <summary>i번 슬롯. 순서는 삽입 순서가 아니다.</summary>
    public readonly T this[int index] => index >= 0 && index < _count
        ? _items[index]
        : throw new ArgumentOutOfRangeException(nameof(index));

    /// <summary>
    /// 항목을 넣는다. 가득 찼으면 salience 가 가장 낮은 것을 밀어낸다.
    /// 새 항목이 그보다도 낮으면 <b>새 항목을 버린다</b> — 더 중요한 기억을 지우지 않는다.
    /// </summary>
    /// <returns>담겼으면 true.</returns>
    public bool Add(in T item)
    {
        if (_count < Capacity)
        {
            _items[_count] = item;
            _age[_count] = _clock++;
            _count++;
            return true;
        }

        int victim = 0;
        byte lowest = _items[0].Salience;
        byte oldest = _age[0];

        for (int i = 1; i < Capacity; i++)
        {
            byte salience = _items[i].Salience;

            if (salience < lowest || (salience == lowest && IsOlder(_age[i], oldest)))
            {
                victim = i;
                lowest = salience;
                oldest = _age[i];
            }
        }

        if (item.Salience < lowest)
        {
            return false;
        }

        _items[victim] = item;
        _age[victim] = _clock++;
        return true;
    }

    /// <summary>전부 비운다.</summary>
    public void Clear()
    {
        _count = 0;
        _clock = 0;
    }

    /// <summary>
    /// 읽기 전용 순회. 할당 0.
    /// <c>UnscopedRef</c> 는 인라인 배열에 대한 스팬이 이 메서드 밖으로 나갈 수 있게 한다 —
    /// 버퍼 자체가 살아 있는 동안만 유효하다.
    /// </summary>
    [UnscopedRef]
    public readonly ReadOnlySpan<T> AsSpan()
    {
        ReadOnlySpan<T> all = _items;
        return all[.._count];
    }

    /// <summary>_clock 이 한 바퀴 돌아도 순서를 맞게 비교한다 (순환 비교).</summary>
    private static bool IsOlder(byte a, byte b) => (byte)(b - a) < 128 && a != b;
}
