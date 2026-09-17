using System.Collections.Immutable;
using System.Text.Json;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.Core.Validation;
using Npc.MasterData;

namespace Npc.Studio.Services;

/// <summary>버킷 한 칸이 어떤 상태인가 (T27).</summary>
public enum BucketState
{
    /// <summary>만들어진 플랜이 없다. 런타임은 폴백으로 돈다.</summary>
    Missing,

    /// <summary>LLM 이 만들었다.</summary>
    Generated,

    /// <summary>사람이 손보고 고정했다.</summary>
    Pinned,

    /// <summary>검증에 떨어져 반려됐다.</summary>
    Rejected,

    /// <summary>
    /// 파일은 있는데 읽거나 컴파일하지 못했다 (H20).
    /// <b>"없음" 과 다르다</b> — 없음은 폴백으로 돌지만 이것은 낡은 파일이라는 뜻이다.
    /// </summary>
    Broken,
}

/// <summary>버킷 격자의 한 칸 (T27).</summary>
/// <param name="Bucket">버킷 키.</param>
/// <param name="State">상태.</param>
/// <param name="Goal">플랜의 목표. 없으면 빈 문자열.</param>
/// <param name="File">읽어 온 파일 경로.</param>
public sealed record BucketCell(BucketKey Bucket, BucketState State, string Goal, string File);

/// <summary>
/// 미리 구운 플랜을 읽는다 (T27).
///
/// <para>
/// <b><c>Npc.Planning</c> 을 참조하지 않는다</b> (CLAUDE.md §3). 파일을 직접 읽고
/// <c>Npc.Core</c> 의 <see cref="SchemaValidator"/>·<see cref="PlanCompiler"/> 로 컴파일한다 —
/// 실제 서버에서 NPC 행동의 98.67% 가 여기서 나오므로, "이 직업이 폭풍 치는 전쟁 중 오후에
/// 무엇을 하나" 의 참 답은 폴백이 아니라 이 파일들이다.
/// </para>
/// </summary>
public sealed class PlanStoreReader(StudioOptions options, StudioWorkspace workspace)
{
    /// <summary>플랜 스토어 경로. 없으면 화면이 탭을 숨긴다.</summary>
    public string Directory => options.PlanStore;

    /// <summary>플랜 스토어가 있는가.</summary>
    public bool Available => System.IO.Directory.Exists(options.PlanStore);

    /// <summary>이 아키타입의 버킷 72칸. 파일이 없는 칸은 <see cref="BucketState.Missing"/> 다.</summary>
    /// <param name="archetypeId">직업 id.</param>
    public ImmutableArray<BucketCell> Grid(string archetypeId)
    {
        ArgumentException.ThrowIfNullOrEmpty(archetypeId);

        return workspace.WithData((data, _) =>
        {
            if (!Available || !data.Archetypes.TryGet(archetypeId, out ArchetypeDef def))
            {
                return ImmutableArray<BucketCell>.Empty;
            }

            var cells = ImmutableArray.CreateBuilder<BucketCell>(BucketKey.PerArchetype);

            foreach (TimeOfDay time in Enum.GetValues<TimeOfDay>())
            {
                foreach (RegionState region in Enum.GetValues<RegionState>())
                {
                    foreach (Climate climate in Enum.GetValues<Climate>())
                    {
                        var key = new BucketKey(def.Code, time, region, climate);

                        cells.Add(Find(archetypeId, key));
                    }
                }
            }

            return cells.ToImmutable();
        });
    }

    /// <summary>한 버킷의 플랜을 컴파일한다. 없으면 null.</summary>
    /// <param name="archetypeId">직업 id.</param>
    /// <param name="bucket">버킷.</param>
    public CompiledPlan? Compile(string archetypeId, BucketKey bucket)
    {
        ArgumentException.ThrowIfNullOrEmpty(archetypeId);

        BucketCell cell = Find(archetypeId, bucket);

        if (cell.State is BucketState.Missing or BucketState.Broken || cell.File.Length == 0)
        {
            return null;
        }

        return workspace.WithData((data, _) => Compile(data, cell.File, bucket, Origin(cell.State)));
    }

    /// <summary>
    /// 파일 한 장을 <see cref="CompiledPlan"/> 으로. 스키마 검증(1단) → 컴파일 순서다.
    /// </summary>
    /// <param name="data">마스터데이터. 어휘를 준다.</param>
    /// <param name="path">플랜 파일.</param>
    /// <param name="bucket">버킷.</param>
    /// <param name="origin">출처.</param>
    public static CompiledPlan? Compile(MasterDataSet data, string path, BucketKey bucket, PlanOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (Unwrap(File.ReadAllText(path)) is not { Length: > 0 } json)
        {
            return null;
        }

        ValidationResult result = SchemaValidator.Validate(json, out PlanDocument? document);

        if (!result.IsValid || document is null)
        {
            return null;
        }

        try
        {
            return PlanCompiler.Compile(document, bucket, new PlanId(0), data, origin);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private BucketCell Find(string archetypeId, BucketKey bucket)
    {
        string name = bucket.Format(archetypeId) + ".json";

        foreach ((string folder, BucketState state) in Layers())
        {
            string path = Path.Combine(folder, name);

            if (File.Exists(path))
            {
                return Readable(path)
                    ? new BucketCell(bucket, state, Goal(path), path)
                    : new BucketCell(bucket, BucketState.Broken, string.Empty, path);
            }
        }

        return new BucketCell(bucket, BucketState.Missing, string.Empty, string.Empty);
    }

    /// <summary>
    /// 찾는 순서. 핀이 먼저다 — 사람이 손본 것이 생성물을 이긴다.
    /// C-03 이후 생성물은 프리픽스 SHA 별 폴더에 들어간다.
    /// </summary>
    private IEnumerable<(string Folder, BucketState State)> Layers()
    {
        yield return (Path.Combine(options.PlanStore, "pinned"), BucketState.Pinned);
        yield return (Path.Combine(options.PlanStore, "plans"), BucketState.Generated);

        if (!System.IO.Directory.Exists(options.PlanStore))
        {
            yield break;
        }

        foreach (string sub in System.IO.Directory
            .EnumerateDirectories(options.PlanStore)
            .OrderBy(d => d, StringComparer.Ordinal))
        {
            string plans = Path.Combine(sub, "plans");

            if (System.IO.Directory.Exists(plans))
            {
                yield return (plans, BucketState.Generated);
            }

            string rejected = Path.Combine(sub, "rejected");

            if (System.IO.Directory.Exists(rejected))
            {
                yield return (rejected, BucketState.Rejected);
            }
        }

        yield return (Path.Combine(options.PlanStore, "rejected"), BucketState.Rejected);
    }

    private static PlanOrigin Origin(BucketState state) =>
        state == BucketState.Pinned ? PlanOrigin.Pinned : PlanOrigin.Prebaked;

    /// <summary>이 파일이 JSON 으로 읽히는가. 아니면 격자에 "깨짐" 으로 그린다 (H20).</summary>
    private static bool Readable(string path)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));

            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return false;
        }
    }

    /// <summary>봉투를 벗긴다. 파일은 <c>{bucket, archetype, origin, plan:{…}}</c> 형식이다.</summary>
    private static string? Unwrap(string raw)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(raw);

            return document.RootElement.TryGetProperty("plan", out JsonElement plan)
                ? plan.GetRawText()
                : raw;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Goal(string path)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));

            return document.RootElement.TryGetProperty("plan", out JsonElement plan)
                && plan.TryGetProperty("goal", out JsonElement goal)
                ? goal.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return string.Empty;
        }
    }
}
