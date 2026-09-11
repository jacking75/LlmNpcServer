using System.ComponentModel;
using ModelContextProtocol.Server;
using Npc.Cli;

namespace Npc.Mcp.Tools;

/// <summary>
/// 마스터데이터 읽기 툴 (E-03).
///
/// <para>
/// <b>전부 <c>npc</c> CLI 와 같은 함수를 부른다.</b> 두 벌로 쓰면 반드시 어긋나고,
/// 어긋난 쪽을 보는 것은 사람이 아니라 모델이다.
/// </para>
/// </summary>
[McpServerToolType]
public sealed class MasterDataTools
{
    private readonly McpOptions _options;

    /// <summary>DI 가 만든다.</summary>
    /// <param name="options">서버 설정.</param>
    public MasterDataTools(McpOptions options) => _options = options;

    /// <summary>V0~V13 검증. <b>무엇을 고치라</b> 까지 같이 낸다 (E-04).</summary>
    /// <param name="json">기계가 읽는 출력인가. 기본 true — 부르는 쪽이 모델이다.</param>
    [McpServerTool(Name = "masterdata_validate")]
    [Description(
        "마스터데이터 V0~V13 을 검증한다. 위반마다 code·파일·JSON 경로·fix_hint 를 낸다. "
        + "마스터데이터를 고친 뒤에는 반드시 이것부터 부른다.")]
    public string Validate(
        [Description("기계가 읽는 JSON 출력. 기본 true")] bool json = true) =>
        CliShell.Run(_options, ValidateCommand.Run, json: json).ToString();

    /// <summary>정의 하나를 사람이 읽는 카드로 (F-03).</summary>
    /// <param name="kind">종류.</param>
    /// <param name="id">id.</param>
    [McpServerTool(Name = "masterdata_explain")]
    [Description(
        "정의 하나를 한국어 카드로 설명한다. kind 는 archetype·action·poi·item·flag·interrupt 중 하나다. "
        + "파일을 직접 읽는 것보다 이쪽이 정확하다 — 파생 정보(어느 아키타입이 쓰는가 등)가 같이 나온다.")]
    public string Explain(
        [Description("archetype | action | poi | item | flag | interrupt")] string kind,
        [Description("정의 id. 예: blacksmith · MoveTo · bread")] string id) =>
        CliShell.Run(_options, ExplainCommand.Run, CliShell.Args(kind, id)).ToString();

    /// <summary>다음 <c>code</c>·<c>bit</c> (F-04).</summary>
    /// <param name="file">파일 이름 또는 별칭.</param>
    [McpServerTool(Name = "masterdata_next_code")]
    [Description(
        "그 파일의 다음 code(또는 bit)를 알려 준다. 예약 구간을 먼저 채운다. "
        + "**눈으로 세지 않는다** — 최대+1 로 두면 비워 둔 예약 구간을 영영 못 쓴다.")]
    public string NextCode(
        [Description("items | pois | actions | archetypes | zones | flags")] string file) =>
        CliShell.Run(_options, NextCodeCommand.Run, CliShell.Args(file)).ToString();

    /// <summary>새 정의 초안 (F-04). <b>파일을 고치지 않는다.</b></summary>
    /// <param name="kind">종류. 지금은 archetype 만.</param>
    /// <param name="id">새 id.</param>
    /// <param name="from">본뜰 기존 id.</param>
    /// <param name="weight">인구 가중치.</param>
    [McpServerTool(Name = "masterdata_scaffold")]
    [Description(
        "새 정의의 초안 JSON 과 파급표(무엇을 더 고쳐야 하는가)를 낸다. "
        + "**파일을 고치지 않는다** — 적용은 masterdata_apply 가 한다.")]
    public string Scaffold(
        [Description("지금은 archetype 만")] string kind,
        [Description("새 id. 예: beekeeper")] string id,
        [Description("본뜰 기존 id. 예: farmer")] string? from = null,
        [Description("인구 가중치 0~1")] double weight = 0) =>
        CliShell.Run(
            _options,
            ScaffoldCommand.Run,
            CliShell.Args(
                kind, id,
                from is null ? null : "--from", from,
                weight <= 0 ? null : "--weight",
                weight <= 0 ? null : weight.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)))
            .ToString();

    /// <summary>변경 집합의 사람 말 요약과 무효화 범위 (F-01).</summary>
    /// <param name="baseRevision">비교 기준 리비전.</param>
    [McpServerTool(Name = "masterdata_diff")]
    [Description(
        "지금 작업본과 기준 리비전의 마스터데이터 차이를 사람 말로 내고, "
        + "그 변경이 무엇을 무효화하는지(프리베이크·거리표·구조 해시) 같이 알려 준다.")]
    public string Diff(
        [Description("git 리비전. 기본 HEAD")] string? baseRevision = null) =>
        CliShell.Run(
            _options,
            DiffCommand.Run,
            CliShell.Args(baseRevision is null ? null : "--base", baseRevision))
            .ToString();

    /// <summary>파생물 신선도 (F-04).</summary>
    [McpServerTool(Name = "masterdata_regen_check")]
    [Description(
        "생성물(poi_distances.bin · npc_instances.json)이 낡았는지 본다. "
        + "낡은 채로 돌면 서버는 조용히 옛 거리표를 쓴다.")]
    public string RegenCheck() =>
        CliShell.Run(_options, RegenCommand.Run, CliShell.Args("--check")).ToString();
}
