using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npc.Contracts;

namespace Npc.MasterData;

/// <summary>세력 하나. <c>factions.json</c> 의 한 줄이다 (D-04).</summary>
/// <param name="Id">세력 id.</param>
/// <param name="Code">
/// <see cref="FactionId"/>. 패킷의 <c>Faction</c> 슬롯 값이다 (B-02).
/// <b>0 은 "미지정" 이라 쓸 수 없다</b> — 세력을 안 준 NPC 와 구분해야 한다.
/// </param>
/// <param name="Description">사람이 읽는 설명.</param>
/// <param name="HostileTo">
/// 적대 세력 id. <b>NPC 서버는 이 값을 판정에 쓰지 않는다</b> — 적대 판정은 게임서버 몫이고(B-06),
/// 여기 있는 이유는 표를 한 곳에 두기 위해서다.
/// </param>
public readonly record struct FactionDef(
    string Id, FactionId Code, string Description, ImmutableArray<string> HostileTo);

/// <summary>
/// 세력 표 (D-04).
///
/// <para>
/// <b>왜 NPC 서버가 이것을 아는가.</b> 적대 판정은 게임서버가 한다 (B-06 — 그래서 <c>Faction</c> 은
/// "통과만 한다" 고 적었다). 다만 <b>통과시키려면 값이 있어야 한다</b>: B-02 가 슬롯을 뚫어 둔 뒤로
/// 아무도 채우지 않아 지금까지 항상 0 이었고, 0 은 게임서버가 판정할 재료가 아니다.
/// </para>
/// </summary>
public sealed class FactionTable
{
    /// <summary>파일 이름.</summary>
    public const string FileName = "factions.json";

    private readonly ImmutableArray<FactionDef> _factions;

    private FactionTable(ImmutableArray<FactionDef> factions) => _factions = factions;

    /// <summary>code 오름차순의 세력들.</summary>
    public ImmutableArray<FactionDef> Factions => _factions;

    /// <summary>세력 수.</summary>
    public int Count => _factions.Length;

    /// <summary>id → code. 로딩·검증 경로에서만 쓴다.</summary>
    /// <param name="id">세력 id.</param>
    /// <param name="code">찾은 code.</param>
    public bool TryGet(string? id, out FactionId code)
    {
        foreach (FactionDef def in _factions)
        {
            if (string.Equals(def.Id, id, StringComparison.Ordinal))
            {
                code = def.Code;
                return true;
            }
        }

        code = default;
        return false;
    }

    /// <summary>code → id. 없으면 빈 문자열.</summary>
    /// <param name="code">세력 code.</param>
    public string NameOf(FactionId code)
    {
        foreach (FactionDef def in _factions)
        {
            if (def.Code == code)
            {
                return def.Id;
            }
        }

        return string.Empty;
    }

    /// <summary>읽고 검증한다. <b>어긋나면 던진다</b> — 기동 실패다.</summary>
    /// <param name="path">파일 경로.</param>
    public static FactionTable Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        FactionsFile? file = JsonSerializer.Deserialize(
            File.ReadAllText(path), FactionJsonContext.Default.FactionsFile);

        if (file?.Factions is null || file.Factions.Length == 0)
        {
            throw new InvalidDataException($"{FileName} 에서 세력을 하나도 읽지 못했다: {path}");
        }

        return Build(file.Factions, path);
    }

    /// <summary>JSON 문자열에서. 테스트 픽스처용.</summary>
    /// <param name="json">파일 내용.</param>
    public static FactionTable Parse(string json)
    {
        FactionsFile? file = JsonSerializer.Deserialize(
            json, FactionJsonContext.Default.FactionsFile);

        if (file?.Factions is null || file.Factions.Length == 0)
        {
            throw new InvalidDataException($"{FileName} 에서 세력을 하나도 읽지 못했다.");
        }

        return Build(file.Factions, FileName);
    }

    private static FactionTable Build(FactionDto[] dtos, string where)
    {
        var seenCode = new HashSet<int>(dtos.Length);
        var seenId = new HashSet<string>(dtos.Length, StringComparer.Ordinal);
        var factions = ImmutableArray.CreateBuilder<FactionDef>(dtos.Length);

        foreach (FactionDto dto in dtos.OrderBy(d => d.Code))
        {
            if (string.IsNullOrWhiteSpace(dto.Id))
            {
                throw new InvalidDataException($"{where}: id 가 빈 줄이 있다 (code {dto.Code}).");
            }

            if (dto.Code is < 1 or > ushort.MaxValue)
            {
                throw new InvalidDataException(
                    $"{where}: {dto.Id} 의 code {dto.Code} 가 범위 밖이다. "
                    + "0 은 '미지정' 이라 쓸 수 없다.");
            }

            if (!seenCode.Add(dto.Code))
            {
                throw new InvalidDataException($"{where}: code {dto.Code} 가 중복이다 ({dto.Id}).");
            }

            if (!seenId.Add(dto.Id))
            {
                throw new InvalidDataException($"{where}: id '{dto.Id}' 가 두 번 나온다.");
            }

            factions.Add(new FactionDef(
                dto.Id,
                new FactionId((ushort)dto.Code),
                dto.Desc ?? string.Empty,
                dto.HostileTo is null ? [] : [.. dto.HostileTo]));
        }

        // 적대 목록이 가리키는 세력이 실제로 있어야 한다 — 오타면 그 적대가 조용히 사라진다.
        var ids = factions.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);

        foreach (FactionDef faction in factions)
        {
            foreach (string other in faction.HostileTo)
            {
                if (!ids.Contains(other))
                {
                    throw new InvalidDataException(
                        $"{where}: {faction.Id}.hostile_to 의 '{other}' 가 표에 없다.");
                }
            }
        }

        return new FactionTable(factions.ToImmutable());
    }
}

internal sealed record FactionsFile(int Version, FactionDto[] Factions);

internal sealed record FactionDto(string Id, int Code, string? Desc, string[]? HostileTo);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(FactionsFile))]
internal sealed partial class FactionJsonContext : JsonSerializerContext;
