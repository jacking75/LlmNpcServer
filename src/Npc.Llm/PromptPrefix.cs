using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.ML.Tokenizers;
using Npc.Core.Plan;
using Npc.MasterData;

namespace Npc.Llm;

/// <summary>프리픽스 한 절의 토큰 수. 예산을 어디서 잡아먹는지 보려고 남긴다.</summary>
/// <param name="Name">절 이름.</param>
/// <param name="Tokens">o200k_base 기준 토큰 수.</param>
public readonly record struct PrefixSection(string Name, int Tokens);

/// <summary>
/// 고정 프롬프트 프리픽스. docs/01 §10 · docs/12 §3.
///
/// <b>기동 시 1회 조립하고 그 뒤 절대 재조립하지 않는다.</b> 1바이트라도 흔들리면
/// 프롬프트 캐시가 전면 미적중이 되고 비용이 그대로 튄다 (docs/01 §10.2).
/// 그래서 이 타입은 불변이고, 재시도 피드백 같은 가변 정보는 서픽스에만 붙는다
/// (CLAUDE.md §2.5).
///
/// 조립 순서 (docs/01 §10.1):
/// <list type="number">
///   <item><c>masterdata/prompt/system_rules.md</c></item>
///   <item>액션 카탈로그 — <see cref="CatalogRenderer"/> 가 <c>actions.json</c>·<c>items.json</c>에서 생성</item>
///   <item>아키타입별 허용 액션 표 — <c>archetypes.json</c>에서 생성</item>
///   <item>DSL 요약 + 실제로 쓰는 JSON Schema — <see cref="SchemaProvider"/></item>
///   <item><c>masterdata/prompt/fewshot/*.json</c> 3건</item>
/// </list>
/// </summary>
public sealed class PromptPrefix
{
    /// <summary>docs/01 §10.1 · CLAUDE.md §2.5 의 캐시 최소 임계. 미달하면 캐시가 아예 안 걸린다.</summary>
    public const int TokenFloor = 4_096;

    /// <summary>프롬프트 자산이 있는 하위 폴더 이름.</summary>
    public const string PromptFolderName = "prompt";

    /// <summary>
    /// 토큰 계측기. 로컬 모델(Qwen/Phi)은 서로 다른 vocab 을 쓰므로 이 값은 <b>기준치</b>다 —
    /// 엔진별 실제 prompt_tokens 는 <see cref="CompileStats"/> 가 usage 에서 받는다.
    /// </summary>
    private static readonly TiktokenTokenizer s_tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");

    private PromptPrefix(string text, ImmutableArray<PrefixSection> sections, SchemaProvider schema)
    {
        Text = text;
        Sha256 = HashOf(text);
        TokenCount = CountTokens(text);
        Sections = sections;
        Schema = schema;
    }

    /// <summary>조립 결과. 프로세스 생애 동안 불변.</summary>
    public string Text { get; }

    /// <summary>로그·메트릭에 항상 붙인다. <b>유니크 해시가 2개 이상이면 즉시 경보</b>다.</summary>
    public string Sha256 { get; }

    /// <summary>o200k_base 기준 토큰 수. 하한 <see cref="TokenFloor"/>.</summary>
    public int TokenCount { get; }

    /// <summary>절별 토큰 수.</summary>
    public ImmutableArray<PrefixSection> Sections { get; }

    /// <summary>프리픽스에 실린 스키마. 컴파일러가 강제 디코딩에 쓸 때 같은 것을 넘긴다.</summary>
    public SchemaProvider Schema { get; }

    /// <summary>o200k_base 토큰 수.</summary>
    public static int CountTokens(string text) => s_tokenizer.CountTokens(text);

    /// <summary>마스터데이터에서 프리픽스를 조립한다. <b>기동 시 1회.</b></summary>
    /// <param name="data">로드된 마스터데이터.</param>
    /// <param name="masterDataDirectory"><c>masterdata/</c> 경로. <c>prompt/</c> 를 그 아래에서 찾는다.</param>
    /// <param name="profile">스키마 변종.</param>
    public static PromptPrefix Build(
        MasterDataSet data,
        string masterDataDirectory,
        SchemaProfile profile = SchemaProfile.Full)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrEmpty(masterDataDirectory);

        string promptDirectory = Path.Combine(masterDataDirectory, PromptFolderName);
        SchemaProvider schema = SchemaProvider.Build(data, profile);

        var parts = new List<(string Name, string Text)>(5)
        {
            ("system_rules", ReadText(Path.Combine(promptDirectory, "system_rules.md"))),
            ("action_catalog", CatalogRenderer.Render(data)),
            ("archetypes", RenderArchetypes(data)),
            ("dsl_summary", RenderDsl(schema)),
            ("fewshot", RenderFewShots(Path.Combine(promptDirectory, "fewshot"))),
        };

        var sb = new StringBuilder(128 * 1024);
        var sections = ImmutableArray.CreateBuilder<PrefixSection>(parts.Count);

        foreach ((string name, string text) in parts)
        {
            sb.Append(text).Append('\n');
            sections.Add(new PrefixSection(name, CountTokens(text)));
        }

        return new PromptPrefix(sb.ToString(), sections.ToImmutable(), schema);
    }

    /// <summary>
    /// 아키타입별 허용 액션 표. docs/12 §8 의 <c>V2.ACTION_NOT_ALLOWED</c> 처방이다.
    ///
    /// <b>서픽스가 아니라 프리픽스에 둔다.</b> 아키타입당 20여 개 id 를 서픽스에 실으면
    /// 개별 재계획 요청이 300토큰 예산을 넘긴다 (docs/12 §3). 이 표는 완전히 정적이라
    /// 캐시에 그대로 얹힌다.
    /// </summary>
    private static string RenderArchetypes(MasterDataSet data)
    {
        var sb = new StringBuilder(16 * 1024);

        sb.Append("# ARCHETYPES\n\n");
        sb.Append("The request names one archetype. Use only the actions listed on its row -\n");
        sb.Append("every other catalog action is rejected for it. `workplace` is what `$workplace`\n");
        sb.Append("binds to, and `recipes` are the ones that archetype normally makes.\n\n");

        foreach (ArchetypeDef archetype in data.Archetypes.Archetypes)
        {
            sb.Append("## ").Append(archetype.Id).Append('\n');
            sb.Append("workplace: ").Append(archetype.WorkplacePoiType ?? "none");

            if (archetype.PrimaryRecipes.Length > 0)
            {
                sb.Append(" | recipes: ").Append(string.Join(", ", archetype.PrimaryRecipes));
            }

            sb.Append('\n');
            sb.Append("actions: ");

            // AllowedActions 는 archetypes.json 의 등장 순서다. 정렬하면 파일 순서와 어긋나므로 그대로 쓴다.
            for (int i = 0; i < archetype.AllowedActions.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(data.ActionName(archetype.AllowedActions[i]));
            }

            sb.Append("\n\n");
        }

        return sb.ToString();
    }

    /// <summary>docs/03 §2 의 스키마를 사람이 읽는 형태로 요약하고, 실제 스키마 본문도 같이 싣는다.</summary>
    private static string RenderDsl(SchemaProvider schema)
    {
        // $$"""…""" — 중괄호가 잔뜩 나오는 본문이라 보간 구멍을 {{…}} 로 둔다.
        return $$"""
            # PLAN FORMAT

            Output exactly one JSON object with these fields.

            - `schema` - always the integer 1.
            - `goal` - snake_case id, 3..32 chars, matching ^[a-z][a-z0-9_]{2,31}$.
            - `reasoning` - at most {{PlanDocument.MaxReasoningLength}} characters, for human reviewers only.
            - `steps` - {{PlanDocument.MinSteps}} to {{PlanDocument.MaxSteps}} objects, executed strictly in
              order. No branches and no loops inside the list; when the situation changes the
              server replans.
            - `on_step_fail` - one of fallback, retry_once, skip, replan.
            - `loop` - boolean. true means the plan repeats until something interrupts it,
              which is what a daily routine wants.

            Each step is `{"action": <id>, "args": {...}, "timeout_s": <int {{PlanDocument.MinTimeoutSeconds}}..{{PlanDocument.MaxTimeoutSeconds}}>}`.
            `timeout_s` is optional; when omitted the catalog default is used. If no event
            arrives within it the runtime synthesises a failure, so a value shorter than the
            action's real duration will break the plan.

            The schema below lists the argument names that exist at all. It does not say which
            action takes which argument - the catalog above does, and a validator rejects any
            argument the action does not declare.

            ```json
            {{schema.PlanSchemaJson}}
            ```

            """;
    }

    /// <summary>
    /// few-shot 예시. <b>파일명 정렬 순서로</b> 싣는다 —
    /// 디렉터리 순회 순서에 의존하면 기계마다 SHA 가 달라진다.
    /// </summary>
    private static string RenderFewShots(string directory)
    {
        string[] files = [.. Directory.GetFiles(directory, "*.json")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)];

        if (files.Length == 0)
        {
            throw new InvalidDataException($"few-shot 예시가 하나도 없다: {directory}");
        }

        var sb = new StringBuilder(16 * 1024);
        var compact = new JsonSerializerOptions { WriteIndented = false };

        sb.Append("# EXAMPLES\n\n");
        sb.Append("Each example is one request and the plan it should produce.\n");

        for (int i = 0; i < files.Length; i++)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(files[i]));

            sb.Append("\n## example ").Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append("\n\nrequest:\n");
            sb.Append(JsonSerializer.Serialize(document.RootElement.GetProperty("request"), compact));

            // 반려→수정 예시. docs/12 §8 의 V3.PRECONDITION_UNMET 처방이다 —
            // 무엇이 왜 반려됐고 어떻게 고치는지를 한 자리에서 보여 준다.
            if (document.RootElement.TryGetProperty("rejected_plan", out JsonElement rejected))
            {
                sb.Append("\n\nrejected plan:\n");
                sb.Append(JsonSerializer.Serialize(rejected, compact));
                sb.Append("\n\nwhy it was rejected:\n");
                sb.Append(JsonSerializer.Serialize(document.RootElement.GetProperty("rejection"), compact));
                sb.Append("\n\ncorrected plan:\n");
            }
            else
            {
                sb.Append("\n\nplan:\n");
            }

            sb.Append(JsonSerializer.Serialize(document.RootElement.GetProperty("plan"), compact));
            sb.Append('\n');
        }

        sb.Append("\nThe examples differ in shape, not only in wording: the situation decides where\n");
        sb.Append("the NPC goes, how long it works, and when it stops for the day.\n");

        return sb.ToString();
    }

    /// <summary>줄바꿈을 LF 로 통일한다. CRLF 로 체크아웃된 기계에서 SHA 가 달라지면 안 된다.</summary>
    private static string ReadText(string path) =>
        File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n";

    private static string HashOf(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
