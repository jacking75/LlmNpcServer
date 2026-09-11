using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Npc.Llm;

/// <summary>변경 이력 한 줄.</summary>
/// <param name="Version">그때의 <c>prompt_version</c>.</param>
/// <param name="Date">올린 날짜 (YYYY-MM-DD).</param>
/// <param name="Note">무엇이 왜 바뀌었나.</param>
public readonly record struct PromptChange(string Version, string Date, string Note);

/// <summary>
/// 프롬프트 프리픽스의 <b>사람이 읽는 이름표</b> (C-03).
///
/// <para>
/// <b>프리픽스의 신원은 SHA-256 이다.</b> 이 파일은 그 SHA 에 붙는 라벨이지 입력이 아니다 —
/// <c>prompt_manifest.json</c> 을 고쳐도 프리픽스 SHA 는 바뀌지 않는다. 라벨을 해시에 넣으면
/// "설명을 고쳤더니 캐시가 전부 미적중" 이 되고, 그러면 아무도 설명을 안 고친다.
/// </para>
///
/// <para>
/// <b>왜 필요한가.</b> <c>system_rules.md</c> 한 줄을 고치면 2,880 버킷이 전부 미적중되는데,
/// 되돌릴 좌표가 SHA 문자열 하나뿐이었다. 버전별 통과율을 비교하거나 "어느 회차로 되돌릴까" 를
/// 말하려면 <b>사람이 부를 수 있는 이름</b>이 있어야 한다.
/// </para>
/// </summary>
public sealed class PromptManifest
{
    /// <summary>파일 이름. <c>masterdata/prompt/</c> 아래에 있다.</summary>
    public const string FileName = "prompt_manifest.json";

    /// <summary>파일이 없을 때 쓰는 값. <b>비어 있음을 감추지 않는다.</b></summary>
    public const string UnknownVersion = "unversioned";

    private PromptManifest(string version, ImmutableArray<PromptChange> changelog)
    {
        PromptVersion = version;
        Changelog = changelog;
    }

    /// <summary>사람이 올리는 버전 이름. 예: <c>2026.09-r1</c>.</summary>
    public string PromptVersion { get; }

    /// <summary>변경 이력. 최신이 앞이다.</summary>
    public ImmutableArray<PromptChange> Changelog { get; }

    /// <summary>파일이 없어 기본값을 쓰고 있는가.</summary>
    public bool IsMissing => string.Equals(PromptVersion, UnknownVersion, StringComparison.Ordinal);

    /// <summary>
    /// 읽는다. <b>파일이 없으면 기동을 막지 않는다</b> — 라벨이 없다고 서버가 못 뜰 이유는 없고,
    /// 대신 <see cref="UnknownVersion"/> 이 메트릭·manifest 에 그대로 실려 보인다.
    /// </summary>
    /// <param name="promptDirectory"><c>masterdata/prompt/</c> 경로.</param>
    public static PromptManifest Load(string promptDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(promptDirectory);

        string path = Path.Combine(promptDirectory, FileName);

        if (!File.Exists(path))
        {
            return new PromptManifest(UnknownVersion, []);
        }

        PromptManifestDto? dto = JsonSerializer.Deserialize(
            File.ReadAllText(path), PromptManifestJsonContext.Default.PromptManifestDto);

        if (dto is null || string.IsNullOrWhiteSpace(dto.PromptVersion))
        {
            throw new InvalidDataException(
                $"{FileName}: prompt_version 이 비어 있다. 라벨이 없으면 되돌릴 좌표가 SHA 뿐이다.");
        }

        ImmutableArray<PromptChange> changelog = dto.Changelog is null
            ? []
            : [.. dto.Changelog.Select(c => new PromptChange(
                c.Version ?? string.Empty, c.Date ?? string.Empty, c.Note ?? string.Empty))];

        return new PromptManifest(dto.PromptVersion, changelog);
    }

    /// <summary>
    /// 프리픽스 SHA 의 짧은 형태 (C-03). <b>플랜 스토어 디렉터리 이름이 이것이다.</b>
    ///
    /// <para>
    /// 8자면 충돌 확률이 사람이 만드는 프롬프트 회차 수에서는 무시할 수 있고, 폴더 이름으로
    /// 읽을 수 있다. 충돌하면 두 회차가 같은 폴더를 쓰게 되므로 manifest 의 <c>prefix_hash</c>
    /// 전문이 그것을 잡는다 — <c>PlanStoreValidator</c> 가 전문으로 비교한다.
    /// </para>
    /// </summary>
    /// <param name="sha256">프리픽스 SHA-256 (소문자 hex 64자).</param>
    public static string ShortSha(string sha256)
    {
        ArgumentException.ThrowIfNullOrEmpty(sha256);

        return sha256.Length <= 8 ? sha256 : sha256[..8];
    }

    // --- JSON DTO. 소스 생성기로 직렬화한다. ---

    internal sealed record PromptManifestDto(
        int Version,
        [property: JsonPropertyName("prompt_version")] string? PromptVersion,
        PromptChangeDto[]? Changelog);

    internal sealed record PromptChangeDto(string? Version, string? Date, string? Note);
}

/// <summary>소스 생성 직렬화 컨텍스트.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(PromptManifest.PromptManifestDto))]
internal sealed partial class PromptManifestJsonContext : JsonSerializerContext;
