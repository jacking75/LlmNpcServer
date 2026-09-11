using System.Collections.Immutable;
using Npc.Contracts;
using Npc.MasterData;

namespace Npc.Tests.MasterData;

/// <summary>
/// D-02 — 대사 주제 표와 로컬라이즈 표.
///
/// <para>
/// <b>여기서 지키는 것은 "번호가 안 밀린다" 하나다.</b> 예전에는 심볼을 사전순으로 모아
/// 그 첨자를 <c>DialogueId</c> 로 썼고, 주제를 하나 추가하면 뒤쪽이 통째로 밀려
/// 프리베이크된 플랜 2,880개와 게임서버의 대사 표가 <b>조용히</b> 어긋났다.
/// </para>
/// </summary>
public sealed class DialogueTableTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    /// <summary>
    /// <b>기존 <c>DialogueId</c> 가 그대로다</b> (프리베이크 호환 회귀).
    ///
    /// 이 값들은 <c>SortedSet</c> 시절의 순서를 굳힌 것이고, 바뀌면 이미 만든 플랜의
    /// <c>Speak</c> 가 다른 주제를 가리킨다.
    /// </summary>
    [Fact]
    public void Codes_MatchTheFrozenOrder()
    {
        DialogueTable table = s_data.Dialogues!;

        Assert.Equal(
            ["call_for_help", "danger", "greeting", "politics", "rumor", "trade", "warning", "work"],
            [.. table.Lines.Select(l => l.Id)]);

        for (int i = 0; i < table.Lines.Length; i++)
        {
            Assert.Equal(i, table.Lines[i].Code.Value);
        }

        // 액션 카탈로그가 이 표를 쓴다 — 첨자가 곧 code 다.
        Assert.Equal([.. table.Names], [.. s_data.Actions.Dialogues]);
    }

    /// <summary>
    /// <b>주제를 추가해도 기존 번호가 안 바뀐다</b> (D-02 완료 조건).
    /// 사전순이면 <c>farewell</c> 이 <c>danger</c> 와 <c>greeting</c> 사이에 끼어 뒤가 전부 밀린다.
    /// </summary>
    [Fact]
    public void AddingATopic_DoesNotShiftExistingCodes()
    {
        DialogueTable before = s_data.Dialogues!;

        DialogueTable after = DialogueTable.Parse(
            """
            { "version": 1, "lines": [
              { "id": "call_for_help", "code": 0 },
              { "id": "danger", "code": 1 },
              { "id": "greeting", "code": 2 },
              { "id": "politics", "code": 3 },
              { "id": "rumor", "code": 4 },
              { "id": "trade", "code": 5 },
              { "id": "warning", "code": 6 },
              { "id": "work", "code": 7 },
              { "id": "farewell", "code": 8 } ] }
            """);

        foreach (DialogueLine line in before.Lines)
        {
            Assert.True(after.TryGet(line.Id, out DialogueId code), line.Id);
            Assert.Equal(line.Code, code);
        }

        Assert.True(after.TryGet("farewell", out DialogueId added));
        Assert.Equal(8, added.Value);
    }

    /// <summary><b>code 중복·id 중복은 로드 실패다.</b> 경고로 두면 한쪽이 조용히 이긴다.</summary>
    [Fact]
    public void Load_RejectsDuplicates()
    {
        InvalidDataException dupCode = Assert.Throws<InvalidDataException>(() => DialogueTable.Parse(
            """{ "version": 1, "lines": [ { "id": "a", "code": 1 }, { "id": "b", "code": 1 } ] }"""));

        Assert.Contains("중복", dupCode.Message, StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => DialogueTable.Parse(
            """{ "version": 1, "lines": [ { "id": "a", "code": 1 }, { "id": "a", "code": 2 } ] }"""));

        Assert.Throws<InvalidDataException>(() => DialogueTable.Parse(
            """{ "version": 1, "lines": [] }"""));
    }

    /// <summary>
    /// <b>V14 — <c>actions.json</c> 의 심볼이 표에 없으면 기동 실패다.</b>
    ///
    /// 경고로 두면 그 심볼의 <c>DialogueId</c> 가 0 이 되는데, 0 은 다른 주제의 번호라
    /// NPC 가 엉뚱한 말을 한다 — 그 사고는 아무 로그도 안 남긴다.
    /// </summary>
    [Fact]
    public void V14_FailsWhenASymbolIsMissing()
    {
        // greeting 을 뺀 표. actions.json 의 Talk.topic 이 그것을 쓴다.
        DialogueTable partial = DialogueTable.Parse(
            """{ "version": 1, "lines": [ { "id": "call_for_help", "code": 0 } ] }""");

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => ActionCatalog.Load(
                Path.Combine(TestPaths.MasterData, "actions.json"), s_data.Items, partial));

        Assert.Contains("V14", error.Message, StringComparison.Ordinal);
        Assert.Contains("greeting", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>대사 code 는 구조 해시에 들어간다</b> (B-04). 두 프로세스가 다른 번호를 쓰면
    /// NPC 가 엉뚱한 말을 하고, 그것은 "내용이 조금 다르다" 가 아니다.
    /// </summary>
    [Fact]
    public void Codes_AreStructural()
    {
        string withTable = StructuralHash.Compute(
            s_data.Actions, s_data.Items, s_data.Zones, s_data.Pois, s_data.Archetypes,
            s_data.Buckets, s_data.Dialogues);

        string without = StructuralHash.Compute(
            s_data.Actions, s_data.Items, s_data.Zones, s_data.Pois, s_data.Archetypes,
            s_data.Buckets);

        Assert.NotEqual(withTable, without);
        Assert.Equal(s_data.StructuralHash, withTable);
    }

    /// <summary>
    /// <b>출하 로케일마다 키가 빠짐없이 있어야 한다</b> (V14).
    /// 누락은 기동 실패가 아니지만 — 표시 계층이므로 — CI 게이트로는 실패다.
    /// </summary>
    [Fact]
    public void Locales_CoverEveryRequiredKey()
    {
        Assert.NotEmpty(s_data.Locales);

        ImmutableArray<string> required = LocalizationTable.RequiredKeys(s_data);

        Assert.NotEmpty(required);

        foreach (LocalizationTable locale in s_data.Locales)
        {
            Assert.True(
                locale.Missing(s_data).IsEmpty,
                $"{locale.Locale}: 빠진 키 {string.Join(", ", locale.Missing(s_data).Take(5))}");

            // 낡은 키도 막는다 — 아이템을 지웠는데 번역이 남으면 파일이 무엇을 덮는지 모르게 된다.
            Assert.True(
                locale.Extra(s_data).IsEmpty,
                $"{locale.Locale}: 낡은 키 {string.Join(", ", locale.Extra(s_data).Take(5))}");
        }

        // 기본 로케일은 반드시 있다.
        Assert.Contains(
            s_data.Locales,
            l => string.Equals(l.Locale, LocalizationTable.DefaultLocale, StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>한국어 표기는 <c>Lexicon</c> 과 같아야 한다.</b> 두 벌이면 반드시 어긋나고,
    /// 어긋나면 블라인드 평가 자료의 두 군이 다른 문장으로 보인다.
    /// </summary>
    [Fact]
    public void KoreanLocale_MatchesTheLexicon()
    {
        LocalizationTable ko = s_data.Locales.Single(
            l => string.Equals(l.Locale, LocalizationTable.DefaultLocale, StringComparison.Ordinal));

        foreach (ArchetypeDef archetype in s_data.Archetypes.Archetypes)
        {
            Assert.Equal(Npc.Narrative.Lexicon.Archetype(archetype.Id), ko[$"npc.{archetype.Id}"]);
        }

        foreach (ItemDef item in s_data.Items.Items)
        {
            Assert.Equal(Npc.Narrative.Lexicon.Item(item.Id), ko[$"item.{item.Id}"]);
        }

        foreach (PoiDef poi in s_data.Pois.Pois)
        {
            Assert.Equal(Npc.Narrative.Lexicon.Place(poi.Subtype), ko[$"poi.{poi.Subtype}"]);
        }
    }

    /// <summary>없는 키는 <b>키를 그대로</b> 돌려준다 — 빈 문자열이면 화면에서 사라진다.</summary>
    [Fact]
    public void Locale_FallsBackToTheKey()
    {
        LocalizationTable ko = s_data.Locales[0];

        Assert.Equal("item.nonexistent", ko["item.nonexistent"]);
        Assert.False(ko.Contains("item.nonexistent"));

        // 주석 키는 문구가 아니다.
        Assert.False(ko.Contains("_comment"));
    }
}
