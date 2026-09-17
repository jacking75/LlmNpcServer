using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace Npc.Studio.Services;

/// <summary>
/// 예외를 화면 문장으로 (H08).
///
/// <para>
/// <b>영어 스택은 "내 잘못이 아니다" 로 읽힌다.</b> <c>'}' is invalid after a single JSON value</c>
/// 를 본 초보자는 자기가 무엇을 해야 하는지 알 수 없고, 그래서 도구를 닫는다.
/// 대응표는 2차 계획 부록 C 다.
/// </para>
///
/// <para>
/// <b>여기서 예외를 삼키지 않는다.</b> 문장을 만들 뿐이고, 부르는 쪽이 토스트나 인라인으로 낸다.
/// </para>
/// </summary>
public static class StudioErrors
{
    /// <summary>예외 하나를 한국어 한 문장으로.</summary>
    /// <param name="error">예외.</param>
    /// <param name="what">무엇을 하다가 났는가 ("저장" · "진단"). 비면 앞말을 붙이지 않는다.</param>
    public static string Humanize(Exception error, string what = "")
    {
        ArgumentNullException.ThrowIfNull(error);

        string body = Body(error);

        return what.Length == 0 ? body : $"{what}하지 못했다 — {body}";
    }

    private static string Body(Exception error) => error switch
    {
        JsonException json => Json(json),
        UnauthorizedAccessException access =>
            $"쓸 권한이 없다: {PathIn(access.Message)}",
        DirectoryNotFoundException directory =>
            $"폴더가 없다: {PathIn(directory.Message)}",
        FileNotFoundException file =>
            $"파일이 없다: {file.FileName ?? PathIn(file.Message)}",
        Win32Exception =>
            "`dotnet` 을 찾지 못했다 — PATH 를 확인한다. 명령을 터미널에서 직접 돌릴 수 있다.",
        IOException io =>
            $"파일을 쓸 수 없다 — 다른 프로그램(편집기 · 백신)이 열고 있거나 권한이 없다. {io.Message}",
        HttpRequestException =>
            "서버에 연결할 수 없다 — 떠 있는지 · 포트가 맞는지 본다.",
        TaskCanceledException =>
            "응답이 없어 기다리기를 그만뒀다.",
        ArgumentException argument => Strip(argument.Message),
        InvalidOperationException invalid => invalid.Message,
        InvalidDataException data => data.Message,
        _ => Strip(error.Message),
    };

    /// <summary>
    /// <c>LineNumber: 12 | BytePositionInLine: 3</c> 은 <b>0 기준</b>이다 —
    /// 편집기가 1 기준으로 세므로 그대로 보여 주면 한 줄 위를 보게 된다.
    /// </summary>
    private static string Json(JsonException json)
    {
        if (json.LineNumber is not { } line)
        {
            return "JSON 문법 오류다. 괄호 · 쉼표를 본다.";
        }

        long column = json.BytePositionInLine ?? 0;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"JSON 문법 오류 — {line + 1}행 {column + 1}열 근처. 괄호 · 쉼표를 본다.");
    }

    /// <summary>
    /// <c>(Parameter 'draft')</c> 접미를 없앤다. 우리가 던지는 것은 사람용 문장이라
    /// 인자 이름이 붙으면 "내가 뭘 잘못 넣었나" 로 읽힌다.
    /// </summary>
    private static string Strip(string message)
    {
        int at = message.IndexOf(" (Parameter '", StringComparison.Ordinal);

        return at > 0 ? message[..at] : message;
    }

    /// <summary>영어 메시지 안의 작은따옴표 경로만 꺼낸다. 없으면 통째로.</summary>
    private static string PathIn(string message)
    {
        int open = message.IndexOf('\'');
        int close = open < 0 ? -1 : message.IndexOf('\'', open + 1);

        return open >= 0 && close > open ? message[(open + 1)..close] : message;
    }
}
