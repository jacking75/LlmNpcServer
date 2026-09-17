using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace Npc.MasterData.Authoring;

/// <summary>번호 고정 위반 하나 (H12).</summary>
/// <param name="File">파일 이름.</param>
/// <param name="Path">JSON Pointer.</param>
/// <param name="Detail">사람이 읽는 한 줄.</param>
public readonly record struct CodeViolation(string File, string Path, string Detail);

/// <summary>
/// <c>code</c>·<c>bit</c> 재배치를 거절한다 (H12).
///
/// <para>
/// <b>CLAUDE.md §2.4 가 "절대 재배치하지 않는다" 라고 쓰고 Studio 사이드바도 그렇게 약속하지만,
/// 강제하는 코드가 없었다.</b> 원문 편집기에서 두 직업의 <c>code</c> 를 맞바꾸면 V1(중복)도
/// 로더도 통과한다 — 번호는 둘 다 유일하기 때문이다. 그런데 프리베이크 2,880건의 파일 이름과
/// NPC 명단의 직업, 거리표의 첨자가 전부 그 번호 위에 있다. 조용히 어긋난다.
/// </para>
///
/// <para>
/// <b>추가는 허용한다.</b> 새 id 가 새 번호를 받는 것은 정상이다. 막는 것은
/// <b>이미 있던 id 의 번호가 바뀌는 것</b>과 <b>id 가 사라지는 것</b> 둘이다.
/// </para>
/// </summary>
public static class CodePinning
{
    /// <summary>파일 → (배열 속성, 번호 속성). 번호 체계를 가진 파일 전부다.</summary>
    public static ImmutableArray<(string File, string Array, string Number)> Pinned { get; } =
    [
        ("archetypes.json", "archetypes", "code"),
        ("pois.json", "pois", "code"),
        ("zones.json", "zones", "code"),
        ("actions.json", "actions", "code"),
        ("items.json", "items", "code"),
        ("factions.json", "factions", "code"),
        ("dialogue_lines.json", "lines", "code"),
        ("world_flags.json", "flags", "bit"),
    ];

    /// <summary>이 파일이 번호 체계를 가지는가. 화면이 확인 문장을 고를 때 쓴다.</summary>
    /// <param name="fileName">파일 이름.</param>
    public static bool IsNumbered(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        foreach ((string file, string _, string _) in Pinned)
        {
            if (string.Equals(file, fileName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 저장 전후를 견준다. 위반이 없으면 빈 배열이다.
    /// </summary>
    /// <param name="fileName">파일 이름. 번호 체계가 없는 파일이면 빈 배열이다.</param>
    /// <param name="before">디스크의 원문.</param>
    /// <param name="after">저장 후보.</param>
    public static ImmutableArray<CodeViolation> Check(string fileName, string before, string after)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        string arrayProperty = string.Empty;
        string numberProperty = string.Empty;

        foreach ((string file, string array, string number) in Pinned)
        {
            if (string.Equals(file, fileName, StringComparison.Ordinal))
            {
                arrayProperty = array;
                numberProperty = number;
                break;
            }
        }

        if (arrayProperty.Length == 0 || before is null || after is null)
        {
            return [];
        }

        Dictionary<string, int>? old = Numbers(before, arrayProperty, numberProperty);
        Dictionary<string, int>? now = Numbers(after, arrayProperty, numberProperty);

        // 읽지 못하면 판정하지 않는다 — 모양 오류는 V1 이 잡는다.
        if (old is null || now is null)
        {
            return [];
        }

        var violations = ImmutableArray.CreateBuilder<CodeViolation>();

        foreach ((string id, int code) in old.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!now.TryGetValue(id, out int changed))
            {
                violations.Add(new CodeViolation(
                    fileName,
                    "/" + arrayProperty,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"'{id}' ({numberProperty} {code}) 가 사라졌다 — 번호가 가리키던 곳이 빈다. 삭제는 파급을 본 뒤 `npc` CLI 로 한다.")));
            }
            else if (changed != code)
            {
                violations.Add(new CodeViolation(
                    fileName,
                    "/" + arrayProperty,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"'{id}' 의 {numberProperty} 가 {code} → {changed} 로 바뀌었다 — 번호는 재배치할 수 없다. 추가는 맨 뒤에만 한다.")));
            }
        }

        return violations.ToImmutable();
    }

    /// <summary>id → 번호. 읽지 못하면 null.</summary>
    private static Dictionary<string, int>? Numbers(string json, string array, string number)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(array, out JsonElement items)
                || items.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var map = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (JsonElement item in items.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("id", out JsonElement id)
                    && id.ValueKind == JsonValueKind.String
                    && item.TryGetProperty(number, out JsonElement code)
                    && code.TryGetInt32(out int value))
                {
                    map[id.GetString()!] = value;
                }
            }

            return map;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
