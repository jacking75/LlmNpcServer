using Npc.Contracts;
using Npc.Core.Plan;

namespace Npc.Core.Validation;

/// <summary>
/// 검증기가 도는 데 필요한 상황. docs/03 §3 · docs/12 §5.
///
/// 1~3단은 <see cref="BucketKey"/> 와 어휘만 있으면 되지만, 4단은 세계를 굴려야 하므로
/// 시드와 시간 예산이 더 필요하다.
/// </summary>
/// <param name="Bucket">아키타입 × 시간대 × 지역상태 × 기후.</param>
/// <param name="InitialFlags">
/// 시작 상태. 버킷과 아키타입 기본 인벤토리에서 유도한다
/// (<c>IPlanValidationVocabulary.InitialFlags</c>).
/// </param>
/// <param name="Seed">
/// 난수 시드. <b>버킷 키에서 유도한다</b> — 같은 플랜은 언제나 같은 판정을 받아야 한다.
/// 안 그러면 골든 테스트가 불안정해진다 (docs/12 §5).
/// </param>
/// <param name="MaxGameHours">이 시간을 넘기면 <c>V4.TIMEOUT</c>.</param>
public readonly record struct ValidationContext(
    BucketKey Bucket,
    WorldFlags InitialFlags,
    int Seed,
    int MaxGameHours = ValidationContext.DefaultMaxGameHours)
{
    /// <summary>docs/03 §3 4단의 시간 상한.</summary>
    public const int DefaultMaxGameHours = 36;

    /// <summary>
    /// 버킷 키에서 시드를 유도한다. <b>난수도 시각도 섞지 않는다</b> (CLAUDE.md §2.3).
    /// 같은 버킷이면 언제 어디서 돌려도 같은 시드가 나온다.
    /// </summary>
    public static int SeedOf(BucketKey bucket) => (int)PlanHash.Mix((uint)bucket.ToIndex() + 1u);

    /// <summary>버킷과 초기 플래그로 상황을 만든다. 시드는 버킷에서 유도한다.</summary>
    public static ValidationContext For(BucketKey bucket, WorldFlags initialFlags) =>
        new(bucket, initialFlags, SeedOf(bucket));
}

/// <summary>
/// 검증기 4단 — 드라이런. docs/03 §3 · docs/12 §5.
///
/// <b>계약만 <c>Npc.Core</c> 에 둔다.</b> 구현은 <c>SimWorld</c> 를 아는 <c>Npc.Sim</c> 이 갖는다 —
/// <c>Npc.Core</c> 는 <c>Npc.Contracts</c> 만 참조하기 때문이다 (CLAUDE.md §3).
/// <c>IPlanVocabulary</c>(docs/03 §5)를 가른 것과 같은 이유이고 같은 방법이다.
/// 호출자(<c>Npc.Llm</c>)는 이 인터페이스만 본다.
/// </summary>
public interface IDryRunValidator
{
    /// <summary>플랜을 축소 세계에서 1~2 사이클 돌려 본다.</summary>
    ValidationResult Validate(CompiledPlan plan, ValidationContext context);
}
