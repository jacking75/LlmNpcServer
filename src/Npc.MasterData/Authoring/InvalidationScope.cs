namespace Npc.MasterData.Authoring;

/// <summary>캐시 무효화 범위. docs/13 §3.</summary>
///
/// <remarks>
/// <b>이 열거형은 <c>Npc.Planning</c> 에 있었다</b> (F-04). <c>ImpactAnalyzer</c> 가 이것을
/// 돌려주는데 <c>Npc.MasterData → Npc.Planning</c> 참조는 CLAUDE.md §3 이 금지한다 —
/// 그리고 "어느 파일이 바뀌면 무엇이 무효인가" 는 애초에 마스터데이터의 성질이다.
/// <c>PlanStoreValidator.ScopeOf</c> 는 이 표로 넘긴다.
/// </remarks>
public enum InvalidationScope
{
    /// <summary>재생성 불필요. 기존 플랜을 그대로 올린다.</summary>
    None = 0,

    /// <summary>일부만 재생성. 기존 플랜은 유효하다.</summary>
    Partial = 1,

    /// <summary>전량 재생성.</summary>
    Full = 2,
}

/// <summary>
/// 파일 이름 → 플랜 무효화 범위 (docs/13 §3).
///
/// <b>이 표가 개발 속도를 좌우한다.</b> POI 를 하나 추가할 때마다 전량 재생성하면
/// 작업이 지옥이 된다 — 2,880건이 매번 3분 + $0.5 다.
/// </summary>
public static class InvalidationTable
{
    /// <summary>
    /// docs/13 §3 의 표. 이 파일이 바뀌었을 때의 무효화 범위.
    ///
    /// 표에 없는 파일은 <see cref="InvalidationScope.Full"/> 이다 —
    /// 모르는 입력이 바뀌었으면 안전한 쪽으로 판정한다.
    /// </summary>
    public static InvalidationScope ScopeOf(string fileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);

        // prompt/** — 프리픽스 해시가 바뀐다. 프롬프트가 달라지면 모든 산출물의 전제가 달라진다.
        if (fileName.StartsWith("prompt/", StringComparison.Ordinal)
            || fileName.StartsWith("prompt\\", StringComparison.Ordinal))
        {
            return InvalidationScope.Full;
        }

        return fileName switch
        {
            // 비트 의미가 바뀌면 모든 플랜의 requires/grants 가 무의미하다.
            "world_flags.json" => InvalidationScope.Full,

            // 카탈로그가 프롬프트 프리픽스에 실린다.
            "actions.json" => InvalidationScope.Full,

            // 아키타입 code 가 버킷 인덱스에 들어간다.
            "archetypes.json" => InvalidationScope.Full,

            // 인덱싱 자체가 바뀐다.
            "context_buckets.json" => InvalidationScope.Full,

            // 기존 플랜은 유효하다. 새 POI 를 쓰는 플랜만 재생성한다.
            "pois.json" => InvalidationScope.Partial,
            "items.json" => InvalidationScope.Partial,

            // 존은 플랜에 들어가지 않는다 ($zone 으로 런타임이 채운다).
            // 그래도 3단 검증의 장소 판정이 존을 보므로 안전하게 부분 무효화한다.
            "zones.json" => InvalidationScope.Partial,

            // 거리 행렬. 플랜의 유효성과 무관하다 — 실행 시점의 이동 시간만 바뀐다.
            "poi_distances.bin" => InvalidationScope.None,

            // 플랜은 개체에 안 묶인다.
            "npc_instances.json" => InvalidationScope.None,

            // 폴백은 별도 저장이다.
            "fallback_plans.json" => InvalidationScope.None,

            // 플랜에 인터럽트가 없다 (docs/03 §1).
            "interrupts.json" => InvalidationScope.None,

            _ => InvalidationScope.Full,
        };
    }
}
