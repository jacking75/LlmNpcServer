using Npc.Contracts;

namespace Npc.Core;

/// <summary>
/// 플랜 생성 티어. docs/14 §4 · 상위 계획 §10.4.
///
/// ordinal 이 곧 "비싸지는 순서" 다 — <c>Downgrade</c> 는 한 칸씩 내린다.
/// </summary>
public enum Tier
{
    /// <summary>거절. 예산이 없어 아무 티어도 못 쓴다. 이때 NPC 는 기존 플랜을 계속 쓴다.</summary>
    None = 0,

    /// <summary>T1 — 로컬 엔진. 저지연·무비용. 1회용 개별 재계획이 여기로 간다.</summary>
    T1 = 1,

    /// <summary>T2 — 외부 API. 고품질·유료. 수천 NPC 가 공유하는 아키타입 플랜이 여기로 간다.</summary>
    T2 = 2,
}

/// <summary>
/// 재계획 예산 계약. docs/14 §3.
///
/// <b>구현(<c>ReplanBudget</c>)은 <c>Npc.Planning</c> 에 있고 <c>Npc.Llm</c> 은 그것을 참조하지 않는다</b>
/// (CLAUDE.md §3 — 둘은 형제 프로젝트다). <c>IDryRunValidator</c>(T2-13)·
/// <c>IPlanReuseSource</c>(T2-11) 와 같은 방법이다.
///
/// <b>이 계약을 우회하는 경로를 만들지 않는다</b> (CLAUDE.md §2.7). 캡을 넘으면
/// T2 → T1 강등이고, T1 마저 소진되면 거절이다.
/// </summary>
public interface IReplanBudget
{
    /// <summary>
    /// 이 티어로 요청 하나를 낼 수 있는가. 소비까지 한다 — 확인만 하려면 <see cref="Peek"/>.
    /// </summary>
    /// <param name="tier">쓰려는 티어.</param>
    /// <param name="estimatedTokens">이 요청의 예상 토큰(프리픽스 + 서픽스 + 출력).</param>
    /// <param name="now">현재 틱. <see cref="System.DateTime"/> 을 쓰지 않는다 (CLAUDE.md §2.3).</param>
    bool TryAcquire(Tier tier, int estimatedTokens, Tick now);

    /// <summary>
    /// 요청한 티어로 못 내면 한 칸 내려서라도 낸다. docs/14 §3 · §4 의 "T2 → T1 강등 → 거절".
    /// </summary>
    /// <returns>실제로 확보한 티어. <see cref="Tier.None"/> 이면 거절이다.</returns>
    Tier Acquire(Tier requested, int estimatedTokens, Tick now);

    /// <summary>소비하지 않고 확인만 한다.</summary>
    bool Peek(Tier tier, int estimatedTokens, Tick now);

    /// <summary>추정 토큰의 비용. T1(로컬)은 항상 0 이다. 정산 시 차액을 내는 데 쓴다.</summary>
    double EstimateCost(Tier tier, int tokens);

    /// <summary>
    /// 호출이 끝난 뒤 실제 사용량을 반영한다. 추정과 실측의 차이를 여기서 정산한다 —
    /// 추정만으로 캡을 세면 실제 비용이 캡을 넘길 수 있다.
    /// </summary>
    void Settle(Tier tier, int actualTokens, double actualCostUsd);
}
