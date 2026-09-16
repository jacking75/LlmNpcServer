using System.Text;
using System.Text.Json;

namespace Npc.MasterData.Authoring;

/// <summary>
/// 서식 보존 JSON 편집 (F-04).
///
/// <b>파싱해서 다시 쓰지 않는다.</b> <c>JsonSerializer.Serialize</c> 로 되쓰면 들여쓰기·
/// 줄바꿈·키 순서·정렬된 주석 배열이 전부 무너지고, 그 diff 는 리뷰할 수 없다 —
/// <c>archetypes.json</c> 한 줄을 고쳤는데 3,000줄이 바뀐 diff 는 아무도 못 읽는다.
///
/// <para>
/// 대신 <see cref="Utf8JsonReader"/> 로 <b>토큰의 바이트 오프셋</b>을 찾아 그 범위만
/// 문자열로 치환한다. 건드리지 않은 바이트는 한 개도 변하지 않는다 —
/// <c>Edit_WithNoChangeIsByteIdentical</c> 이 그것을 강제한다.
/// </para>
///
/// <para>
/// <b>이것은 편집 도구이지 검증기가 아니다.</b> 결과가 유효한 마스터데이터인지는
/// <c>MasterDataValidator</c> 가 판정한다. 여기서는 JSON 으로 파싱되는지까지만 본다.
/// </para>
/// </summary>
public static class JsonSurgeon
{
    /// <summary>
    /// 최상위 배열에 항목 하나를 <b>맨 뒤에</b> 넣는다.
    ///
    /// 맨 뒤인 이유는 하나다 — <c>code</c>·<c>bit</c> 는 배열 순서가 아니라 필드 값이지만,
    /// 사람이 파일을 읽을 때 번호 순서와 줄 순서가 어긋나면 다음 번호를 잘못 짚는다.
    /// </summary>
    /// <param name="json">원문.</param>
    /// <param name="arrayProperty">최상위 배열 속성 이름 (<c>archetypes</c> 등).</param>
    /// <param name="itemJson">넣을 항목의 JSON. 들여쓰기는 이 함수가 맞춘다.</param>
    public static string AppendToArray(string json, string arrayProperty, string itemJson)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);
        ArgumentException.ThrowIfNullOrEmpty(arrayProperty);
        ArgumentException.ThrowIfNullOrEmpty(itemJson);

        (int start, int end) = ArrayRange(json, arrayProperty);

        // 마지막 항목의 끝 = 닫는 대괄호 앞의 마지막 비공백.
        int close = end - 1;
        int lastContent = close - 1;

        while (lastContent > start && char.IsWhiteSpace(json[lastContent]))
        {
            lastContent--;
        }

        string indent = ItemIndent(json, start, close);

        // 빈 배열이면 쉼표 없이 넣는다.
        bool empty = lastContent <= start;
        string inserted = empty
            ? Environment.NewLine + indent + Reindent(itemJson, indent) + Environment.NewLine
            : "," + Environment.NewLine + indent + Reindent(itemJson, indent);

        int at = empty ? start + 1 : lastContent + 1;

        return Verified(json[..at] + inserted + json[at..]);
    }

    /// <summary>
    /// 최상위 스칼라 필드의 값을 바꾼다. <c>total_keys</c> 처럼 계산으로 정해지는 값에 쓴다.
    /// </summary>
    /// <param name="json">원문.</param>
    /// <param name="property">최상위 속성 이름.</param>
    /// <param name="rawValue">새 값의 JSON 표현 (<c>2952</c>·<c>"x"</c>·<c>true</c>).</param>
    public static string SetTopLevel(string json, string property, string rawValue)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);
        ArgumentException.ThrowIfNullOrEmpty(property);
        ArgumentException.ThrowIfNullOrEmpty(rawValue);

        (int start, int end) = ValueRange(json, property);

        return Verified(json[..start] + rawValue + json[end..]);
    }

    /// <summary>이 객체에 그 최상위 속성이 있는가.</summary>
    /// <param name="json">객체 하나를 담은 JSON.</param>
    /// <param name="property">속성 이름.</param>
    public static bool HasTopLevel(string json, string property)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);
        ArgumentException.ThrowIfNullOrEmpty(property);

        try
        {
            _ = ValueRange(json, property);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// 최상위 속성을 바꾸거나, 없으면 넣는다 (T10).
    ///
    /// <b><see cref="SetTopLevel"/> 은 없는 속성에 던진다.</b> 폼 편집기는 원래 없던 필드를
    /// 넣어야 한다 — 대장장이에게는 <c>duty_hours</c> 가 없고, 근무 시간대를 주려면 새로 만들어야 한다.
    /// </summary>
    /// <param name="json">객체 하나를 담은 JSON.</param>
    /// <param name="property">속성 이름.</param>
    /// <param name="rawValue">새 값의 JSON 표현.</param>
    /// <param name="beforeProperty">
    /// 이 속성 앞에 넣는다. null 이거나 없으면 마지막 속성 뒤에 넣는다 —
    /// <b>키 순서는 로더에 영향이 없지만 사람이 읽는 순서는 스키마 순서다.</b>
    /// </param>
    public static string SetOrAddTopLevel(
        string json, string property, string rawValue, string? beforeProperty = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);
        ArgumentException.ThrowIfNullOrEmpty(property);
        ArgumentException.ThrowIfNullOrEmpty(rawValue);

        if (HasTopLevel(json, property))
        {
            return SetTopLevel(json, property, rawValue);
        }

        string entry = "\"" + property + "\": " + rawValue;

        if (beforeProperty is { Length: > 0 } anchor && HasTopLevel(json, anchor))
        {
            int at = LineStart(json, NameStart(json, anchor));
            string indent = json[at..NameStart(json, anchor)];

            return Verified(json[..at] + indent + entry + "," + Environment.NewLine + json[at..]);
        }

        // 마지막 비공백 = 닫는 중괄호. 그 앞의 마지막 내용 뒤에 쉼표로 잇는다.
        int close = json.LastIndexOf('}');
        int last = close - 1;

        while (last > 0 && char.IsWhiteSpace(json[last]))
        {
            last--;
        }

        string closeIndent = CloseIndent(json, close);

        return Verified(
            json[..(last + 1)] + "," + Environment.NewLine + closeIndent + "  " + entry + json[(last + 1)..]);
    }

    /// <summary>
    /// 최상위 속성을 지운다. 값이 비어 원래 없던 상태로 돌아갈 때 쓴다 —
    /// <b><c>null</c> 로 두는 것과 없는 것은 diff 에서 다르게 읽힌다.</b>
    /// </summary>
    /// <param name="json">객체 하나를 담은 JSON.</param>
    /// <param name="property">속성 이름.</param>
    public static string RemoveTopLevel(string json, string property)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);
        ArgumentException.ThrowIfNullOrEmpty(property);

        if (!HasTopLevel(json, property))
        {
            return json;
        }

        (int _, int valueEnd) = ValueRange(json, property);
        int start = LineStart(json, NameStart(json, property));
        int end = valueEnd;

        // 뒤따르는 쉼표와 줄바꿈까지 같이 지운다. 없으면 앞의 쉼표를 지운다.
        while (end < json.Length && (json[end] == ' ' || json[end] == '\t'))
        {
            end++;
        }

        if (end < json.Length && json[end] == ',')
        {
            end++;

            while (end < json.Length && (json[end] == '\r' || json[end] == '\n'))
            {
                end++;
            }

            return Verified(json[..start] + json[end..]);
        }

        int before = start - 1;

        while (before > 0 && char.IsWhiteSpace(json[before]))
        {
            before--;
        }

        if (before > 0 && json[before] == ',')
        {
            return Verified(json[..before] + json[end..]);
        }

        return Verified(json[..start] + json[end..]);
    }

    /// <summary>이 속성 이름 토큰이 시작하는 위치 (여는 따옴표).</summary>
    private static int NameStart(string json, string property)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(json);
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });

        int depth = 0;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName && depth == 1
                && reader.ValueTextEquals(property))
            {
                return ByteToChar(json, utf8, (int)reader.TokenStartIndex);
            }

            depth += reader.TokenType switch
            {
                JsonTokenType.StartObject or JsonTokenType.StartArray => 1,
                JsonTokenType.EndObject or JsonTokenType.EndArray => -1,
                _ => 0,
            };
        }

        throw new InvalidOperationException($"'{property}' 필드가 없다.");
    }

    /// <summary>이 위치가 속한 줄의 시작 (들여쓰기 앞).</summary>
    private static int LineStart(string json, int at)
    {
        int i = json.LastIndexOf('\n', Math.Max(0, at - 1));

        return i < 0 ? 0 : i + 1;
    }

    /// <summary>
    /// 배열 항목 하나의 스칼라 필드를 바꾼다. 가중치 재배분이 이것을 쓴다.
    /// </summary>
    /// <param name="json">원문.</param>
    /// <param name="arrayProperty">최상위 배열 속성 이름.</param>
    /// <param name="keyProperty">항목을 고르는 필드 (<c>id</c>).</param>
    /// <param name="keyValue">그 필드의 값.</param>
    /// <param name="property">바꿀 필드.</param>
    /// <param name="rawValue">새 값의 JSON 표현.</param>
    public static string SetInArrayItem(
        string json,
        string arrayProperty,
        string keyProperty,
        string keyValue,
        string property,
        string rawValue)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);
        ArgumentException.ThrowIfNullOrEmpty(rawValue);

        (int itemStart, int itemEnd) = ItemRange(json, arrayProperty, keyProperty, keyValue);
        (int start, int end) = ValueRange(json[itemStart..itemEnd], property);

        return Verified(json[..(itemStart + start)] + rawValue + json[(itemStart + end)..]);
    }

    /// <summary>
    /// 최상위 배열의 바이트 범위 (여는 <c>[</c> 부터 닫는 <c>]</c> 다음까지).
    /// </summary>
    /// <param name="json">원문.</param>
    /// <param name="arrayProperty">배열 속성 이름.</param>
    public static (int Start, int End) ArrayRange(string json, string arrayProperty)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);
        ArgumentException.ThrowIfNullOrEmpty(arrayProperty);

        byte[] utf8 = Encoding.UTF8.GetBytes(json);
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });

        int depth = 0;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName && depth == 1
                && reader.ValueTextEquals(arrayProperty))
            {
                reader.Read();

                if (reader.TokenType != JsonTokenType.StartArray)
                {
                    throw new InvalidOperationException($"'{arrayProperty}' 가 배열이 아니다.");
                }

                int start = (int)reader.TokenStartIndex;

                reader.Skip();

                return (ByteToChar(json, utf8, start), ByteToChar(json, utf8, (int)reader.BytesConsumed));
            }

            depth += reader.TokenType switch
            {
                JsonTokenType.StartObject or JsonTokenType.StartArray => 1,
                JsonTokenType.EndObject or JsonTokenType.EndArray => -1,
                _ => 0,
            };
        }

        throw new InvalidOperationException($"최상위에 '{arrayProperty}' 배열이 없다.");
    }

    /// <summary>이 객체의 <paramref name="property"/> 값이 차지하는 범위.</summary>
    /// <param name="json">객체 하나를 담은 JSON.</param>
    /// <param name="property">속성 이름.</param>
    public static (int Start, int End) ValueRange(string json, string property)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);
        ArgumentException.ThrowIfNullOrEmpty(property);

        byte[] utf8 = Encoding.UTF8.GetBytes(json);
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });

        int depth = 0;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName && depth == 1
                && reader.ValueTextEquals(property))
            {
                reader.Read();

                int start = (int)reader.TokenStartIndex;

                reader.Skip();

                return (ByteToChar(json, utf8, start), ByteToChar(json, utf8, (int)reader.BytesConsumed));
            }

            depth += reader.TokenType switch
            {
                JsonTokenType.StartObject or JsonTokenType.StartArray => 1,
                JsonTokenType.EndObject or JsonTokenType.EndArray => -1,
                _ => 0,
            };
        }

        throw new InvalidOperationException($"'{property}' 필드가 없다.");
    }

    /// <summary><paramref name="keyProperty"/> 가 <paramref name="keyValue"/> 인 배열 항목의 범위.</summary>
    /// <param name="json">원문.</param>
    /// <param name="arrayProperty">배열 속성 이름.</param>
    /// <param name="keyProperty">고르는 필드.</param>
    /// <param name="keyValue">그 값.</param>
    public static (int Start, int End) ItemRange(
        string json, string arrayProperty, string keyProperty, string keyValue)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyProperty);
        ArgumentException.ThrowIfNullOrEmpty(keyValue);

        (int arrayStart, int arrayEnd) = ArrayRange(json, arrayProperty);

        string slice = json[arrayStart..arrayEnd];
        byte[] utf8 = Encoding.UTF8.GetBytes(slice);
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });

        reader.Read();   // StartArray

        while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
        {
            int start = (int)reader.TokenStartIndex;

            reader.Skip();

            int end = (int)reader.BytesConsumed;

            using JsonDocument item = JsonDocument.Parse(utf8.AsMemory(start, end - start));

            if (item.RootElement.TryGetProperty(keyProperty, out JsonElement key)
                && key.ValueKind == JsonValueKind.String
                && string.Equals(key.GetString(), keyValue, StringComparison.Ordinal))
            {
                return (arrayStart + ByteToChar(slice, utf8, start), arrayStart + ByteToChar(slice, utf8, end));
            }
        }

        throw new InvalidOperationException($"{arrayProperty} 에 {keyProperty}='{keyValue}' 인 항목이 없다.");
    }

    /// <summary>
    /// 배열 항목의 들여쓰기를 원문에서 읽는다.
    /// <b>4칸이라고 가정하지 않는다</b> — 파일마다 다르고, 가정하면 diff 에 공백 변경이 섞인다.
    /// </summary>
    private static string ItemIndent(string json, int arrayStart, int arrayClose)
    {
        // 배열 안 첫 줄의 들여쓰기를 그대로 쓴다.
        int line = json.IndexOf('\n', arrayStart);

        if (line >= 0 && line < arrayClose)
        {
            int i = line + 1;
            int begin = i;

            while (i < arrayClose && (json[i] == ' ' || json[i] == '\t'))
            {
                i++;
            }

            if (i > begin)
            {
                return json[begin..i];
            }
        }

        // 한 줄짜리 배열이었다. 닫는 대괄호의 들여쓰기 + 2 칸으로 둔다.
        return CloseIndent(json, arrayClose) + "  ";
    }

    private static string CloseIndent(string json, int arrayClose)
    {
        int i = arrayClose - 1;

        while (i >= 0 && (json[i] == ' ' || json[i] == '\t'))
        {
            i--;
        }

        return json[(i + 1)..arrayClose];
    }

    /// <summary>여러 줄 항목의 2번째 줄부터 들여쓰기를 맞춘다.</summary>
    private static string Reindent(string itemJson, string indent)
    {
        string[] lines = itemJson.ReplaceLineEndings("\n").Split('\n');

        if (lines.Length == 1)
        {
            return itemJson;
        }

        var sb = new StringBuilder(itemJson.Length + (lines.Length * indent.Length));

        sb.Append(lines[0]);

        for (int i = 1; i < lines.Length; i++)
        {
            sb.Append(Environment.NewLine).Append(indent).Append(lines[i]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 편집 결과가 JSON 으로 파싱되는지 본다.
    /// <b>깨진 파일을 디스크에 쓰지 않는다</b> — 마스터데이터가 깨지면 기동 자체가 안 된다.
    /// </summary>
    private static string Verified(string json)
    {
        using (JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }))
        {
            return json;
        }
    }

    /// <summary>
    /// UTF-8 바이트 오프셋 → char 오프셋.
    /// <b>한글 주석·설명이 들어 있으므로 둘이 다르다</b> — 바이트 오프셋으로 문자열을 자르면 깨진다.
    /// </summary>
    private static int ByteToChar(string json, byte[] utf8, int byteOffset)
    {
        // ASCII 만 있으면 같다. 마스터데이터는 대부분 여기에 걸린다.
        if (byteOffset <= json.Length && Encoding.UTF8.GetByteCount(json.AsSpan(0, byteOffset)) == byteOffset)
        {
            return byteOffset;
        }

        return Encoding.UTF8.GetString(utf8, 0, byteOffset).Length;
    }
}

/// <summary>
/// <see cref="JsonSurgeon"/> 이 만드는 문자열 리터럴의 인코딩 옵션.
///
/// <b>한글을 escape 하지 않는다.</b> 마스터데이터는 사람이 읽고 고치는 파일이고
/// 다른 줄은 전부 한글 그대로다 — 도구가 넣은 줄만 <c>\uXXXX</c> 면 그 파일이 두 가지 표기를 갖게 된다.
/// </summary>
public static class JsonSurgeonText
{
    /// <summary>직렬화 옵션.</summary>
    public static JsonSerializerOptions Options { get; } =
        new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
