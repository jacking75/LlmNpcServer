using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npc.MasterData;

namespace Npc.Planning;

/// <summary>어떤 엔진으로 만들었나. docs/03 §7 의 <c>generated_by</c>.</summary>
/// <param name="Tier">T1(로컬) | T2(외부).</param>
/// <param name="Model">엔진 id. <c>appsettings.Llm.json</c> 의 것 그대로.</param>
/// <param name="Temperature">시도 1의 temperature.</param>
/// <param name="Concurrency">
/// AIMD 초기 동시성. <b>이 값의 근거가 manifest 에 남아야 한다</b> (T3-12) —
/// "동시 32" 는 상위 계획의 추정이고 W1 은 외부 동시성을 재지 않았다.
/// </param>
/// <param name="PeakConcurrency">회차 중 도달한 최대 동시성.</param>
/// <param name="FirstRateLimitConcurrency">429 가 처음 난 동시성. 한 번도 안 났으면 0.</param>
public sealed record ManifestGeneratedBy(
    string Tier,
    string Model,
    double Temperature,
    int Concurrency = 0,
    int PeakConcurrency = 0,
    int FirstRateLimitConcurrency = 0);

/// <summary>버킷 집계. docs/03 §7 의 <c>counts</c>.</summary>
/// <param name="Total">전체 버킷 수. 2,880.</param>
/// <param name="Generated">LLM 생성 + 검증 통과.</param>
/// <param name="Pinned">사람이 고정한 것.</param>
/// <param name="Fallback">폴백으로 해소한 것.</param>
/// <param name="Reused">인접 버킷에서 빌려 온 것 (docs/12 §7).</param>
public readonly record struct ManifestCounts(
    int Total,
    int Generated,
    int Pinned,
    int Fallback,
    int Reused = 0)
{
    /// <summary>생성 완료율. P3 게이트는 ≥ 0.95 다.</summary>
    public double GeneratedRate => Total == 0 ? 0 : (double)(Generated + Pinned) / Total;
}

/// <summary>검증 단계별 실패 집계. docs/03 §7 의 <c>validation</c>.</summary>
/// <param name="Pass">4단까지 통과.</param>
/// <param name="FailSchema">1단 실패.</param>
/// <param name="FailVocab">2단 실패.</param>
/// <param name="FailCoherence">3단 실패.</param>
/// <param name="FailDryRun">4단 실패.</param>
/// <param name="FailCall">서버에 닿지도 못한 것 (429 · 타임아웃).</param>
public readonly record struct ManifestValidation(
    int Pass,
    int FailSchema,
    int FailVocab,
    int FailCoherence,
    int FailDryRun,
    int FailCall = 0)
{
    /// <summary>판정한 건수.</summary>
    public int Total => Pass + FailSchema + FailVocab + FailCoherence + FailDryRun + FailCall;

    /// <summary>통과율.</summary>
    public double PassRate => Total == 0 ? 0 : (double)Pass / Total;
}

/// <summary>
/// 플랜 스토어 manifest. docs/03 §7.
///
/// <b>이 파일이 곧 R&amp;D 보고서의 원자료다.</b> 프리베이크를 돌 때마다 보존한다
/// (CLAUDE.md §6 — <c>planstore/manifest.json</c> 은 반드시 커밋한다).
///
/// <b>생성 시각은 외부에서 인자로 주입한다.</b> <c>DateTime.Now</c> 를 여기서 부르면
/// 같은 입력이 같은 파일을 내지 못해 결정론이 깨진다 (CLAUDE.md §2.3 · docs/13 §8).
/// 그래서 <see cref="GeneratedAt"/> 은 문자열이고 이 타입은 시계를 모른다.
/// </summary>
public sealed record Manifest
{
    /// <summary>지금 쓰는 스키마 버전.</summary>
    public const int CurrentSchema = 1;

    /// <summary>파일 이름.</summary>
    public const string FileName = "manifest.json";

    /// <summary>스키마 버전.</summary>
    public int Schema { get; init; } = CurrentSchema;

    /// <summary>마스터데이터 전체 콘텐츠 해시. <see cref="MasterDataSet.ContentHash"/> 를 그대로 쓴다.</summary>
    public required string MasterdataHash { get; init; }

    /// <summary>프롬프트 프리픽스 SHA-256. 바뀌면 플랜 스토어가 전량 무효다.</summary>
    public required string PrefixHash { get; init; }

    /// <summary>어떤 엔진으로 만들었나.</summary>
    public required ManifestGeneratedBy GeneratedBy { get; init; }

    /// <summary>버킷 집계.</summary>
    public ManifestCounts Counts { get; init; }

    /// <summary>검증 단계별 집계.</summary>
    public ManifestValidation Validation { get; init; }

    /// <summary>실비용(USD). P3 게이트는 ≤ $5 다.</summary>
    public double CostUsd { get; init; }

    /// <summary>전체 소요(초). P3 게이트는 ≤ 300 이다.</summary>
    public double WallClockSeconds { get; init; }

    /// <summary>프롬프트 캐시 적중률(입력 토큰 기준). P3 게이트는 ≥ 0.95 다.</summary>
    public double CacheHitRate { get; init; }

    /// <summary>
    /// 생성 시각. <b>외부에서 주입한 문자열이다</b> — 이 타입은 시계를 부르지 않는다.
    /// 비워 두면 결정론적 산출물이 된다 (같은 입력 → 같은 파일).
    /// </summary>
    public string GeneratedAt { get; init; } = string.Empty;

    /// <summary>
    /// 예산 캡에 걸려 중단됐는가. <c>--resume</c> 이 이 값을 보고 이어서 돈다 (T3-14).
    /// </summary>
    public bool Partial { get; init; }

    /// <summary>
    /// 파일별 해시. 파일명 오름차순. <see cref="PlanStoreValidator"/> 의 부분 무효화 판정이 쓴다 —
    /// 이게 없으면 POI 하나 추가할 때마다 전량 재생성이다 (docs/13 §3).
    /// </summary>
    public ImmutableArray<FileHash> FileHashes { get; init; } = [];

    /// <summary>이 파일 하나의 해시. 없으면 null.</summary>
    public string? HashOf(string fileName)
    {
        foreach (FileHash hash in FileHashes)
        {
            if (string.Equals(hash.FileName, fileName, StringComparison.Ordinal))
            {
                return hash.Sha256;
            }
        }

        return null;
    }

    /// <summary>
    /// 마스터데이터에서 뼈대를 만든다. 집계·비용·소요는 호출부가 <c>with</c> 로 채운다.
    /// </summary>
    /// <param name="data">마스터데이터. 해시를 여기서 가져온다.</param>
    /// <param name="prefixHash">프롬프트 프리픽스 SHA-256.</param>
    /// <param name="generatedBy">엔진 정보.</param>
    /// <param name="generatedAt">생성 시각 문자열. <b>외부 주입</b>. null 이면 빈 문자열.</param>
    public static Manifest For(
        MasterDataSet data,
        string prefixHash,
        ManifestGeneratedBy generatedBy,
        string? generatedAt = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrEmpty(prefixHash);
        ArgumentNullException.ThrowIfNull(generatedBy);

        return new Manifest
        {
            MasterdataHash = data.ContentHash,
            PrefixHash = prefixHash,
            GeneratedBy = generatedBy,
            GeneratedAt = generatedAt ?? string.Empty,
            FileHashes = data.FileHashes,
            Counts = new ManifestCounts(Total: Npc.Core.BucketKey.TotalKeys, 0, 0, 0),
        };
    }

    /// <summary>JSON 으로. 사람이 읽는 파일이라 들여쓴다.</summary>
    public string ToJson() => JsonSerializer.Serialize(ToDto(this), ManifestJsonContext.Default.ManifestDto);

    /// <summary>파일로 쓴다. 폴더가 없으면 만든다.</summary>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        // BOM 없이 쓴다. Encoding.UTF8 은 BOM 을 붙이고, 그러면 jq 같은 외부 도구가 첫 글자에서 걸린다.
        File.WriteAllText(path, ToJson(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>파일에서 읽는다. 없으면 null — 첫 프리베이크에는 manifest 가 없다.</summary>
    public static Manifest? Load(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        ManifestDto? dto = JsonSerializer.Deserialize(
            File.ReadAllText(path), ManifestJsonContext.Default.ManifestDto);

        return dto is null ? null : FromDto(dto);
    }

    /// <summary><c>planstore/</c> 폴더에서 읽는다.</summary>
    public static Manifest? LoadFrom(string planStoreDirectory) =>
        Load(Path.Combine(planStoreDirectory, FileName));

    private static ManifestDto ToDto(Manifest m) => new(
        m.Schema,
        m.MasterdataHash,
        m.PrefixHash,
        m.GeneratedAt,
        m.Partial,
        new GeneratedByDto(
            m.GeneratedBy.Tier,
            m.GeneratedBy.Model,
            m.GeneratedBy.Temperature,
            m.GeneratedBy.Concurrency,
            m.GeneratedBy.PeakConcurrency,
            m.GeneratedBy.FirstRateLimitConcurrency),
        new CountsDto(
            m.Counts.Total, m.Counts.Generated, m.Counts.Pinned, m.Counts.Fallback, m.Counts.Reused),
        new ValidationDto(
            m.Validation.Pass,
            m.Validation.FailSchema,
            m.Validation.FailVocab,
            m.Validation.FailCoherence,
            m.Validation.FailDryRun,
            m.Validation.FailCall),
        Math.Round(m.CostUsd, 6),
        Math.Round(m.WallClockSeconds, 1),
        Math.Round(m.CacheHitRate, 4),
        [.. m.FileHashes.Select(h => new FileHashDto(h.FileName, h.Sha256))]);

    private static Manifest FromDto(ManifestDto dto) => new()
    {
        Schema = dto.Schema,
        MasterdataHash = dto.MasterdataHash ?? string.Empty,
        PrefixHash = dto.PrefixHash ?? string.Empty,
        GeneratedAt = dto.GeneratedAt ?? string.Empty,
        Partial = dto.Partial,
        GeneratedBy = new ManifestGeneratedBy(
            dto.GeneratedBy?.Tier ?? string.Empty,
            dto.GeneratedBy?.Model ?? string.Empty,
            dto.GeneratedBy?.Temperature ?? 0,
            dto.GeneratedBy?.Concurrency ?? 0,
            dto.GeneratedBy?.PeakConcurrency ?? 0,
            dto.GeneratedBy?.FirstRateLimitConcurrency ?? 0),
        Counts = dto.Counts is { } c
            ? new ManifestCounts(c.Total, c.Generated, c.Pinned, c.Fallback, c.Reused)
            : default,
        Validation = dto.Validation is { } v
            ? new ManifestValidation(v.Pass, v.FailSchema, v.FailVocab, v.FailCoherence, v.FailDryrun, v.FailCall)
            : default,
        CostUsd = dto.CostUsd,
        WallClockSeconds = dto.WallClockS,
        CacheHitRate = dto.CacheHitRate,
        FileHashes = dto.FileHashes is null
            ? []
            : [.. dto.FileHashes.Select(h => new FileHash(h.File, h.Sha256))],
    };

    // --- JSON DTO. 소스 생성기로 직렬화한다. 필드 이름은 docs/03 §7 그대로다. ---

    internal sealed record ManifestDto(
        int Schema,
        string? MasterdataHash,
        string? PrefixHash,
        string? GeneratedAt,
        bool Partial,
        GeneratedByDto? GeneratedBy,
        CountsDto? Counts,
        ValidationDto? Validation,
        double CostUsd,
        double WallClockS,
        double CacheHitRate,
        FileHashDto[]? FileHashes);

    internal sealed record GeneratedByDto(
        string Tier,
        string Model,
        double Temperature,
        int Concurrency,
        int PeakConcurrency,
        int FirstRateLimitConcurrency);

    internal sealed record CountsDto(int Total, int Generated, int Pinned, int Fallback, int Reused);

    internal sealed record ValidationDto(
        int Pass, int FailSchema, int FailVocab, int FailCoherence, int FailDryrun, int FailCall);

    internal sealed record FileHashDto(string File, string Sha256);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true)]
[JsonSerializable(typeof(Manifest.ManifestDto))]
internal sealed partial class ManifestJsonContext : JsonSerializerContext;
