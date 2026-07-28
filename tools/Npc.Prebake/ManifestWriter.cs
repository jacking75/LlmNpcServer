using System.Globalization;
using System.Text;
using Npc.Core.Validation;
using Npc.Llm;
using Npc.MasterData;
using Npc.Planning;

namespace Npc.Prebake;

/// <summary>
/// manifest 작성. docs/03 §7 · T3-16.
///
/// <b>이 파일이 곧 R&amp;D 보고서의 원자료다.</b> 프리베이크를 돌 때마다 보존한다 —
/// <c>manifest.json</c> 은 최신 회차이고, 매 회차의 요약은 <c>manifest_history.jsonl</c> 에 한 줄씩 쌓인다.
/// 덮어쓰기만 하면 "지난주 회차는 몇 %였나" 를 다시 물을 수 없다.
///
/// <para>
/// <b>생성 시각은 밖에서 온다.</b> 이 클래스도 <see cref="Manifest"/> 도 시계를 부르지 않는다
/// (CLAUDE.md §2.3 · docs/13 §8 의 "흔한 실수" 표).
/// </para>
/// </summary>
public static class ManifestWriter
{
    /// <summary>회차별 요약이 쌓이는 파일. 사람이 손으로 고치지 않는다.</summary>
    public const string HistoryFileName = "manifest_history.jsonl";

    /// <summary>
    /// 회차 결과를 manifest 로 조립한다.
    /// </summary>
    /// <param name="data">마스터데이터. 해시와 파일별 해시가 여기서 온다.</param>
    /// <param name="prefix">프롬프트 프리픽스.</param>
    /// <param name="engine">엔진 설정.</param>
    /// <param name="tier">티어 표기 (T1 · T2).</param>
    /// <param name="report">생성 회차 결과.</param>
    /// <param name="dryRun">전수 드라이런 결과.</param>
    /// <param name="store">최종 스토어. 버킷 집계가 여기서 나온다.</param>
    /// <param name="generatedAt">생성 시각 문자열. <b>외부 주입</b>.</param>
    public static Manifest Build(
        MasterDataSet data,
        PromptPrefix prefix,
        LlmEngineOptions engine,
        string tier,
        BulkRunReport report,
        DryRunReport dryRun,
        PlanStore store,
        string generatedAt,
        int target = 0)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(store);

        // store 는 저장 뒤 디스크에서 다시 읽은 것이어야 한다 — 러너의 스토어는 이번 회차 몫만
        // 들고 있어서 증분 --only 회차의 counts.generated 가 이전 회차를 잊는다 (결정 13).
        int pinned = store.PinnedBuckets;
        int filled = store.FilledBuckets;

        return Manifest.For(
            data,
            prefix.Sha256,
            new ManifestGeneratedBy(
                tier,
                engine.Id,
                engine.Temperature,
                report.StartConcurrency,
                report.PeakConcurrency,
                report.FirstRateLimitConcurrency),
            generatedAt) with
        {
            Counts = new ManifestCounts(
                Total: Npc.Core.BucketKey.TotalKeys,
                Generated: Math.Max(0, filled - pinned),
                Pinned: pinned,
                Fallback: report.FellBack,
                Reused: report.Reused,
                Target: target),
            Validation = CountValidation(report, dryRun),
            CostUsd = report.CostUsd,
            WallClockSeconds = report.WallClockSeconds,
            CacheHitRate = report.CacheHitRate,
            Partial = report.StoppedByBudget,
        };
    }

    /// <summary>
    /// manifest 를 쓰고 회차 요약을 이력에 한 줄 붙인다.
    /// </summary>
    /// <param name="planStoreDirectory"><c>planstore/</c> 경로.</param>
    /// <param name="manifest">쓸 manifest.</param>
    /// <param name="dryRun">이력에 남길 드라이런 결과.</param>
    /// <returns>쓴 manifest 파일 경로.</returns>
    public static string Save(string planStoreDirectory, Manifest manifest, DryRunReport dryRun)
    {
        ArgumentException.ThrowIfNullOrEmpty(planStoreDirectory);
        ArgumentNullException.ThrowIfNull(manifest);

        Directory.CreateDirectory(planStoreDirectory);

        string path = Path.Combine(planStoreDirectory, Manifest.FileName);

        manifest.Save(path);
        AppendHistory(planStoreDirectory, manifest, dryRun);

        return path;
    }

    /// <summary>회차 요약 한 줄. 덮어쓰기만 하면 지난 회차 숫자를 다시 못 본다.</summary>
    public static void AppendHistory(string planStoreDirectory, Manifest manifest, DryRunReport dryRun)
    {
        ArgumentException.ThrowIfNullOrEmpty(planStoreDirectory);
        ArgumentNullException.ThrowIfNull(manifest);

        Directory.CreateDirectory(planStoreDirectory);

        var line = new StringBuilder(512);
        var invariant = CultureInfo.InvariantCulture;

        line.Append(invariant, $$"""
            {"generated_at":"{{Escape(manifest.GeneratedAt)}}","model":"{{Escape(manifest.GeneratedBy.Model)}}","tier":"{{Escape(manifest.GeneratedBy.Tier)}}","prefix_hash":"{{Escape(manifest.PrefixHash)}}","masterdata_hash":"{{Escape(manifest.MasterdataHash)}}","partial":{{(manifest.Partial ? "true" : "false")}},"generated":{{manifest.Counts.Generated}},"pinned":{{manifest.Counts.Pinned}},"fallback":{{manifest.Counts.Fallback}},"reused":{{manifest.Counts.Reused}},"pass":{{manifest.Validation.Pass}},"fail_schema":{{manifest.Validation.FailSchema}},"fail_vocab":{{manifest.Validation.FailVocab}},"fail_coherence":{{manifest.Validation.FailCoherence}},"fail_dryrun":{{manifest.Validation.FailDryRun}},"fail_call":{{manifest.Validation.FailCall}},"cost_usd":{{manifest.CostUsd.ToString("F6", invariant)}},"wall_clock_s":{{manifest.WallClockSeconds.ToString("F1", invariant)}},"cache_hit_rate":{{manifest.CacheHitRate.ToString("F4", invariant)}},"dryrun_checked":{{dryRun.Checked}},"dryrun_failed":{{dryRun.Failed}},"dryrun_s":{{dryRun.WallClockSeconds.ToString("F2", invariant)}},"concurrency":{{manifest.GeneratedBy.Concurrency}},"peak_concurrency":{{manifest.GeneratedBy.PeakConcurrency}},"first_rate_limit_concurrency":{{manifest.GeneratedBy.FirstRateLimitConcurrency}}}
            """);
        line.Append('\n');

        // BOM 없이 쓴다. jsonl 첫 줄에 BOM 이 붙으면 jq 가 첫 글자에서 걸린다.
        File.AppendAllText(
            Path.Combine(planStoreDirectory, HistoryFileName),
            line.ToString(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>
    /// 검증 단계별 집계. 생성 회차의 최종 판정 + 전수 드라이런에서 새로 걸린 것을 합친다.
    ///
    /// <b>드라이런 실패는 따로 센다.</b> 생성 단계에서 통과했는데 전수 스윕에서 걸린 것은
    /// 인접 재사용·폴백으로 채운 버킷이고, 그것이 몇 개인지가 재사용 정책의 성적표다.
    /// </summary>
    private static ManifestValidation CountValidation(BulkRunReport report, DryRunReport dryRun)
    {
        int pass = 0;
        int schema = 0;
        int vocab = 0;
        int coherence = 0;
        int dry = 0;
        int call = 0;

        foreach (BucketOutcome outcome in report.Outcomes)
        {
            if (outcome.Stats.Attempt == 0)
            {
                continue;   // 예산 캡에 걸려 던지지도 못했다. 실패로 세지 않는다
            }

            if (outcome.Validation.IsValid)
            {
                pass++;
                continue;
            }

            if (!outcome.Stats.Reached)
            {
                call++;
                continue;
            }

            switch (outcome.Validation.FailedAt)
            {
                case ValidationStage.Schema:
                    schema++;
                    break;

                case ValidationStage.Vocabulary:
                    vocab++;
                    break;

                case ValidationStage.Coherence:
                    coherence++;
                    break;

                case ValidationStage.DryRun:
                    dry++;
                    break;

                default:
                    schema++;
                    break;
            }
        }

        // 전수 스윕에서 새로 걸린 것. 생성 단계 집계와 겹치지 않는다.
        dry += dryRun.Failed;

        return new ManifestValidation(pass, schema, vocab, coherence, dry, call);
    }

    private static string Escape(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
}
