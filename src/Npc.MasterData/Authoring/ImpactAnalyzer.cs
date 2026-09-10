using System.Collections.Immutable;

namespace Npc.MasterData.Authoring;

/// <summary>변경 하나의 파급.</summary>
/// <param name="Changed">바뀐 파일 이름 (오름차순).</param>
/// <param name="Scope">플랜 스토어 무효화 범위.</param>
/// <param name="PrefixChanged">프롬프트 프리픽스가 바뀌는가 → 플랜 전량 무효.</param>
/// <param name="StructuralChanged">구조 해시가 바뀌는가 → 게임서버 재배포 (B-04).</param>
/// <param name="ContentChanged">내용 해시가 바뀌는가.</param>
/// <param name="Regenerate">다시 만들어야 하는 파생물.</param>
public readonly record struct Impact(
    ImmutableArray<string> Changed,
    InvalidationScope Scope,
    bool PrefixChanged,
    bool StructuralChanged,
    bool ContentChanged,
    ImmutableArray<DerivedStatus> Regenerate)
{
    /// <summary>게임서버를 다시 배포해야 하는가. 구조 해시가 바뀌면 핸드셰이크가 거절된다.</summary>
    public bool NeedsGameServerRedeploy => StructuralChanged;

    /// <summary>프리베이크를 다시 돌려야 하는가.</summary>
    public bool NeedsPrebake => Scope != InvalidationScope.None;
}

/// <summary>
/// 변경 집합 → 무엇을 다시 해야 하는가 (F-04).
///
/// <b>사람이 기억하고 있던 것을 표로 만든다.</b> "POI 를 고치면 거리표를 다시 만들어야 한다"
/// "아키타입을 고치면 프리픽스가 바뀐다" 같은 것은 지금 문서에만 있고, 문서를 안 읽으면
/// 낡은 파생물로 돌아간다.
///
/// <para>
/// <b>프리픽스 해시는 인자로 받는다.</b> 프리픽스 조립은 <c>Npc.Llm</c> 에 있고
/// <c>Npc.MasterData → Npc.Llm</c> 참조는 CLAUDE.md §3 이 금지한다 —
/// <c>PlanStoreValidator.Compare</c> 가 같은 이유로 같은 모양이다.
/// </para>
/// </summary>
public static class ImpactAnalyzer
{
    /// <summary>
    /// 프롬프트 프리픽스에 실리는 파일. 이것이 바뀌면 프리픽스 SHA 가 바뀌고
    /// <b>플랜 스토어가 전량 무효</b>가 된다.
    /// </summary>
    public static ImmutableArray<string> PrefixInputs { get; } =
        ["actions.json", "archetypes.json", "items.json", "world_flags.json"];

    /// <summary>
    /// 구조 해시에 들어가는 파일. 게임서버가 이것으로 계약을 맞춘다 (B-04).
    /// <c>StructuralHash.Compute</c> 가 읽는 표와 같아야 한다.
    /// </summary>
    public static ImmutableArray<string> StructuralInputs { get; } =
        ["actions.json", "items.json", "zones.json", "pois.json", "archetypes.json", "context_buckets.json"];

    /// <summary>
    /// 파일 목록을 받아 파급을 낸다.
    /// </summary>
    /// <param name="masterDataDirectory"><c>masterdata/</c> 경로. 파생물 신선도를 여기서 본다.</param>
    /// <param name="changedFiles">바뀐 파일 이름. 경로가 아니라 파일 이름이다.</param>
    public static Impact Of(string masterDataDirectory, IEnumerable<string> changedFiles)
    {
        ArgumentException.ThrowIfNullOrEmpty(masterDataDirectory);
        ArgumentNullException.ThrowIfNull(changedFiles);

        ImmutableArray<string> changed =
        [
            .. changedFiles.Select(Path.GetFileName)
                .Where(f => !string.IsNullOrEmpty(f))
                .Select(f => f!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f, StringComparer.Ordinal),
        ];

        InvalidationScope scope = InvalidationScope.None;
        bool prefix = false;
        bool structural = false;
        bool content = false;

        foreach (string file in changed)
        {
            InvalidationScope fileScope = InvalidationTable.ScopeOf(file);

            if (fileScope > scope)
            {
                scope = fileScope;
            }

            prefix |= PrefixInputs.Contains(file, StringComparer.OrdinalIgnoreCase);
            structural |= StructuralInputs.Contains(file, StringComparer.OrdinalIgnoreCase);

            // 내용 해시는 masterdata/ 의 모든 파일을 센다. 파생물도 거기 든다.
            content = true;
        }

        // 프리픽스가 바뀌면 파일별 판정을 볼 것도 없이 전량이다 — 다른 어휘로 만든 플랜이다.
        if (prefix)
        {
            scope = InvalidationScope.Full;
        }

        return new Impact(
            changed,
            scope,
            prefix,
            structural,
            content,
            [.. Regenerate(masterDataDirectory, changed)]);
    }

    /// <summary>
    /// 이 변경 때문에 낡아지는 파생물. <b>디스크의 현재 상태도 같이 본다</b> —
    /// 이번 변경과 무관하게 이미 낡아 있었다면 그것도 다시 만들어야 한다.
    /// </summary>
    private static IEnumerable<DerivedStatus> Regenerate(
        string masterDataDirectory, ImmutableArray<string> changed)
    {
        ImmutableArray<DerivedStatus> current = DerivedArtifacts.Check(masterDataDirectory);

        foreach (DerivedEntry known in DerivedArtifacts.Known)
        {
            DerivedStatus status = current.FirstOrDefault(
                s => string.Equals(s.Artifact, known.Artifact, StringComparison.Ordinal));

            if (status.Stale)
            {
                yield return status;
                continue;
            }

            // 아직 안 낡았지만 이번 변경이 입력을 건드린다면, 적용 후 낡는다.
            foreach (string input in known.Inputs.Keys)
            {
                if (changed.Contains(input, StringComparer.OrdinalIgnoreCase))
                {
                    yield return new DerivedStatus(
                        known.Artifact, true, $"{input} 이 바뀐다", known.Generator);
                    break;
                }
            }
        }
    }

    /// <summary>사람이 읽는 파급 보고. <c>npc diff --semantic</c> 이 이것을 낸다.</summary>
    public static string Describe(in Impact impact)
    {
        var lines = new List<string>(8)
        {
            impact.Changed.IsEmpty
                ? "바뀐 파일 없음"
                : "바뀐 파일  " + string.Join(" · ", impact.Changed),
        };

        lines.Add(
            "구조 해시  "
            + (impact.StructuralChanged
                ? "변경 → 게임서버 재배포 필요 (핸드셰이크 거절)"
                : "그대로 → 게임서버 재배포 불필요"));

        lines.Add(
            "프리픽스   "
            + (impact.PrefixChanged
                ? "변경 → 플랜 스토어 전량 무효 → 프리베이크 필요"
                : "그대로 → 프리픽스 캐시 유지"));

        lines.Add(
            "플랜       " + impact.Scope switch
            {
                InvalidationScope.None => "무효화 없음",
                InvalidationScope.Partial => "부분 무효 → 바뀐 것만 재생성 (`--resume`)",
                _ => "전량 무효 → 전량 재생성",
            });

        lines.Add(
            "파생물     "
            + (impact.Regenerate.IsEmpty
                ? "최신"
                : string.Join(" · ", impact.Regenerate.Select(r => r.Artifact + " 낡음")) + " → `npc regen`"));

        return string.Join(Environment.NewLine, lines);
    }
}
