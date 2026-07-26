using System.Collections.Immutable;
using Npc.Core;
using Npc.Core.Plan;
using Npc.Core.Validation;
using Npc.MasterData;

namespace Npc.Planning;

/// <summary>
/// 인접 버킷 정의와 재사용. docs/12 §6·§7.
///
/// 생성이 두 번 다 실패했을 때 폴백으로 떨어지기 전에 한 번 더 건져 보는 자리다.
/// <b>아키타입은 절대 바꾸지 않는다.</b> 대장장이 플랜을 농부에게 주면 허용 액션 목록부터 어긋난다 —
/// 거리가 가까운 것은 상황(시간대·지역상태·기후)뿐이다.
/// </summary>
public static class BucketNeighbors
{
    /// <summary>docs/12 §7 이 정한 후보 수 상한.</summary>
    public const int MaxNeighbors = 5;

    /// <summary>
    /// 거리가 가까운 순으로 후보를 채운다. 자기 자신과 중복은 빼므로 5개 미만이 나올 수 있다.
    /// 할당이 없다 — 프리베이크가 2,880번 부른다.
    /// </summary>
    /// <returns>채운 개수.</returns>
    public static int Fill(BucketKey key, Span<BucketKey> destination)
    {
        Span<BucketKey> candidates =
        [
            key with { C = Climate.Fair },                                  // 기후만 완화
            key with { R = Relax(key.R) },                                  // War→Alert, Alert→Peace
            key with { C = Climate.Fair, R = Relax(key.R) },
            key with { T = AdjacentTime(key.T) },                           // 인접 시간대
            key with { R = RegionState.Peace, C = Climate.Fair },           // 최후
        ];

        int written = 0;

        foreach (BucketKey candidate in candidates)
        {
            if (candidate == key || written >= destination.Length)
            {
                continue;
            }

            bool duplicate = false;
            for (int i = 0; i < written; i++)
            {
                if (destination[i] == candidate)
                {
                    duplicate = true;
                    break;
                }
            }

            if (!duplicate)
            {
                destination[written++] = candidate;
            }
        }

        return written;
    }

    /// <summary>docs/12 §7 의 시그니처. 도구·테스트가 쓴다.</summary>
    public static ImmutableArray<BucketKey> Of(BucketKey key)
    {
        Span<BucketKey> buffer = stackalloc BucketKey[MaxNeighbors];
        int count = Fill(key, buffer);

        var builder = ImmutableArray.CreateBuilder<BucketKey>(count);

        for (int i = 0; i < count; i++)
        {
            builder.Add(buffer[i]);
        }

        return builder.ToImmutable();
    }

    /// <summary>지역 상태를 한 단계 누그러뜨린다. 평시는 그대로다.</summary>
    public static RegionState Relax(RegionState state) => state switch
    {
        RegionState.Disaster => RegionState.War,
        RegionState.War => RegionState.Alert,
        RegionState.Alert => RegionState.Peace,
        _ => RegionState.Peace,
    };

    /// <summary>바로 앞 시간대. Dawn 의 앞은 Night 다.</summary>
    public static TimeOfDay AdjacentTime(TimeOfDay time) =>
        (TimeOfDay)(((int)time + BucketKey.TimeOfDayCount - 1) % BucketKey.TimeOfDayCount);

    /// <summary>
    /// 인접 버킷의 플랜을 빌려 온다. <b>빌려 온 플랜도 2·3단을 다시 통과해야 한다</b> (docs/12 §7) —
    /// 상황이 달라졌으므로 전제조건도 달라진다.
    /// </summary>
    /// <param name="target">채우려는 버킷.</param>
    /// <param name="store">이미 만들어진 플랜들.</param>
    /// <param name="data">검증 어휘.</param>
    /// <param name="reused">빌려 온 플랜. 대상 버킷으로 다시 표시된다.</param>
    /// <param name="source">어느 버킷에서 빌렸는가.</param>
    /// <returns>빌릴 수 있으면 참.</returns>
    public static bool TryReuse(
        BucketKey target,
        PlanStore store,
        MasterDataSet data,
        out CompiledPlan? reused,
        out BucketKey source)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(data);

        Span<BucketKey> neighbors = stackalloc BucketKey[MaxNeighbors];
        int count = Fill(target, neighbors);

        for (int i = 0; i < count; i++)
        {
            BucketKey neighbor = neighbors[i];

            if (!store.HasBucket(neighbor))
            {
                continue;
            }

            CompiledPlan candidate = store.Resolve(neighbor);

            if (!Revalidate(candidate, target, data))
            {
                continue;
            }

            reused = candidate with { Bucket = target };
            source = neighbor;
            return true;
        }

        reused = null;
        source = default;
        return false;
    }

    /// <summary>빌려 온 플랜을 대상 버킷 기준으로 2·3단 재검증한다.</summary>
    public static bool Revalidate(CompiledPlan plan, BucketKey target, MasterDataSet data)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(data);

        // 원본 JSON 이 있으면 그것을 본다 — 컴파일 왕복은 route 경유지 같은 것을 잃는다.
        PlanDocument? document;

        if (!string.IsNullOrEmpty(plan.SourceJson))
        {
            if (!SchemaValidator.Validate(plan.SourceJson, out document).IsValid || document is null)
            {
                return false;
            }
        }
        else
        {
            document = PlanCompiler.ToDocument(plan, data);
        }

        return VocabularyValidator.Validate(document, target.A, data).IsValid
            && CoherenceValidator.Validate(document, target, target.A, data).IsValid;
    }
}
