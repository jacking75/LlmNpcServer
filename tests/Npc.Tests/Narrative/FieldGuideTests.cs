using System.Collections.Immutable;
using System.Text.Json;
using Npc.MasterData;
using Npc.Narrative;
using Npc.Studio.Services;

namespace Npc.Tests.Narrative;

/// <summary>
/// 사전 드리프트 (T02).
///
/// <b>여기가 잡는 것은 "새로 넣고 설명을 빼먹었다" 하나다.</b> 액션을 추가하고 표기를
/// 안 넣으면 Studio 가 영어 id 를 그대로 내고, 필드를 추가하고 설명을 안 넣으면 그 칸은
/// 초보자에게 뜻을 모르는 입력란이 된다 — 둘 다 조용히 일어난다.
/// </summary>
public sealed class FieldGuideTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    /// <summary>스키마에 있지만 사전에 넣지 않는 것 — 사람용 주석과 파일 머리말이다.</summary>
    private static readonly ImmutableHashSet<string> Ignored =
        ImmutableHashSet.Create(StringComparer.Ordinal, "_comment", "$schema", "version");

    [Fact]
    public void Lexicon_CoversEveryAction()
    {
        foreach (ActionDef action in s_data.Actions.Actions)
        {
            Assert.True(
                Lexicon.Actions.ContainsKey(action.Id),
                $"액션 '{action.Id}' 의 한국어 표기가 Lexicon.Actions 에 없다.");
        }
    }

    [Fact]
    public void Lexicon_CoversEveryArchetypeGroup()
    {
        foreach (ArchetypeDef def in s_data.Archetypes.Archetypes)
        {
            Assert.True(
                Lexicon.Group(def.Id) != Lexicon.ArchetypeGroup.Other,
                $"아키타입 '{def.Id}' 의 직군이 Lexicon.Groups 에 없다 — 인형 색과 목록 묶음이 '기타' 가 된다.");
        }
    }

    /// <summary>
    /// <c>docs/schema/*.base.schema.json</c> 을 재귀로 훑어 모든 속성이 사전에 있는지 본다.
    /// 배열 첨자는 경로에서 뺀다 — 화면은 <c>/duty_hours</c> 로 부르지
    /// <c>/archetypes/3/duty_hours</c> 로 부르지 않는다.
    /// </summary>
    [Fact]
    public void FieldGuide_CoversEverySchemaProperty()
    {
        var missing = new List<string>();

        foreach (string path in Directory.EnumerateFiles(
            TestPaths.At("docs", "schema"), "*.base.schema.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            string file = Path.GetFileName(path).Replace(".base.schema.json", ".json", StringComparison.Ordinal);

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));

            foreach (string property in Properties(document.RootElement, document.RootElement, string.Empty, top: true))
            {
                if (FieldGuide.Of(file, property) is null)
                {
                    missing.Add(file + "#" + property);
                }
            }
        }

        Assert.True(missing.Count == 0, "FieldGuide 에 없는 필드: " + string.Join(", ", missing));
    }

    [Fact]
    public void FieldGuide_HasNoEmptyText()
    {
        foreach (FieldHelp help in FieldGuide.Fields.Values)
        {
            Assert.False(string.IsNullOrWhiteSpace(help.Title), help.File + help.Path + " 의 제목이 비었다.");
            Assert.False(string.IsNullOrWhiteSpace(help.What), help.File + help.Path + " 의 설명이 비었다.");
            Assert.False(string.IsNullOrWhiteSpace(help.Why), help.File + help.Path + " 의 용도가 비었다.");
        }

        Assert.NotEmpty(FieldGuide.Glossary);
    }

    /// <summary>
    /// 편집 폼에 실리는 칸은 <b>"틀리면 무슨 일이 나는가" 가 반드시 있다</b> (H28).
    ///
    /// <para>
    /// 읽기 전용 값은 잘못 쓸 일이 없으니 <c>Caution</c> 이 비어도 된다. 그러나 사람이
    /// 고치는 칸에 그 줄이 없으면 ⓘ 가 "이게 뭔지" 만 말하고 <b>"바꿔도 되는지" 는 말하지 않는다</b> —
    /// 초보자가 정확히 알고 싶은 것이 그쪽이다.
    /// </para>
    /// </summary>
    [Fact]
    public void FieldGuide_EditableFieldsHaveCaution()
    {
        var missing = new List<string>();

        foreach ((string file, ImmutableArray<string> paths) in EditablePaths)
        {
            foreach (string path in paths)
            {
                FieldHelp? help = FieldGuide.Of(file, path);

                Assert.True(help is not null, $"{file}#{path} 이 사전에 없다.");

                if (string.IsNullOrWhiteSpace(help!.Value.Caution))
                {
                    missing.Add(file + "#" + path);
                }
            }
        }

        Assert.True(missing.Count == 0, "편집 칸인데 주의 문장이 없다: " + string.Join(", ", missing));
    }

    /// <summary>
    /// 편집 폼이 실제로 그리는 칸. <b>폼이 표를 노출하므로 손으로 적지 않는다</b> —
    /// 새 칸을 폼에 넣고 사전에 빼먹으면 여기서 깨진다.
    /// </summary>
    private static IEnumerable<(string File, ImmutableArray<string> Paths)> EditablePaths
    {
        get
        {
            yield return (
                "archetypes.json",
                [.. StudioArchetypeForm.JsonKeys.Values.Where(k => k != "id").Select(k => "/" + k)]);

            yield return (
                "fallback_plans.json",
                [.. StudioFallbackForm.JsonKeys.Values.Where(k => k is not ("id" or "archetype")).Select(k => "/" + k)]);

            yield return (
                "npc_overrides.json",
                ["/patrol_route", "/aggro_radius_m", "/faction", "/dialogue_profile", "/schedule_offset_min"]);

            yield return (
                "pois.json",
                ["/id", "/subtype", "/type", "/capacity", "/pos/x", "/pos/z", "/open_hours/from",
                 "/open_hours/to", "/allowed_archetypes", "/resources"]);
        }
    }

    /// <summary>사전이 실제 데이터를 읽는지 — 로컬라이즈 표가 있으면 그 문구가 먼저다.</summary>
    [Fact]
    public void Lexicon_PrefersLocalizationTable()
    {
        ArchetypeDef def = s_data.Archetypes.Archetypes[0];

        Assert.False(string.IsNullOrWhiteSpace(Lexicon.ArchetypeName(s_data, def.Id)));
        Assert.False(string.IsNullOrWhiteSpace(Lexicon.Zone(s_data, s_data.Zones.Zones[0].Id)));

        PoiDef home = s_data.Pois.Pois.First(p => p.Type == PoiType.Home);

        Assert.StartsWith(Lexicon.Place(home.Subtype), Lexicon.PlaceName(home), StringComparison.Ordinal);
        Assert.Contains("#", Lexicon.PlaceName(home), StringComparison.Ordinal);
    }

    /// <summary>
    /// 스키마 한 장의 속성 경로. 항목이 객체인 배열은 한 단계 벗겨서 항목의 속성을 그대로 올린다.
    ///
    /// <para>
    /// <b>구멍이 둘 있었다</b> (H28) — <c>$ref</c> 를 따라가지 않아 <c>items.json</c> 의
    /// <c>recipes/outputs</c> 가 통째로 빠졌고, <b>원시값 배열</b>(<c>world_flags.json</c> 의
    /// <c>reserved_bits</c>)은 벗기면 아무것도 안 남아 검사에서 사라졌다.
    /// </para>
    /// </summary>
    private static IEnumerable<string> Properties(
        JsonElement root, JsonElement node, string path, bool top = false)
    {
        node = Resolve(root, node);

        if (node.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        if (node.TryGetProperty("properties", out JsonElement properties))
        {
            foreach (JsonProperty property in properties.EnumerateObject())
            {
                if (Ignored.Contains(property.Name))
                {
                    continue;
                }

                JsonElement value = Resolve(root, property.Value);

                // 최상위의 <b>객체</b> 배열(archetypes · pois …)만 벗긴다. 화면이 항목 하나를 본다.
                // 원시값 배열은 그 자체가 칸이므로 경로를 그대로 낸다.
                bool unwrap = top
                    && value.TryGetProperty("type", out JsonElement type)
                    && type.ValueKind == JsonValueKind.String
                    && string.Equals(type.GetString(), "array", StringComparison.Ordinal)
                    && HasProperties(root, Items(root, value));

                if (unwrap)
                {
                    foreach (string nested in Properties(root, Items(root, value), path))
                    {
                        yield return nested;
                    }

                    continue;
                }

                string child = path + "/" + property.Name;
                yield return child;

                foreach (string nested in Properties(root, Items(root, value), child))
                {
                    yield return nested;
                }
            }
        }
    }

    private static bool HasProperties(JsonElement root, JsonElement node) =>
        Resolve(root, node) is { ValueKind: JsonValueKind.Object } resolved
        && resolved.TryGetProperty("properties", out _);

    /// <summary>배열이면 항목 스키마로 내려간다. 아니면 자기 자신이다.</summary>
    private static JsonElement Items(JsonElement root, JsonElement node)
    {
        JsonElement resolved = Resolve(root, node);

        return resolved.ValueKind == JsonValueKind.Object && resolved.TryGetProperty("items", out JsonElement items)
            ? Resolve(root, items)
            : resolved;
    }

    /// <summary>
    /// <c>$ref</c> 를 문서 안에서 푼다 (H28).
    /// <b>따라가지 않으면 그 가지 전체가 검사에서 조용히 빠진다.</b>
    /// </summary>
    private static JsonElement Resolve(JsonElement root, JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object
            || !node.TryGetProperty("$ref", out JsonElement reference)
            || reference.ValueKind != JsonValueKind.String
            || reference.GetString() is not { } pointer
            || !pointer.StartsWith("#/", StringComparison.Ordinal))
        {
            return node;
        }

        JsonElement at = root;

        foreach (string part in pointer[2..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (at.ValueKind != JsonValueKind.Object || !at.TryGetProperty(part, out JsonElement next))
            {
                return node;
            }

            at = next;
        }

        return at;
    }
}
