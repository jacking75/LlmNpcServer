using System.ComponentModel;
using ModelContextProtocol.Server;
using Npc.Cli;

namespace Npc.Mcp.Tools;

/// <summary>
/// 플랜 툴 (E-03).
///
/// <para>
/// <b>플랜은 파일이 아니라 문자열로 온다.</b> 모델이 방금 만든 것을 검증하려는 것이므로
/// 어디에도 저장돼 있지 않다 — 임시 파일에 써서 CLI 에 넘긴다. CLI 를 "문자열도 받게"
/// 고치는 대신 이렇게 하는 이유는, <b>껍질이 코어의 모양을 바꾸지 않게</b> 하기 위해서다.
/// </para>
/// </summary>
[McpServerToolType]
public sealed class PlanTools
{
    private readonly McpOptions _options;

    /// <summary>DI 가 만든다.</summary>
    /// <param name="options">서버 설정.</param>
    public PlanTools(McpOptions options) => _options = options;

    /// <summary>4단 검증.</summary>
    /// <param name="plan">플랜 JSON.</param>
    /// <param name="bucket">버킷 키.</param>
    [McpServerTool(Name = "plan_validate")]
    [Description(
        "플랜 JSON 을 4단(스키마·어휘·정합성·드라이런)으로 검증한다. "
        + "떨어지면 어느 단의 어느 코드인지와 고치는 방법을 낸다. "
        + "**강제 디코딩을 믿지 않는다** — 모델이 낸 플랜은 반드시 이것을 지난다.")]
    public string Validate(
        [Description("플랜 JSON. 봉투(schema·bucket·plan)째로 줘도 된다")] string plan,
        [Description("버킷 키. 예: blacksmith@Dawn.Peace.Fair")] string bucket) =>
        WithTempFile(plan, bucket, file =>
            CliShell.Run(_options, PlanCommand.Run, CliShell.Args("validate", file, "--bucket", bucket)));

    /// <summary>결정론 자동 수선 (C-05).</summary>
    /// <param name="plan">플랜 JSON.</param>
    /// <param name="bucket">버킷 키.</param>
    [McpServerTool(Name = "plan_repair")]
    [Description(
        "검증에 떨어진 플랜을 규칙으로 고친다(전제 MoveTo 삽입·루프 닫기·수량 낮추기). "
        + "**검증을 건너뛰지 않는다** — 고친 문서는 1단부터 다시 지나고, 통과해야 결과가 나온다. "
        + "plan_validate 가 V3.PRECONDITION_UNMET·LOOP_NOT_CLOSED·RESOURCE_IMBALANCE 를 냈을 때 부른다.")]
    public string Repair(
        [Description("플랜 JSON")] string plan,
        [Description("버킷 키")] string bucket) =>
        WithTempFile(plan, bucket, file =>
            CliShell.Run(_options, PlanCommand.Run, CliShell.Args("repair", file, "--bucket", bucket)));

    /// <summary>한국어 하루 일지 (F-03).</summary>
    /// <param name="plan">플랜 JSON.</param>
    /// <param name="bucket">버킷 키.</param>
    [McpServerTool(Name = "plan_narrate")]
    [Description(
        "플랜을 한국어 하루 일지로 푼다. 트레이스·수지·소요 시간이 같이 나온다. "
        + "**검수자에게 보여 줄 때 쓴다** — JSON 을 읽히는 것보다 이쪽이 오독이 적다.")]
    public string Narrate(
        [Description("플랜 JSON")] string plan,
        [Description("버킷 키")] string bucket) =>
        WithTempFile(plan, bucket, file =>
            CliShell.Run(_options, PlanCommand.Run, CliShell.Args("narrate", file, "--bucket", bucket)));

    /// <summary>플랜 스토어 상태.</summary>
    /// <param name="archetype">아키타입 필터.</param>
    /// <param name="state">상태 필터.</param>
    [McpServerTool(Name = "bucket_status")]
    [Description(
        "플랜 스토어의 버킷 상태를 센다 — 생성됨·핀·폴백·없음. "
        + "'어느 버킷을 더 구워야 하나' 에 답한다. 파일 시스템만 보므로 서버가 안 떠 있어도 된다.")]
    public string BucketStatus(
        [Description("아키타입 id 로 좁힌다")] string? archetype = null,
        [Description("missing | fallback | pinned | generated")] string? state = null) =>
        CliShell.Run(
            _options,
            BucketsCommand.Run,
            CliShell.Args(
                archetype is null ? null : "--archetype", archetype,
                state is null ? null : "--state", state))
            .ToString();

    /// <summary>
    /// 플랜 문자열을 임시 파일에 쓰고 명령을 돌린다. <b>끝나면 지운다</b> —
    /// 모델이 부르는 툴이라 호출 수가 많고, 남기면 임시 폴더가 플랜으로 찬다.
    /// </summary>
    private static string WithTempFile(string plan, string bucket, Func<string, CliResult> run)
    {
        if (string.IsNullOrWhiteSpace(plan))
        {
            return "[exit 2]\nplan 이 비어 있다.";
        }

        if (string.IsNullOrWhiteSpace(bucket))
        {
            return "[exit 2]\nbucket 이 비어 있다. 예: blacksmith@Dawn.Peace.Fair";
        }

        string file = Path.Combine(Path.GetTempPath(), $"npc-mcp-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(file, plan);
            return run(file).ToString();
        }
        finally
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }
}
