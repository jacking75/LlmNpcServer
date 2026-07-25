using System.Text.Json;
using System.Text.Json.Serialization;

namespace Npc.Core.Plan;

/// <summary>
/// 플랜 DSL 의 System.Text.Json 소스 생성 컨텍스트. docs/11 §9.
/// <b>런타임 리플렉션 0.</b> 프리베이크에서 2,880건, 런타임 재계획에서 매번 돌아가는 경로다.
///
/// <see cref="JsonUnmappedMemberHandling.Disallow"/> 로 스키마에 없는 필드를 거부한다 —
/// docs/03 §3 의 <c>V1.EXTRA_FIELD</c> 가 이걸로 잡힌다.
/// </summary>
[JsonSourceGenerationOptions(
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PlanDocument))]
[JsonSerializable(typeof(PlanStep))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class PlanJsonContext : JsonSerializerContext;
