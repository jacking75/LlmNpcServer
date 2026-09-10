using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Npc.MasterData.Authoring;

/// <summary>파생물 한 건의 신선도 기록.</summary>
/// <param name="Artifact">생성물 파일 이름.</param>
/// <param name="Generator">만든 도구 (<c>gen_npcs.cs</c>).</param>
/// <param name="Inputs">입력 파일 이름 → 그때의 SHA-256.</param>
public sealed record DerivedEntry(
    string Artifact, string Generator, ImmutableSortedDictionary<string, string> Inputs);

/// <summary>신선도 판정 하나.</summary>
/// <param name="Artifact">생성물 파일 이름.</param>
/// <param name="Stale">낡았는가.</param>
/// <param name="Reason">낡았다면 왜인지. 신선하면 빈 문자열.</param>
/// <param name="Generator">다시 만들 도구.</param>
public readonly record struct DerivedStatus(string Artifact, bool Stale, string Reason, string Generator)
{
    /// <summary>사람이 읽는 한 줄.</summary>
    public override string ToString() =>
        Stale ? $"{Artifact} 낡음 — {Reason} (`{Generator}` 재실행)" : $"{Artifact} 최신";
}

/// <summary>
/// 파생물 신선도 잠금 (F-04).
///
/// <b>`poi_distances.bin` 과 `npc_instances.json` 은 생성물이다.</b> 입력이 바뀌었는데
/// 다시 만들지 않으면 서버는 <b>낡은 거리표로 조용히 돈다</b> — 검증도 통과하고 기동도 되지만
/// NPC 가 존재하지 않는 POI 로 걸어간다. 그 오류는 재현하기도 어렵다.
///
/// <para>
/// <c>masterdata/derived.lock.json</c> 에 "어떤 입력 해시로 만들었는가" 를 기록한다.
/// 생성기가 쓰고, 로더가 읽어 <b>경고</b>한다 — 기동 실패가 아니라 경고인 이유는,
/// 낡은 파생물로도 개발 중에는 돌려 볼 수 있어야 하기 때문이다. 판정은 사람이 한다.
/// </para>
/// </summary>
public static class DerivedArtifacts
{
    /// <summary>잠금 파일 이름.</summary>
    public const string FileName = "derived.lock.json";

    /// <summary>
    /// 파생물과 그 입력. <b>여기가 사실의 출처다</b> — 생성기와 로더가 같은 표를 본다.
    /// 표에 없는 파일은 파생물이 아니다.
    /// </summary>
    public static ImmutableArray<DerivedEntry> Known { get; } =
    [
        new(
            "poi_distances.bin",
            "tools/gen_poi_distances.cs",
            ImmutableSortedDictionary<string, string>.Empty
                .Add("pois.json", string.Empty)
                .Add("zones.json", string.Empty)),
        new(
            "npc_instances.json",
            "tools/gen_npcs.cs",
            ImmutableSortedDictionary<string, string>.Empty
                .Add("archetypes.json", string.Empty)
                .Add("pois.json", string.Empty)
                .Add("zones.json", string.Empty)),
    ];

    /// <summary>이 파일이 파생물인가.</summary>
    public static bool IsDerived(string fileName) =>
        Known.Any(k => string.Equals(k.Artifact, fileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 잠금 파일을 현재 입력 해시로 다시 쓴다. <b>생성기가 자기 파생물을 만든 직후 부른다.</b>
    /// </summary>
    /// <param name="masterDataDirectory"><c>masterdata/</c> 경로.</param>
    /// <param name="artifact">방금 만든 파생물 파일 이름.</param>
    public static void Record(string masterDataDirectory, string artifact)
    {
        ArgumentException.ThrowIfNullOrEmpty(masterDataDirectory);
        ArgumentException.ThrowIfNullOrEmpty(artifact);

        DerivedEntry known = Known.FirstOrDefault(
            k => string.Equals(k.Artifact, artifact, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"'{artifact}' 는 알려진 파생물이 아니다.", nameof(artifact));

        Dictionary<string, LockEntry> all = ReadRaw(masterDataDirectory);

        var inputs = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (string input in known.Inputs.Keys)
        {
            inputs[input] = HashOf(Path.Combine(masterDataDirectory, input));
        }

        all[artifact] = new LockEntry(known.Generator, inputs);

        Write(masterDataDirectory, all);
    }

    /// <summary>
    /// 파생물이 낡았는지 본다. <b>잠금 기록이 없으면 낡은 것으로 본다</b> —
    /// 언제 무엇으로 만들었는지 모르는 파일을 최신이라고 부를 근거가 없다.
    /// </summary>
    /// <param name="masterDataDirectory"><c>masterdata/</c> 경로.</param>
    public static ImmutableArray<DerivedStatus> Check(string masterDataDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(masterDataDirectory);

        Dictionary<string, LockEntry> all = ReadRaw(masterDataDirectory);
        var results = ImmutableArray.CreateBuilder<DerivedStatus>(Known.Length);

        foreach (DerivedEntry known in Known)
        {
            string artifactPath = Path.Combine(masterDataDirectory, known.Artifact);

            if (!File.Exists(artifactPath))
            {
                results.Add(new DerivedStatus(known.Artifact, true, "파일이 없다", known.Generator));
                continue;
            }

            if (!all.TryGetValue(known.Artifact, out LockEntry? entry))
            {
                results.Add(new DerivedStatus(
                    known.Artifact, true, $"{FileName} 에 기록이 없다", known.Generator));
                continue;
            }

            string? changed = FirstChanged(masterDataDirectory, known, entry);

            results.Add(changed is null
                ? new DerivedStatus(known.Artifact, false, string.Empty, known.Generator)
                : new DerivedStatus(known.Artifact, true, $"{changed} 이 바뀌었다", known.Generator));
        }

        return results.ToImmutable();
    }

    /// <summary>낡은 것만. 로더의 경고와 <c>regen</c> 이 같은 목록을 본다.</summary>
    public static ImmutableArray<DerivedStatus> Stale(string masterDataDirectory) =>
        [.. Check(masterDataDirectory).Where(s => s.Stale)];

    /// <summary>파일 하나의 SHA-256 (소문자 hex). <c>MasterDataSet.FileHashes</c> 와 같은 형식이다.</summary>
    public static string HashOf(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!File.Exists(path))
        {
            return string.Empty;
        }

        using FileStream stream = File.OpenRead(path);

        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string? FirstChanged(
        string masterDataDirectory, DerivedEntry known, LockEntry entry)
    {
        // 입력 목록 자체가 바뀌었으면 (표에 파일이 추가됐으면) 기록을 믿을 수 없다.
        foreach (string input in known.Inputs.Keys)
        {
            if (!entry.Inputs.TryGetValue(input, out string? recorded))
            {
                return input;
            }

            if (!string.Equals(recorded, HashOf(Path.Combine(masterDataDirectory, input)), StringComparison.Ordinal))
            {
                return input;
            }
        }

        return null;
    }

    private static Dictionary<string, LockEntry> ReadRaw(string masterDataDirectory)
    {
        string path = Path.Combine(masterDataDirectory, FileName);

        if (!File.Exists(path))
        {
            return new Dictionary<string, LockEntry>(StringComparer.Ordinal);
        }

        LockFile? file = JsonSerializer.Deserialize(File.ReadAllText(path), LockJsonContext.Default.LockFile);

        return file?.Artifacts is null
            ? new Dictionary<string, LockEntry>(StringComparer.Ordinal)
            : new Dictionary<string, LockEntry>(file.Artifacts, StringComparer.Ordinal);
    }

    /// <summary>
    /// 쓰기 전용 옵션. <b>한글을 <c>\uXXXX</c> 로 escape 하지 않는다</b> —
    /// 사람이 diff 로 읽는 파일이라 읽히지 않으면 기록의 뜻이 없다.
    /// </summary>
    private static readonly JsonSerializerOptions s_write =
        new(LockJsonContext.Default.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static void Write(string masterDataDirectory, Dictionary<string, LockEntry> artifacts)
    {
        var sorted = new SortedDictionary<string, LockEntry>(artifacts, StringComparer.Ordinal);

        string json = JsonSerializer
            .Serialize(new LockFile(1, Comment, sorted), s_write)
            .ReplaceLineEndings();

        File.WriteAllText(
            Path.Combine(masterDataDirectory, FileName), json + Environment.NewLine, new UTF8Encoding(false));
    }

    private static readonly string[] Comment =
    [
        "생성물이 어떤 입력으로 만들어졌는지의 기록이다 (F-04). 손으로 고치지 않는다.",
        "생성기가 쓰고 MasterDataLoader 가 읽어 낡음을 경고한다.",
        "시각을 적지 않는다 — 같은 입력이면 같은 파일이어야 diff 로 검토할 수 있다.",
    ];

    internal sealed record LockEntry(
        [property: JsonPropertyName("generator")] string Generator,
        [property: JsonPropertyName("inputs")] SortedDictionary<string, string> Inputs);

    internal sealed record LockFile(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("_comment")] string[] Comment,
        [property: JsonPropertyName("artifacts")] SortedDictionary<string, LockEntry> Artifacts);
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(DerivedArtifacts.LockFile))]
internal sealed partial class LockJsonContext : JsonSerializerContext;
