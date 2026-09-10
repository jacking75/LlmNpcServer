using System.Text.Json.Serialization;

namespace Npc.MasterData;

/// <summary>
/// <c>world_flags.json</c> 의 모양 (E-02).
///
/// <b>로더가 쓰지 않는다.</b> 이 파일은 빌드 시점에 소스 생성기가 읽어 <c>WorldFlags</c> enum 을
/// 만들고, 런타임에는 다시 읽지 않는다 — 그래서 로더 DTO 가 없다.
///
/// <para>
/// <b>그러면 드리프트가 난다.</b> 파일에 필드가 늘었는데 여기가 그대로면 스키마가 거짓을 말한다.
/// <c>SchemaDtoTests</c> 가 실제 파일을 이 DTO 로 왕복시켜 <b>손실이 없는지</b> 확인한다 —
/// 필드가 늘면 그 테스트가 먼저 깨진다.
/// </para>
/// </summary>
/// <param name="Version">파일 형식 버전.</param>
/// <param name="ReservedBits">추가를 허용하는 bit 구간. 여기 밖의 번호는 쓰지 않는다.</param>
/// <param name="Flags">플래그 정의.</param>
internal sealed record WorldFlagsFile(
    int Version,
    [property: JsonPropertyName("reserved_bits")] int[] ReservedBits,
    FlagDto[] Flags);

/// <summary>플래그 하나.</summary>
/// <param name="Bit">0..63. <b>재배치하지 않는다</b> — 프리베이크된 플랜이 통째로 깨진다.</param>
/// <param name="Id">코드가 쓰는 이름. <c>WorldFlags</c> enum 의 멤버가 된다.</param>
/// <param name="Group">분류. 문서·대시보드가 묶는 데만 쓴다.</param>
/// <param name="Desc">사람이 읽는 설명.</param>
internal sealed record FlagDto(int Bit, string Id, string Group, string Desc);

/// <summary>
/// <c>fallback_plans.json</c> 의 모양 (E-02).
///
/// <b>로더는 <c>JsonNode</c> 로 읽는다</b> — 플랜 본문을 <c>PlanDocument</c> 스키마 검증에
/// 그대로 넘겨야 해서 강타입으로 받지 않는다. 스키마 발행에는 모양이 필요하므로 여기 둔다.
/// <c>SchemaDtoTests</c> 가 왕복으로 드리프트를 막는다.
/// </summary>
/// <param name="Version">파일 형식 버전.</param>
/// <param name="Plans">폴백 플랜. 아키타입마다 하나씩 있어야 한다 (V7).</param>
internal sealed record FallbackPlansFile(int Version, FallbackPlanDto[] Plans);

/// <summary>폴백 플랜 하나. 플랜 본문은 <c>PlanDocument</c> 와 같은 모양이다.</summary>
/// <param name="Id">플랜 id. 아키타입의 <c>fallback_plan</c> 이 이 값을 가리킨다.</param>
/// <param name="Archetype">어느 아키타입의 폴백인가.</param>
/// <param name="Goal">목표 식별자.</param>
/// <param name="Loop">마지막 스텝 뒤 첫 스텝으로 돌아가는가. 폴백은 <c>true</c> 여야 한다 (V7).</param>
/// <param name="OnStepFail">스텝 실패 시 정책.</param>
/// <param name="Steps">3~10개. 그 아키타입의 <c>allowed_actions</c> 만 쓴다 (V8).</param>
internal sealed record FallbackPlanDto(
    string Id,
    string Archetype,
    string Goal,
    bool Loop,
    [property: JsonPropertyName("on_step_fail")] string? OnStepFail,
    FallbackStepDto[] Steps);

/// <summary>폴백 플랜의 스텝 하나.</summary>
/// <param name="Action">액션 id. 카탈로그에 있어야 한다 (V4).</param>
/// <param name="Args">액션 파라미터. 이름·타입은 <c>actions.json</c> 의 <c>params</c> 가 정한다.</param>
/// <param name="TimeoutSeconds">타임아웃(초). <b>0 이면 안 된다</b> — 모든 스텝에 강제한다.</param>
internal sealed record FallbackStepDto(
    string Action,
    System.Text.Json.JsonElement? Args,
    [property: JsonPropertyName("timeout_s")] int TimeoutSeconds);
