using System.Collections.Immutable;
using System.Diagnostics;
using Npc.Core;
using Npc.Core.Plan;
using Npc.Core.Validation;
using Npc.MasterData;
using Npc.Planning;
using Npc.Sim.Validation;

namespace Npc.Prebake;

/// <summary>버킷 하나의 드라이런 판정.</summary>
/// <param name="Bucket">어느 버킷인가.</param>
/// <param name="Validation">4단 판정. 통과면 <see cref="ValidationResult.Ok"/>.</param>
public readonly record struct DryRunOutcome(BucketKey Bucket, ValidationResult Validation);

/// <summary>전수 드라이런 한 회차.</summary>
/// <param name="Failures">걸린 버킷만. 버킷 인덱스 오름차순.</param>
/// <param name="Checked">판정한 버킷 수.</param>
/// <param name="Skipped">표본에서 빠진 버킷 수.</param>
/// <param name="WallClockSeconds">전체 소요. 게이트는 2,880건 ≤ 60초다.</param>
/// <param name="Parallelism">쓴 병렬도.</param>
public readonly record struct DryRunReport(
    ImmutableArray<DryRunOutcome> Failures,
    int Checked,
    int Skipped,
    double WallClockSeconds,
    int Parallelism)
{
    /// <summary>걸린 수.</summary>
    public int Failed => Failures.Length;

    /// <summary>통과한 수.</summary>
    public int Passed => Checked - Failed;

    /// <summary>통과율.</summary>
    public double PassRate => Checked == 0 ? 1 : (double)Passed / Checked;

    /// <summary>건당 소요(ms). T2-13 실측과 대조할 값이다.</summary>
    public double MsPerPlan => Checked == 0 ? 0 : WallClockSeconds * 1_000 / Checked;
}

/// <summary>
/// 전수 드라이런. docs/13 §8 · T3-15.
///
/// <b>프리베이크에서 이것을 생략하면 데드락 플랜이 런타임에 배포된다.</b>
/// 생성 단계의 4단은 <b>그 버킷에서 만든 플랜</b>만 본다 — 인접 버킷에서 빌려 온 플랜은
/// 2·3단만 다시 통과했고(<c>BucketNeighbors.Revalidate</c>), 폴백은 기동 검증(V7)이 본 것이다.
/// 스토어에 실제로 올라간 것 전부를 대상 버킷 기준으로 한 번 더 굴리는 자리가 여기다.
///
/// <para>
/// <b>결정론이다.</b> 시드는 버킷 키에서 나오고(<c>ValidationContext.SeedOf</c>) 표본 선택도
/// <c>PlanHash.Mix</c> 해시라 <see cref="Random"/> 이 없다. 병렬로 돌려도 판정은 같고,
/// 결과는 버킷 인덱스 순으로 정렬해서 돌려준다 (CLAUDE.md §2.3).
/// </para>
///
/// <para>
/// <see cref="DryRunValidator"/> 는 호출마다 <c>SimWorld</c> 를 새로 만들고 읽기 전용 마스터데이터만
/// 들고 있다 — 인스턴스 하나를 여러 스레드가 같이 써도 된다.
/// </para>
/// </summary>
public static class DryRunStage
{
    /// <summary>표본 비율의 분해능. <c>--dryrun-sample 0.001</c> 까지 구분한다.</summary>
    private const int SampleScale = 1_000;

    /// <summary>해시 솔트. 다른 지터와 같은 수열이 나오지 않게 한다.</summary>
    private const int Salt = 0x4452_594E;   // "DRYN"

    /// <summary>
    /// 스토어에 올라간 버킷 플랜 전부를 4단으로 굴린다.
    /// </summary>
    /// <param name="store">검사할 스토어.</param>
    /// <param name="data">마스터데이터.</param>
    /// <param name="sample">
    /// 표본 비율. <b>프리베이크는 1.0(전수)이다</b> (docs/13 §4 의 <c>--dryrun-sample 1.0</c>).
    /// </param>
    /// <param name="parallelism">병렬도. 0 이면 <see cref="Environment.ProcessorCount"/>.</param>
    /// <param name="cancellationToken">취소.</param>
    public static DryRunReport Run(
        PlanStore store,
        MasterDataSet data,
        double sample = 1.0,
        int parallelism = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(data);

        int workers = parallelism > 0 ? parallelism : Environment.ProcessorCount;
        var validator = new DryRunValidator(data);

        // 판정 대상을 먼저 고른다 — 병렬 구간에서 표본 판정까지 하면 읽기가 두 번 된다.
        var targets = new List<(int Index, CompiledPlan Plan)>(BucketKey.TotalKeys);
        int skipped = 0;

        for (int index = 0; index < BucketKey.TotalKeys; index++)
        {
            if (store.PeekBucket(BucketKey.FromIndex(index)) is not { } plan)
            {
                continue;
            }

            if (!InSample(index, sample))
            {
                skipped++;
                continue;
            }

            targets.Add((index, plan));
        }

        var failures = new ValidationResult?[BucketKey.TotalKeys];
        long started = Stopwatch.GetTimestamp();

        Parallel.ForEach(
            targets,
            new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = cancellationToken },
            item =>
            {
                var bucket = BucketKey.FromIndex(item.Index);

                // 플랜이 어느 버킷에서 왔든 <b>이 버킷의 상황</b>으로 굴린다 —
                // 인접 재사용 플랜이 대상 버킷에서 교착하는지가 여기서 드러난다.
                ValidationResult result = validator.Validate(
                    item.Plan,
                    ValidationContext.For(bucket, data.InitialFlags(bucket)));

                if (!result.IsValid)
                {
                    failures[item.Index] = result;
                }
            });

        double wallClock = Stopwatch.GetElapsedTime(started).TotalSeconds;

        // 결과 순서를 버킷 인덱스로 고정한다. 병렬 완료 순서에 기대면 회차 간 diff 가 흔들린다.
        var builder = ImmutableArray.CreateBuilder<DryRunOutcome>();

        for (int index = 0; index < failures.Length; index++)
        {
            if (failures[index] is { } failure)
            {
                builder.Add(new DryRunOutcome(BucketKey.FromIndex(index), failure));
            }
        }

        return new DryRunReport(builder.ToImmutable(), targets.Count, skipped, wallClock, workers);
    }

    /// <summary>
    /// 이 버킷이 표본에 드는가. <b>결정론이다</b> — <see cref="Random"/> 을 쓰면
    /// 회차마다 다른 버킷을 검사해 "2회 실행 → 동일 판정"이 성립하지 않는다.
    /// </summary>
    public static bool InSample(int bucketIndex, double sample)
    {
        if (sample >= 1.0)
        {
            return true;
        }

        if (sample <= 0)
        {
            return false;
        }

        return PlanHash.Mix(bucketIndex, Salt) % SampleScale < (uint)(sample * SampleScale);
    }
}
