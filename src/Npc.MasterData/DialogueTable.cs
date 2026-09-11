using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npc.Contracts;

namespace Npc.MasterData;

/// <summary>대사 주제 하나. <c>dialogue_lines.json</c> 의 한 줄이다 (D-02).</summary>
/// <param name="Id">주제 id. <c>actions.json</c> 의 <c>emits.map.Dialogue</c> 심볼과 같은 문자열이다.</param>
/// <param name="Code">
/// <see cref="DialogueId"/>. <b>절대 재배치하지 않는다</b> — 프리베이크된 플랜과 게임서버의
/// 대사 표가 이 번호로 이어져 있다 (CLAUDE.md §2.4).
/// </param>
/// <param name="Tags">묶음 태그. 표시·검수용이고 런타임은 읽지 않는다.</param>
/// <param name="Priority">이 주제의 발화 우선순위. 게임서버가 쓴다. 기본 <c>Normal</c>.</param>
public readonly record struct DialogueLine(
    string Id, DialogueId Code, ImmutableArray<string> Tags, string Priority);

/// <summary>
/// 대사 주제 표 (D-02).
///
/// <para>
/// <b>왜 파일로 뺐나.</b> 예전에는 <c>actions.json</c> 의 <c>emits.map.Dialogue</c> 심볼을
/// <c>SortedSet</c> 으로 모아 그 <b>첨자</b>를 <see cref="DialogueId"/> 로 썼다. 주제를 하나
/// 추가하면 사전순 뒤쪽이 통째로 밀리고, 프리베이크된 플랜 2,880개와 게임서버의 대사 표가
/// <b>조용히</b> 어긋났다 — 증상은 "NPC 가 엉뚱한 말을 한다" 이고 원인은 아무 로그에도 없다.
/// </para>
///
/// <para>
/// <b>문구는 여기 없다.</b> 패킷은 code 만 싣고(N3) 문구를 고르는 것은 표시 계층이다 —
/// 로컬라이즈 표의 <c>dialogue.&lt;id&gt;</c> 키가 그것이다.
/// </para>
/// </summary>
public sealed class DialogueTable
{
    /// <summary>파일 이름.</summary>
    public const string FileName = "dialogue_lines.json";

    private readonly ImmutableArray<string> _names;
    private readonly FrozenLookup _byId;

    private DialogueTable(ImmutableArray<DialogueLine> lines, ImmutableArray<string> names)
    {
        Lines = lines;
        _names = names;
        _byId = FrozenLookup.Of(lines);
    }

    /// <summary>code 오름차순의 주제들.</summary>
    public ImmutableArray<DialogueLine> Lines { get; }

    /// <summary>
    /// code 를 첨자로 하는 이름 표. <b>빈 칸은 빈 문자열이다</b> —
    /// code 에 구멍이 있어도 첨자 접근이 성립해야 한다.
    /// </summary>
    public ImmutableArray<string> Names => _names;

    /// <summary>정의된 주제 수. <see cref="Names"/> 의 길이가 아니다 (구멍이 있을 수 있다).</summary>
    public int Count => Lines.Length;

    /// <summary>id → code. 로딩·검증 경로에서만 쓴다.</summary>
    /// <param name="id">주제 id.</param>
    /// <param name="code">찾은 code.</param>
    public bool TryGet(string id, out DialogueId code) => _byId.TryGet(id, out code);

    /// <summary>이 id 가 표에 있는가. V14 가 쓴다.</summary>
    /// <param name="id">주제 id.</param>
    public bool Contains(string id) => _byId.TryGet(id, out _);

    /// <summary>읽고 검증한다. <b>어긋나면 던진다</b> — 기동 실패다 (CLAUDE.md §2.4).</summary>
    /// <param name="path">파일 경로.</param>
    public static DialogueTable Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        DialogueLinesFile? file = JsonSerializer.Deserialize(
            File.ReadAllText(path), DialogueJsonContext.Default.DialogueLinesFile);

        if (file?.Lines is null || file.Lines.Length == 0)
        {
            throw new InvalidDataException($"{FileName} 에서 대사 주제를 하나도 읽지 못했다: {path}");
        }

        return Build(file.Lines, path);
    }

    /// <summary>JSON 문자열에서. 테스트 픽스처용.</summary>
    /// <param name="json">파일 내용.</param>
    public static DialogueTable Parse(string json)
    {
        DialogueLinesFile? file = JsonSerializer.Deserialize(
            json, DialogueJsonContext.Default.DialogueLinesFile);

        if (file?.Lines is null || file.Lines.Length == 0)
        {
            throw new InvalidDataException($"{FileName} 에서 대사 주제를 하나도 읽지 못했다.");
        }

        return Build(file.Lines, FileName);
    }

    private static DialogueTable Build(DialogueLineDto[] dtos, string where)
    {
        var byCode = new string?[dtos.Length == 0 ? 1 : dtos.Max(d => d.Code) + 1];
        var seen = new HashSet<string>(dtos.Length, StringComparer.Ordinal);
        var lines = ImmutableArray.CreateBuilder<DialogueLine>(dtos.Length);

        foreach (DialogueLineDto dto in dtos.OrderBy(d => d.Code))
        {
            if (string.IsNullOrWhiteSpace(dto.Id))
            {
                throw new InvalidDataException($"{where}: id 가 빈 줄이 있다 (code {dto.Code}).");
            }

            if (dto.Code is < 0 or > ushort.MaxValue)
            {
                throw new InvalidDataException($"{where}: {dto.Id} 의 code {dto.Code} 가 범위 밖이다.");
            }

            if (byCode[dto.Code] is { } other)
            {
                throw new InvalidDataException(
                    $"{where}: code {dto.Code} 가 중복이다 ({other} · {dto.Id}). "
                    + "code 는 재배치하지 않고 추가는 뒤에만 한다.");
            }

            if (!seen.Add(dto.Id))
            {
                throw new InvalidDataException($"{where}: id '{dto.Id}' 가 두 번 나온다.");
            }

            byCode[dto.Code] = dto.Id;

            lines.Add(new DialogueLine(
                dto.Id,
                new DialogueId((ushort)dto.Code),
                dto.Tags is null ? [] : [.. dto.Tags],
                string.IsNullOrWhiteSpace(dto.Priority) ? "Normal" : dto.Priority));
        }

        return new DialogueTable(
            lines.ToImmutable(),
            [.. byCode.Select(name => name ?? string.Empty)]);
    }

    /// <summary>id → code. 개수가 적어 선형 탐색이 해시보다 싸다.</summary>
    private readonly struct FrozenLookup(ImmutableArray<DialogueLine> lines)
    {
        private readonly ImmutableArray<DialogueLine> _lines = lines;

        public static FrozenLookup Of(ImmutableArray<DialogueLine> lines) => new(lines);

        public bool TryGet(string id, out DialogueId code)
        {
            foreach (DialogueLine line in _lines)
            {
                if (string.Equals(line.Id, id, StringComparison.Ordinal))
                {
                    code = line.Code;
                    return true;
                }
            }

            code = default;
            return false;
        }
    }
}

internal sealed record DialogueLinesFile(int Version, DialogueLineDto[] Lines);

internal sealed record DialogueLineDto(string Id, int Code, string[]? Tags, string? Priority);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(DialogueLinesFile))]
internal sealed partial class DialogueJsonContext : JsonSerializerContext;
