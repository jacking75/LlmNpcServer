using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Npc.Eval.Core;
using Npc.Llm;

namespace Npc.Eval;

/// <summary>
/// LLM 심사원 (C-04 단계 6).
///
/// <para>
/// <b>권고 신호이지 게이트가 아니다.</b> 루브릭 채점으로 배포를 막으면 심사 모델이 바뀔 때마다
/// 기준이 소리 없이 움직인다 — 통과율·다양성·골든은 우리가 정의한 결정론적 판정이지만
/// 이 점수는 다른 모델의 의견이다. 보고서에 싣되 <see cref="EvalGate"/> 에서는 뺀다.
/// </para>
///
/// <para>
/// <b>플레이어 문자열이 들어갈 자리가 없다</b> (CLAUDE.md §2.5). 심사원이 보는 것은
/// 아키타입 id·버킷 좌표·액션 시퀀스·goal 뿐이고, 전부 우리 어휘다.
/// </para>
///
/// <para>
/// 루브릭 4항목은 <c>masterdata/prompt/system_rules.md</c> 의 "WHAT MAKES A PLAN GOOD" 이다.
/// <b>여기에 그 문장을 다시 쓰지 않는다</b> — 두 벌이 되면 반드시 어긋난다.
/// </para>
/// </summary>
public static class PlanJudge
{
    /// <summary>한 번에 채점할 플랜 수. 너무 많으면 모델이 뒤쪽을 대충 본다.</summary>
    public const int BatchSize = 10;

    /// <summary>점수 하한·상한.</summary>
    public const int MinScore = 1;

    /// <summary>점수 상한.</summary>
    public const int MaxScore = 5;

    /// <summary>
    /// 표본을 채점한다. 평균 점수를 1~5 로 돌려준다. 채점할 것이 없으면 -1.
    /// </summary>
    /// <param name="samples">표본. <b>생성에 성공한 것만</b> 본다 — 폴백은 사람이 쓴 것이다.</param>
    /// <param name="rubric"><c>system_rules.md</c> 에서 뽑은 루브릭 원문.</param>
    /// <param name="client">심사원 클라이언트.</param>
    /// <param name="cancellationToken">취소.</param>
    public static async Task<double> ScoreAsync(
        ImmutableArray<PlanSample> samples,
        string rubric,
        IChatClient client,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(rubric);

        PlanSample[] judged = [.. samples.Where(s => s.CountsForDiversity)];

        if (judged.Length == 0)
        {
            return -1;
        }

        double total = 0;
        int counted = 0;

        for (int at = 0; at < judged.Length; at += BatchSize)
        {
            PlanSample[] batch = [.. judged.Skip(at).Take(BatchSize)];

            IReadOnlyList<int> scores = await ScoreBatchAsync(
                batch, rubric, client, cancellationToken).ConfigureAwait(false);

            foreach (int score in scores)
            {
                total += Math.Clamp(score, MinScore, MaxScore);
                counted++;
            }
        }

        return counted == 0 ? -1 : total / counted;
    }

    /// <summary>
    /// 프롬프트를 만든다. <b>공개로 둔다</b> — 무엇을 물었는지 보여 주지 않는 채점은
    /// 재현할 수 없고, 재현할 수 없는 숫자는 보고서에 실을 값이 아니다.
    /// </summary>
    /// <param name="batch">채점할 플랜.</param>
    /// <param name="rubric">루브릭 원문.</param>
    public static string Prompt(IReadOnlyList<PlanSample> batch, string rubric)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var text = new StringBuilder(4 * 1024);

        text.Append("You score NPC daily plans against a rubric. ");
        text.Append("Answer with JSON only: {\"scores\":[n,...]} where each n is 1..5, ");
        text.Append("one entry per plan, in the order given. No prose.\n\n");
        text.Append("RUBRIC\n\n");
        text.Append(rubric);
        text.Append("\n\nPLANS\n\n");

        for (int i = 0; i < batch.Count; i++)
        {
            PlanSample sample = batch[i];

            text.Append(CultureInfo.InvariantCulture,
                $"{i + 1}. bucket={sample.Bucket} goal={sample.Goal} actions={sample.Actions}\n");
        }

        return text.ToString();
    }

    private static async Task<IReadOnlyList<int>> ScoreBatchAsync(
        IReadOnlyList<PlanSample> batch,
        string rubric,
        IChatClient client,
        CancellationToken cancellationToken)
    {
        ChatResponse response = await client
            .GetResponseAsync(Prompt(batch, rubric), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return Parse(response.Text, batch.Count);
    }

    /// <summary>
    /// 응답에서 점수를 뽑는다. <b>못 읽으면 빈 목록이다</b> — 모르는 응답을 3점으로
    /// 채우면 평균이 중앙으로 끌려가 채점이 아무 말도 안 하게 된다.
    /// </summary>
    /// <param name="text">모델 응답.</param>
    /// <param name="expected">기대하는 점수 개수.</param>
    internal static IReadOnlyList<int> Parse(string? text, int expected)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        int start = text.IndexOf('{', StringComparison.Ordinal);
        int end = text.LastIndexOf('}');

        if (start < 0 || end <= start)
        {
            return [];
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(text[start..(end + 1)]);

            if (!document.RootElement.TryGetProperty("scores", out JsonElement scores)
                || scores.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var parsed = new List<int>(expected);

            foreach (JsonElement item in scores.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out int score))
                {
                    parsed.Add(score);
                }
            }

            // 개수가 다르면 어느 플랜의 점수인지 알 수 없다. 통째로 버린다.
            return parsed.Count == expected ? parsed : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
