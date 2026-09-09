using System.Collections;
using System.Collections.Immutable;
using System.Text.Json;

namespace Npc.Host.Config;

/// <summary>옵션 하나의 모양. 값을 받는가, 받지 않는 스위치인가.</summary>
/// <param name="Name">긴 이름. 앞의 두 하이픈을 포함한다.</param>
/// <param name="TakesValue">값을 받는가.</param>
public readonly record struct OptionSpec(string Name, bool TakesValue);

/// <summary>
/// 설정 소스 셋을 하나로 합친다 (A-04).
///
/// 우선순위 <b>CLI &gt; 환경변수 &gt; 설정 파일 &gt; 기본값</b>.
///
/// <b>파서를 하나로 유지한다.</b> 소스마다 파서를 두면 "환경변수로는 되는데 인자로는 안 되는"
/// 종류의 어긋남이 생긴다. 대신 환경변수와 파일을 <b>합성 argv</b> 로 바꿔 실제 인자 앞에 붙인다 —
/// <see cref="HostOptions.TryParse(string[], out HostOptions, out string?)"/> 는 뒤에 온 값이
/// 앞의 값을 덮으므로 그것만으로 우선순위가 성립한다.
///
/// 환경변수 이름 규칙: <c>--gs-host</c> 는 <c>NPC_GS_HOST</c>. 설정 파일 키는 <c>gs-host</c>.
/// </summary>
public static class HostOptionsSource
{
    /// <summary>환경변수 접두.</summary>
    public const string EnvPrefix = "NPC_";

    /// <summary>기본 설정 파일 이름.</summary>
    public const string DefaultFileName = "npc.settings.json";

    /// <summary>설정 파일 안에서는 쓸 수 없는 키. 파일이 자기 경로를 정하는 순환을 막는다.</summary>
    public const string FileForbiddenKey = "config";

    /// <summary>
    /// 인자로 줄 수 있는 옵션 전부. <see cref="HostOptions.Usage"/> 와 어긋나면 테스트가 깨진다.
    ///
    /// 도움말 스위치는 없다 — 환경변수나 파일로 도움말을 켜는 것은 의미가 없다.
    /// <c>--config</c> 는 있다(컨테이너가 <c>NPC_CONFIG</c> 로 경로를 준다). 다만 설정 파일
    /// 자신이 그 키를 쓰는 것은 <see cref="FileForbiddenKey"/> 로 막는다.
    /// </summary>
    public static ImmutableArray<OptionSpec> Options { get; } =
    [
        new("--loopback", false),
        new("--link", true),
        new("--trace", true),
        new("--gs-host", true),
        new("--gs-port", true),
        new("--zone", true),
        new("--dev-control", false),
        new("--npcs", true),
        new("--time-scale", true),
        new("--days", true),
        new("--tier", true),
        new("--no-llm", false),
        new("--t1-workers", true),
        new("--t2-workers", true),
        new("--t1-engine", true),
        new("--t2-engine", true),
        new("--scenario", true),
        new("--fail-rate", true),
        new("--drop-rate", true),
        new("--player-bots", true),
        new("--masterdata", true),
        new("--planstore", true),
        new("--seed", true),
        new("--port", true),
        new("--bind", true),
        new("--config", true),
        new("--health-port", true),
        new("--profile", true),
        new("--snapshot-dir", true),
        new("--snapshot-interval-s", true),
        new("--snapshot-keep", true),
        new("--restore", true),
        new("--shutdown-timeout-s", true),
        new("--weights", true),
        new("--scan-cap", true),
        new("--max-speed", false),
        new("--no-dashboard", false),
        new("--on-link-fault", true),
        new("--fault-grace-s", true),
        new("--live-stall-s", true),
        new("--ready-tick-stall-s", true),
    ];

    /// <summary>옵션 이름에서 환경변수 이름을 만든다.</summary>
    public static string EnvNameOf(string option)
    {
        ArgumentNullException.ThrowIfNull(option);

        return EnvPrefix + option.TrimStart('-').Replace('-', '_').ToUpperInvariant();
    }

    /// <summary>옵션 이름에서 설정 파일 키를 만든다. 앞의 하이픈만 뗀다.</summary>
    public static string FileKeyOf(string option)
    {
        ArgumentNullException.ThrowIfNull(option);

        return option.TrimStart('-');
    }

    /// <summary>
    /// 환경변수를 합성 argv 로. 접두가 다른 것은 무시한다.
    ///
    /// 스위치는 <c>1</c>·<c>true</c>·<c>yes</c>·<c>on</c> 이면 켜고 그 밖은 끈다 —
    /// <c>NPC_NO_DASHBOARD=false</c> 로 "명시적으로 안 켠다" 를 표현할 수 있어야 한다.
    /// </summary>
    public static string[] FromEnv(IReadOnlyDictionary<string, string?> env)
    {
        ArgumentNullException.ThrowIfNull(env);

        var argv = new List<string>();

        foreach (OptionSpec spec in Options)
        {
            if (!env.TryGetValue(EnvNameOf(spec.Name), out string? value) || value is null)
            {
                continue;
            }

            Append(argv, spec, value);
        }

        return [.. argv];
    }

    /// <summary>
    /// 설정 파일을 합성 argv 로. 파일이 없으면 빈 배열이고 성공이다.
    ///
    /// 형식은 평면 JSON 객체다. 모르는 키는 <b>거절한다</b> —
    /// 오타 난 키를 조용히 무시하면 "설정했는데 안 먹는다" 가 된다.
    /// </summary>
    /// <param name="path">파일 경로.</param>
    /// <param name="profile">고를 프로파일 절 이름. null 이면 최상위만 읽는다.</param>
    /// <param name="argv">합성 argv. 프로파일 절이 최상위보다 뒤에 온다(더 세다).</param>
    /// <param name="error">실패 사유.</param>
    public static bool TryFromFile(string path, string? profile, out string[] argv, out string? error)
    {
        ArgumentNullException.ThrowIfNull(path);

        argv = [];
        error = null;

        if (!File.Exists(path))
        {
            return true;
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(
                File.ReadAllText(path),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                });
        }
        catch (JsonException e)
        {
            error = $"설정 파일을 읽지 못했다 ({path}): {e.Message}";
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = $"설정 파일의 최상위는 객체여야 한다: {path}";
                return false;
            }

            var list = new List<string>();

            if (!TryReadSection(document.RootElement, path, list, out error))
            {
                return false;
            }

            if (profile is not null
                && document.RootElement.TryGetProperty("profiles", out JsonElement profiles)
                && profiles.ValueKind == JsonValueKind.Object
                && profiles.TryGetProperty(profile, out JsonElement section))
            {
                if (section.ValueKind != JsonValueKind.Object)
                {
                    error = $"설정 파일의 profiles.{profile} 은 객체여야 한다: {path}";
                    return false;
                }

                if (!TryReadSection(section, path, list, out error))
                {
                    return false;
                }
            }

            argv = [.. list];
        }

        return true;
    }

    /// <summary>
    /// 설정 파일을 찾는다. 명시 경로 다음 실행 파일 폴더, 그다음 작업 폴더 순.
    ///
    /// <b>어느 것을 읽었는지 돌려준다</b> — <c>appsettings.Llm.json</c> 은 작업 폴더 기준,
    /// <c>appsettings.json</c> 은 실행 파일 기준이라 로드 경로가 갈렸고 그것이 진단을 어렵게 했다.
    /// </summary>
    public static string? Locate(string? explicitPath, string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        if (explicitPath is not null)
        {
            return File.Exists(explicitPath) ? Path.GetFullPath(explicitPath) : null;
        }

        foreach (string dir in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            string candidate = Path.Combine(dir, fileName);

            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    /// <summary>현재 프로세스 환경변수를 사전으로. 테스트가 가짜를 넣을 수 있게 분리했다.</summary>
    public static IReadOnlyDictionary<string, string?> CurrentEnvironment()
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && key.StartsWith(EnvPrefix, StringComparison.OrdinalIgnoreCase))
            {
                map[key] = entry.Value as string;
            }
        }

        return map;
    }

    private static bool TryReadSection(
        JsonElement section, string path, List<string> argv, out string? error)
    {
        error = null;

        var byKey = Options.ToDictionary(spec => FileKeyOf(spec.Name), spec => spec, StringComparer.OrdinalIgnoreCase);

        foreach (JsonProperty property in section.EnumerateObject())
        {
            // 프로파일 절은 --profile 이 고른다. 최상위 순회에서는 건너뛴다.
            if (property.NameEquals("profiles"))
            {
                continue;
            }

            if (property.NameEquals(FileForbiddenKey))
            {
                error =
                    $"설정 파일이 자기 경로를 정할 수 없다 ({path}): '{FileForbiddenKey}' 키는 "
                    + "명령줄이나 NPC_CONFIG 로만 준다.";

                return false;
            }

            if (!byKey.TryGetValue(property.Name, out OptionSpec spec))
            {
                error =
                    $"설정 파일에 모르는 키가 있다 ({path}): '{property.Name}'. "
                    + "옵션 이름에서 앞의 하이픈을 뺀 것이 키다.";

                return false;
            }

            if (!TryScalar(property.Value, out string? text))
            {
                error = $"설정 파일 '{property.Name}' 의 값이 스칼라가 아니다: {path}";
                return false;
            }

            Append(argv, spec, text!);
        }

        return true;
    }

    private static void Append(List<string> argv, OptionSpec spec, string value)
    {
        if (spec.TakesValue)
        {
            argv.Add(spec.Name);
            argv.Add(value);
            return;
        }

        if (IsTruthy(value))
        {
            argv.Add(spec.Name);
        }
    }

    private static bool IsTruthy(string value) =>
        value.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";

    private static bool TryScalar(JsonElement element, out string? text)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                text = element.GetString();
                return text is not null;

            case JsonValueKind.Number:
                text = element.GetRawText();
                return true;

            case JsonValueKind.True:
                text = "true";
                return true;

            case JsonValueKind.False:
                text = "false";
                return true;

            default:
                text = null;
                return false;
        }
    }
}
