using System.Collections.Immutable;
using System.Text.Json;
using Npc.MasterData;
using Npc.Narrative;

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

            foreach (string property in Properties(document.RootElement, string.Empty, root: true))
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

    /// <summary>스키마 한 장의 속성 경로. 배열은 한 단계 벗겨서 항목의 속성을 그대로 올린다.</summary>
    private static IEnumerable<string> Properties(JsonElement node, string path, bool root = false)
    {
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

                // 최상위의 배열 속성(archetypes · pois …)은 경로에 넣지 않는다. 화면이 항목 하나를 본다.
                bool unwrap = root
                    && property.Value.TryGetProperty("type", out JsonElement type)
                    && type.ValueKind == JsonValueKind.String
                    && string.Equals(type.GetString(), "array", StringComparison.Ordinal);

                if (unwrap)
                {
                    foreach (string nested in Properties(Items(property.Value), path))
                    {
                        yield return nested;
                    }

                    continue;
                }

                string child = path + "/" + property.Name;
                yield return child;

                foreach (string nested in Properties(Items(property.Value), child))
                {
                    yield return nested;
                }
            }
        }
    }

    /// <summary>배열이면 항목 스키마로 내려간다. 아니면 자기 자신이다.</summary>
    private static JsonElement Items(JsonElement node) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty("items", out JsonElement items)
            ? items
            : node;
}
