namespace Npc.Tests;

/// <summary>
/// 할당 0 을 흔들리지 않게 재는 계기.
///
/// <b>워밍업만으로는 부족하다.</b> 계층 JIT 은 메서드를 나중에 다시 컴파일하고, 그 재컴파일과
/// PGO 계측이 <b>측정 창 안에</b> 떨어지면 코드가 아무것도 할당하지 않아도 수 KB 가 잡힌다.
/// 이 저장소에서 <c>RingBuffer8_AddDoesNotAllocate</c>·<c>Lod_UpdateDoesNotAllocate</c> 가
/// 병렬 회차에서 간헐적으로 그렇게 깨졌다.
///
/// <para>
/// <b>여러 창의 최솟값을 쓴다.</b> 실제로 할당하는 코드는 <b>모든</b> 창에서 할당하고,
/// JIT 재컴파일은 많아도 한두 창에만 나타난다 — 그 차이가 신호와 잡음을 가른다.
/// 창을 늘려 통과시키는 것이 아니라, <b>같은 판정을 안정적으로</b> 내게 하는 것이다.
/// </para>
///
/// <para>
/// 부하 회차의 <c>bytesPerTick</c> 은 이것과 다른 계기다 — 그쪽은 누계를 틱 수로 나누고,
/// 여기는 한 창의 순수 델타를 본다.
/// </para>
/// </summary>
public static class AllocationProbe
{
    /// <summary>기본 측정 창 수.</summary>
    public const int DefaultWindows = 5;

    /// <summary>
    /// <paramref name="action"/> 을 여러 번 돌려 <b>최소</b> 할당 바이트를 돌려준다.
    /// </summary>
    /// <param name="action">잴 코드. 창마다 한 번씩 불린다.</param>
    /// <param name="warmup">측정 전에 돌릴 횟수. JIT 승격을 창 밖으로 밀어낸다.</param>
    /// <param name="windows">측정 창 수.</param>
    public static long MinimumBytes(Action action, int warmup = 3, int windows = DefaultWindows)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentOutOfRangeException.ThrowIfNegative(warmup);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windows);

        for (int i = 0; i < warmup; i++)
        {
            action();
        }

        long minimum = long.MaxValue;

        for (int window = 0; window < windows; window++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();

            action();

            minimum = Math.Min(minimum, GC.GetAllocatedBytesForCurrentThread() - before);

            if (minimum == 0)
            {
                // 0 보다 작아질 수 없다. 더 돌릴 이유가 없다.
                return 0;
            }
        }

        return minimum;
    }
}
