using System.Collections.Immutable;
using Npc.MasterData;

namespace Npc.Planning;

/// <summary>캐시 무효화 범위. docs/13 §3.</summary>
public enum InvalidationScope
{
    /// <summary>재생성 불필요. 기존 플랜을 그대로 올린다.</summary>
    None = 0,

    /// <summary>일부만 재생성. 기존 플랜은 유효하다.</summary>
    Partial = 1,

    /// <summary>2,880건 전량 재생성.</summary>
    Full = 2,
}

/// <summary>
/// 플랜 스토어 무효화 판정. docs/13 §3.
///
/// <b>이 표가 개발 속도를 좌우한다.</b> POI 를 하나 추가할 때마다 전량 재생성하면
/// W8 이후 작업이 지옥이 된다 — 2,880건이 매번 3분 + $0.5 다.
///
/// <para>
/// 판정 순서가 중요하다: 콘텐츠 해시가 같으면 볼 것도 없이 <see cref="InvalidationScope.None"/>,
/// 프리픽스가 바뀌었으면 파일 diff 를 볼 것도 없이 <see cref="InvalidationScope.Full"/> 이다
/// — 프롬프트가 달라지면 모든 산출물의 전제가 달라진다.
/// </para>
///
/// <para>
/// <b>"추가만" 은 코드가 검증하지 않는다.</b> 파일 해시 하나로는 추가와 수정을 가를 수 없다.
/// 대신 저장소 규약이 그것을 보장한다 — <c>code</c>·<c>bit</c> 번호는 절대 재배치하지 않고
/// 추가는 뒤에만 한다 (CLAUDE.md §2.4). 게다가 플랜에는 절대 POI id 가 들어가지 않고
/// (docs/01 §2.2 의 <c>$home</c>·<c>$workplace</c> 심볼만 쓴다),
/// <c>items.json</c> 의 레시피는 프롬프트 프리픽스에 실려 있어 내용이 바뀌면
/// 프리픽스 해시 검사에서 먼저 걸린다.
/// </para>
/// </summary>
public static class PlanStoreValidator
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

    /// <summary>
    /// 기존 manifest 와 현재 마스터데이터를 비교해 무효화 범위를 판정한다.
    ///
    /// <paramref name="prefixHash"/> 를 인자로 받는 이유는 <see cref="MasterDataSet"/> 이
    /// <c>PromptPrefix</c> 를 들고 있지 않기 때문이다 —
    /// 프리픽스는 <c>Npc.Llm</c> 에 있고 <c>Npc.MasterData → Npc.Llm</c> 참조는 CLAUDE.md §3 이 금지한다.
    /// </summary>
    /// <param name="previous">기존 manifest. null 이면 스토어가 없다는 뜻이라 전량이다.</param>
    /// <param name="current">현재 마스터데이터.</param>
    /// <param name="prefixHash">현재 프롬프트 프리픽스 SHA-256.</param>
    public static InvalidationScope Compare(Manifest? previous, MasterDataSet current, string prefixHash) =>
        Compare(previous, current, prefixHash, out _);

    /// <summary>무효화 범위 + 바뀐 파일 목록. 로그·리포트가 "왜 전량인가"를 보여줄 수 있어야 한다.</summary>
    /// <param name="previous">기존 manifest. null 이면 전량.</param>
    /// <param name="current">현재 마스터데이터.</param>
    /// <param name="prefixHash">현재 프롬프트 프리픽스 SHA-256.</param>
    /// <param name="changed">바뀐 파일 이름 (오름차순). 프리픽스만 바뀌었으면 <c>prompt/</c> 한 줄.</param>
    public static InvalidationScope Compare(
        Manifest? previous,
        MasterDataSet current,
        string prefixHash,
        out ImmutableArray<string> changed)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentException.ThrowIfNullOrEmpty(prefixHash);

        if (previous is null)
        {
            changed = [];
            return InvalidationScope.Full;   // 스토어가 없다. 전량 생성이다
        }

        bool prefixChanged = !string.Equals(previous.PrefixHash, prefixHash, StringComparison.Ordinal);

        // 마스터데이터도 프롬프트도 그대로면 볼 것이 없다.
        if (!prefixChanged
            && string.Equals(previous.MasterdataHash, current.ContentHash, StringComparison.Ordinal))
        {
            changed = [];
            return InvalidationScope.None;
        }

        if (prefixChanged)
        {
            changed = ["prompt/"];
            return InvalidationScope.Full;   // 프롬프트가 바뀌었다
        }

        changed = ChangedFiles(previous, current);

        // 기존 manifest 에 파일 해시가 없으면(구버전) 어느 파일이 바뀌었는지 알 수 없다.
        if (previous.FileHashes.IsEmpty)
        {
            return InvalidationScope.Full;
        }

        InvalidationScope scope = InvalidationScope.None;

        foreach (string file in changed)
        {
            InvalidationScope fileScope = ScopeOf(file);

            if (fileScope > scope)
            {
                scope = fileScope;
            }
        }

        return scope;
    }

    /// <summary>
    /// 해시가 다른 파일 이름 (오름차순). 한쪽에만 있는 파일도 "바뀐 것"으로 센다.
    /// </summary>
    public static ImmutableArray<string> ChangedFiles(Manifest previous, MasterDataSet current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        var names = new SortedSet<string>(StringComparer.Ordinal);

        foreach (FileHash hash in previous.FileHashes)
        {
            names.Add(hash.FileName);
        }

        foreach (FileHash hash in current.FileHashes)
        {
            names.Add(hash.FileName);
        }

        var changed = ImmutableArray.CreateBuilder<string>(names.Count);

        foreach (string name in names)
        {
            if (!string.Equals(previous.HashOf(name), current.HashOf(name), StringComparison.Ordinal))
            {
                changed.Add(name);
            }
        }

        return changed.ToImmutable();
    }
}
