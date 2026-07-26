using System.Globalization;
using System.Text;
using System.Text.Json;
using Npc.Core;
using Npc.Core.Validation;
using Npc.MasterData;

namespace Npc.Llm;

/// <summary>
/// 검증에 실패한 산출물 보존. docs/03 §7 · docs/13 §8.
///
/// <b>조용히 버리면 품질 개선의 원자료가 사라진다.</b> 통과율을 올리려면 무엇이 왜 걸렸는지를
/// 봐야 하고(T2-20·T2-21), 그 근거는 모델이 실제로 뱉은 문장뿐이다.
///
/// 파일명은 <c>&lt;bucket&gt;.&lt;attempt&gt;.json</c> —
/// 같은 버킷의 시도 1과 시도 2가 서로를 덮어쓰지 않는다.
/// <b>시각을 넣지 않는다</b> (CLAUDE.md §2.3): 같은 입력이면 같은 파일이 나와야 diff 가 의미를 갖는다.
/// </summary>
public sealed class RejectedStore
{
    private readonly string _directory;
    private readonly MasterDataSet _data;

    /// <summary>보존 위치를 정한다. 폴더는 첫 쓰기 때 만든다.</summary>
    /// <param name="directory"><c>planstore/rejected/</c> 경로.</param>
    /// <param name="data">아키타입 id 를 파일명에 쓰기 위해 필요하다.</param>
    public RejectedStore(string directory, MasterDataSet data)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(data);

        _directory = directory;
        _data = data;
    }

    /// <summary>기본 위치(<c>planstore/rejected</c>)에 만든다.</summary>
    public static RejectedStore CreateDefault(string planStoreDirectory, MasterDataSet data) =>
        new(Path.Combine(planStoreDirectory, "rejected"), data);

    /// <summary>여기까지 보존한 건수.</summary>
    public int Count { get; private set; }

    /// <summary>보존 폴더.</summary>
    public string Directory => _directory;

    /// <summary>이 버킷·시도의 파일 경로.</summary>
    public string PathOf(BucketKey bucket, int attempt) =>
        Path.Combine(
            _directory,
            $"{bucket.Format(_data.Archetypes[bucket.A].Id)}.{attempt.ToString(CultureInfo.InvariantCulture)}.json");

    /// <summary>실패 하나를 보존한다. 통과한 결과를 주면 아무 일도 하지 않는다.</summary>
    public void Save(BucketKey bucket, in CompileStats stats, in ValidationResult validation, string responseText)
    {
        if (validation.IsValid)
        {
            return;
        }

        System.IO.Directory.CreateDirectory(_directory);

        var buffer = new MemoryStream(4 * 1024);

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();

            writer.WriteString("bucket", bucket.Format(_data.Archetypes[bucket.A].Id));
            writer.WriteString("archetype", _data.Archetypes[bucket.A].Id);
            writer.WriteNumber("attempt", stats.Attempt);

            writer.WriteString("stage", validation.FailedAt.ToString());
            writer.WriteString("code", validation.Code);
            writer.WriteNumber("step", validation.StepIndex);
            writer.WriteString("detail", validation.Detail);

            writer.WriteString("model", stats.Model);
            writer.WriteString("prefix_sha", stats.PrefixSha);
            writer.WriteBoolean("forced_decoding", stats.Forced);
            writer.WriteNumber("prompt_tokens", stats.PromptTokens);
            writer.WriteNumber("cached_tokens", stats.CachedTokens);
            writer.WriteNumber("completion_tokens", stats.CompletionTokens);
            writer.WriteNumber("latency_ms", Math.Round(stats.LatencyMs, 1));
            writer.WriteNumber("cost_usd", Math.Round(stats.CostUsd, 8));

            if (stats.Error is { } error)
            {
                writer.WriteString("call_error", error);
            }

            // 모델이 실제로 뱉은 문장. 이것이 없으면 실패 코드만 남고 원인을 못 본다.
            writer.WriteString("response", responseText);

            writer.WriteEndObject();
        }

        File.WriteAllText(PathOf(bucket, stats.Attempt), Encoding.UTF8.GetString(buffer.ToArray()));
        Count++;
    }
}
