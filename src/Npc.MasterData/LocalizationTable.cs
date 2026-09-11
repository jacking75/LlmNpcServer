using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace Npc.MasterData;

/// <summary>
/// 로컬라이즈 표 (D-02).
///
/// <para>
/// <b>문구는 마스터데이터에 없었다.</b> <c>archetypes.json</c> 은 <c>name_key</c>(<c>npc.blacksmith</c>)
/// 만 갖고 실제 문구는 없어, 게임서버·툴·<c>Npc.Narrate</c> 가 각자 하드코딩했다 — 세 곳이
/// 다르게 읽으면 블라인드 평가 자료의 두 군이 다른 문장으로 보인다.
/// </para>
///
/// <para>
/// <b>표시 계층이다.</b> 행동을 정하는 값(code·bit)은 여전히 <c>masterdata/</c> 의 다른 파일이
/// 소유한다 (CLAUDE.md §2.4). 여기 있는 것은 "그 id 를 어떻게 읽어 줄까" 뿐이다 —
/// 그래서 누락은 <b>기동 실패가 아니라 경고</b>다.
/// </para>
///
/// <para>
/// <b>키 접두는 무엇이 이름을 소유하는지 말한다.</b> <c>npc.</c>(아키타입) · <c>poi.</c>(subtype) ·
/// <c>item.</c> · <c>zone.</c> · <c>dialogue.</c>. 액션·플래그는 아직 없다 — 표시 계층이
/// 그것들을 id 그대로 쓰고 있고, 쓰지 않는 문구를 미리 번역해 두면 그 번역이 먼저 낡는다.
/// </para>
/// </summary>
public sealed class LocalizationTable
{
    /// <summary>파일이 있는 폴더 이름.</summary>
    public const string FolderName = "localization";

    /// <summary>기본 로케일. 이 저장소의 서술이 한국어다.</summary>
    public const string DefaultLocale = "ko-KR";

    private readonly ImmutableSortedDictionary<string, string> _text;

    private LocalizationTable(string locale, ImmutableSortedDictionary<string, string> text)
    {
        Locale = locale;
        _text = text;
    }

    /// <summary>로케일 이름. 파일 이름에서 온다.</summary>
    public string Locale { get; }

    /// <summary>키 → 문구. 키 오름차순이다.</summary>
    public ImmutableSortedDictionary<string, string> Text => _text;

    /// <summary>키 수.</summary>
    public int Count => _text.Count;

    /// <summary>
    /// 문구. 없으면 <b>키를 그대로</b> 돌려준다 — 빈 문자열을 주면 화면에서 사라져
    /// 누락을 알아챌 계기가 없다.
    /// </summary>
    /// <param name="key">키.</param>
    public string this[string key] => _text.GetValueOrDefault(key, key);

    /// <summary>이 키가 있는가. 완전성 검사(V14)가 쓴다.</summary>
    /// <param name="key">키.</param>
    public bool Contains(string key) => _text.ContainsKey(key);

    /// <summary>파일 하나를 읽는다.</summary>
    /// <param name="path">파일 경로. 파일 이름(확장자 제외)이 로케일이다.</param>
    public static LocalizationTable Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        // <b><c>JsonDocument</c> 로 직접 읽는다.</b> 이유가 둘이다 —
        // (1) 키가 열려 있어 DTO 를 쓸 수 없고, 리플렉션 역직렬화는 이 어셈블리에서 금지다
        //     (<c>Runtime_UsesNoRuntimeJsonReflection</c>).
        // (2) 다른 마스터데이터 파일처럼 설명을 남길 자리(<c>_comment</c>)가 필요한데
        //     그 값은 배열이라, 값 타입을 고정하면 그 줄 하나가 파일 전체를 못 읽게 만든다.
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"로컬라이즈 파일이 객체가 아니다: {path}");
        }

        var text = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);

        foreach (JsonProperty entry in document.RootElement.EnumerateObject())
        {
            if (!entry.Name.StartsWith('_') && entry.Value.ValueKind == JsonValueKind.String)
            {
                text[entry.Name] = entry.Value.GetString()!;
            }
        }

        return new LocalizationTable(Path.GetFileNameWithoutExtension(path), text.ToImmutable());
    }

    /// <summary>폴더의 모든 로케일. 파일이 없으면 빈 배열이다 — 오류가 아니다.</summary>
    /// <param name="masterDataDirectory"><c>masterdata/</c> 경로.</param>
    public static ImmutableArray<LocalizationTable> LoadAll(string masterDataDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(masterDataDirectory);

        string directory = Path.Combine(masterDataDirectory, FolderName);

        if (!Directory.Exists(directory))
        {
            return [];
        }

        // 파일 순서를 고정한다. 순회 순서에 기대면 경고 메시지가 회차마다 달라진다.
        string[] files = [.. Directory.GetFiles(directory, "*.json")];

        Array.Sort(files, StringComparer.Ordinal);

        return [.. files.Select(Load)];
    }

    /// <summary>
    /// 이 마스터데이터가 요구하는 키 전부 (V14).
    ///
    /// <b>키 목록은 마스터데이터가 정한다.</b> 손으로 적으면 아키타입을 추가할 때마다
    /// 두 곳을 고쳐야 하고, 한 곳만 고쳐지는 날이 온다.
    /// </summary>
    /// <param name="data">마스터데이터.</param>
    public static ImmutableArray<string> RequiredKeys(MasterDataSet data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var keys = new SortedSet<string>(StringComparer.Ordinal);

        foreach (ArchetypeDef archetype in data.Archetypes.Archetypes)
        {
            keys.Add($"npc.{archetype.Id}");
        }

        foreach (PoiDef poi in data.Pois.Pois)
        {
            keys.Add($"poi.{poi.Subtype}");
        }

        foreach (ItemDef item in data.Items.Items)
        {
            keys.Add($"item.{item.Id}");
        }

        foreach (ZoneDef zone in data.Zones.Zones)
        {
            keys.Add($"zone.{zone.Id}");
        }

        foreach (DialogueLine line in data.Dialogues?.Lines ?? [])
        {
            keys.Add($"dialogue.{line.Id}");
        }

        return [.. keys];
    }

    /// <summary>
    /// V14 — 이 로케일에 빠진 키. <b>누락은 경고다</b>(표시 계층이므로) — 다만 CI 게이트는
    /// 이 목록이 비어 있기를 요구한다.
    /// </summary>
    /// <param name="data">마스터데이터.</param>
    public ImmutableArray<string> Missing(MasterDataSet data) =>
        [.. RequiredKeys(data).Where(key => !Contains(key))];

    /// <summary>
    /// 마스터데이터에 없는 키. <b>낡은 문구다</b> — 아이템을 지웠는데 번역이 남은 것이고,
    /// 방치하면 번역 파일이 무엇을 덮는지 아무도 모르게 된다.
    /// </summary>
    /// <param name="data">마스터데이터.</param>
    public ImmutableArray<string> Extra(MasterDataSet data)
    {
        var required = RequiredKeys(data).ToHashSet(StringComparer.Ordinal);

        return [.. _text.Keys.Where(key => !required.Contains(key))];
    }

    /// <summary>사람이 읽는 한 줄. 기동 로그가 낸다.</summary>
    /// <param name="data">마스터데이터.</param>
    public string Describe(MasterDataSet data)
    {
        int missing = Missing(data).Length;
        int extra = Extra(data).Length;

        string missingText = missing == 0
            ? string.Empty
            : string.Create(CultureInfo.InvariantCulture, $" · 누락 {missing}");

        string extraText = extra == 0
            ? string.Empty
            : string.Create(CultureInfo.InvariantCulture, $" · 낡은 키 {extra}");

        return string.Create(CultureInfo.InvariantCulture, $"{Locale}: 문구 {Count}개")
            + missingText
            + extraText;
    }
}
