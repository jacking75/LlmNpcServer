using Npc.TestBed.Protocol;
using Npc.TestClient.Net;

namespace Npc.TestClient.Render;

/// <summary>
/// 스냅샷 두 장 사이를 메운다. docs/20 §9.3.
///
/// <para>
/// <b>스냅샷은 5Hz 다.</b> 그대로 그리면 200ms마다 순간이동한다. 마지막 두 장을 들고
/// <c>renderTime = now − 200ms</c> 시점을 선형 보간한다 — 한 프레임 늦게 보는 대신
/// 매 프레임 위치가 있다.
/// </para>
///
/// <para>
/// <b>새 스냅샷이 늦으면 외삽하지 않고 멈춘다.</b> 데모에서는 튀는 것보다 멈추는 것이 낫다 —
/// 외삽하면 NPC 가 벽을 뚫고 나갔다가 되돌아오고, 사람은 그것을 "서버가 이상하다" 로 읽는다.
/// </para>
///
/// <para>
/// <b>프레임마다 할당하지 않는다.</b> 출력 배열과 짝짓기 사전을 한 번 잡고 재사용한다 —
/// 60fps × 256 엔티티면 그러지 않을 때 초당 15,000개의 임시 객체가 나온다.
/// </para>
/// </summary>
public sealed class EntityInterpolator
{
    /// <summary>보간 지연(ms). 스냅샷 주기 200ms 와 같다 — 한 장 분의 여유다.</summary>
    public const int DelayMillis = 200;

    /// <summary>출력 상한. AOI 상한과 같다 (docs/20 §8.1).</summary>
    public const int MaxEntities = 256;

    private readonly EntityState[] _output = new EntityState[MaxEntities];

    /// <summary>
    /// 직전 스냅샷의 (종류, id) → 첨자.
    ///
    /// <b>키에 종류를 넣는 이유.</b> NPC 첨자와 <c>PlayerId</c> 는 값 범위가 겹친다 —
    /// id 만으로 짝지으면 플레이어 3 이 NPC 3 의 위치로 보간된다.
    /// </summary>
    private readonly Dictionary<long, int> _index = new(MaxEntities);

    /// <summary>마지막으로 채운 엔티티 수.</summary>
    public int Count { get; private set; }

    /// <summary>보간에 실제로 쓴 비율(0~1). 1 이면 최신 스냅샷 그대로다.</summary>
    public float LastAlpha { get; private set; }

    /// <summary>스냅샷이 끊겨 멈춰 있는가. 상태바가 이것을 말할 수 있다.</summary>
    public bool Stalled { get; private set; }

    /// <summary>보간된 엔티티. 앞에서 <see cref="Count"/> 개만 유효하다.</summary>
    public ReadOnlySpan<EntityState> Entities => _output.AsSpan(0, Count);

    /// <summary>
    /// 이 시각의 화면 상태를 만든다.
    /// </summary>
    /// <param name="latest">가장 최근 스냅샷.</param>
    /// <param name="previous">그 직전 스냅샷. 없으면 <paramref name="latest"/> 를 그대로 쓴다.</param>
    /// <param name="nowMillis">지금(ms, <see cref="Environment.TickCount64"/> 기준).</param>
    /// <returns>채운 엔티티 수.</returns>
    public int Resolve(SnapshotFrame? latest, SnapshotFrame? previous, long nowMillis)
    {
        Count = 0;
        LastAlpha = 1f;
        Stalled = false;

        if (latest is not { } now)
        {
            return 0;
        }

        EntityState[] target = now.Snapshot.Entities ?? [];
        int count = Math.Min(Math.Min(now.Snapshot.EntityCount, target.Length), MaxEntities);

        if (count == 0)
        {
            return 0;
        }

        long renderAt = nowMillis - DelayMillis;

        if (previous is not { } before || before.AtMillis >= now.AtMillis)
        {
            // 짝이 없다. 최신 위치를 그대로 쓴다 — 첫 프레임과 재접속 직후가 이 경우다.
            target.AsSpan(0, count).CopyTo(_output);
            Count = count;

            return count;
        }

        // 0..1 로 자른다. 1 을 넘기지 않는 것이 "외삽하지 않는다" 다.
        float alpha = (float)(renderAt - before.AtMillis) / (now.AtMillis - before.AtMillis);

        Stalled = alpha > 1f;
        LastAlpha = Math.Clamp(alpha, 0f, 1f);

        BuildIndex(before.Snapshot);

        for (int i = 0; i < count; i++)
        {
            EntityState entity = target[i];

            if (_index.TryGetValue(Key(entity), out int j))
            {
                EntityState from = before.Snapshot.Entities![j];

                entity.X = from.X + ((entity.X - from.X) * LastAlpha);
                entity.Z = from.Z + ((entity.Z - from.Z) * LastAlpha);
            }

            // 짝이 없으면 방금 AOI 에 들어온 것이다. 보간할 과거가 없으므로 새 위치 그대로다.
            _output[i] = entity;
        }

        Count = count;

        return count;
    }

    private void BuildIndex(Snapshot previous)
    {
        _index.Clear();

        EntityState[] entities = previous.Entities ?? [];
        int count = Math.Min(previous.EntityCount, entities.Length);

        for (int i = 0; i < count; i++)
        {
            _index[Key(entities[i])] = i;
        }
    }

    private static long Key(in EntityState entity) => ((long)entity.Kind << 32) | (uint)entity.Id;
}
