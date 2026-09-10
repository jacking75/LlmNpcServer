using System.Collections.Immutable;
using System.Text.Json;

namespace Npc.MasterData.Authoring;

/// <summary>
/// 다음 <c>code</c>·<c>bit</c> 를 계산한다 (F-04).
///
/// <b>재배치는 이 API 로 불가능하다.</b> 기존 번호를 옮기는 메서드를 두지 않았다 —
/// 옮기는 순간 프리베이크된 플랜 2,880개가 통째로 깨지고, 그것은 되돌릴 수 없다
/// (CLAUDE.md §2.4). 여기가 하는 일은 <b>맨 뒤가 어디인지 알려 주는 것</b> 하나다.
///
/// <para>
/// <c>world_flags.json</c> 은 다르다. 비트에는 <c>reserved_bits</c> 예약 구간이 있고,
/// 22~23 처럼 <b>중간에 빈 구간</b>이 있다 — "다음 = 최대 + 1" 로 두면 그 구간을 영원히
/// 못 쓴다. 예약 구간을 오름차순으로 먼저 채운다.
/// </para>
/// </summary>
public static class CodeAllocator
{
    /// <summary>배열 이름과 번호 필드 이름. 파일마다 다르다.</summary>
    /// <param name="File">파일 이름.</param>
    /// <param name="Array">번호를 가진 항목이 담긴 배열의 속성 이름.</param>
    /// <param name="Field">번호 필드 이름.</param>
    /// <param name="StartsAtZero">0 이 유효한 번호인가. <c>items</c> 는 0 을 "없음" 으로 쓴다.</param>
    public readonly record struct Layout(string File, string Array, string Field, bool StartsAtZero);

    /// <summary>번호를 가진 마스터데이터 파일. 표에 없는 파일은 번호 체계가 없다.</summary>
    public static ImmutableArray<Layout> Layouts { get; } =
    [
        // items 의 code 0 은 "아이템 없음" 이다. ItemId.default 가 그 값이다.
        new("items.json", "items", "code", StartsAtZero: false),
        new("actions.json", "actions", "code", StartsAtZero: true),
        new("archetypes.json", "archetypes", "code", StartsAtZero: true),
        new("zones.json", "zones", "code", StartsAtZero: true),
        new("pois.json", "pois", "code", StartsAtZero: true),
        new("world_flags.json", "flags", "bit", StartsAtZero: true),
    ];

    /// <summary>이 파일의 배치. 모르는 파일이면 null.</summary>
    public static Layout? LayoutOf(string fileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);

        foreach (Layout layout in Layouts)
        {
            if (string.Equals(layout.File, fileName, StringComparison.OrdinalIgnoreCase))
            {
                return layout;
            }
        }

        return null;
    }

    /// <summary>
    /// 이 파일에서 다음에 쓸 번호. 파일을 읽어 판단한다 — 코드에 개수를 적어 두지 않는다.
    /// </summary>
    /// <param name="masterDataDirectory"><c>masterdata/</c> 경로.</param>
    /// <param name="fileName">파일 이름. <see cref="Layouts"/> 에 있어야 한다.</param>
    public static int Next(string masterDataDirectory, string fileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(masterDataDirectory);

        if (LayoutOf(fileName) is not { } layout)
        {
            throw new ArgumentException($"'{fileName}' 에는 번호 체계가 없다.", nameof(fileName));
        }

        string path = Path.Combine(masterDataDirectory, fileName);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));

        ImmutableArray<int> used = Used(document.RootElement, layout);
        ImmutableArray<int> reserved = Reserved(document.RootElement);

        // 예약 구간이 있으면 거기가 다음 자리다. 오름차순으로 첫 빈칸.
        foreach (int bit in reserved)
        {
            if (!used.Contains(bit))
            {
                return bit;
            }
        }

        if (used.IsEmpty)
        {
            return layout.StartsAtZero ? 0 : 1;
        }

        int next = used.Max() + 1;

        // 예약 구간이 선언돼 있는데 다 찼으면 그 사실이 결론이다 — 그 뒤는 예약 밖이다.
        if (!reserved.IsEmpty && next > reserved.Max())
        {
            throw new InvalidOperationException(
                $"{fileName}: 예약 구간({reserved.Min()}~{reserved.Max()})이 전부 찼다. "
                + "새 구간을 reserved_bits 에 먼저 선언한다 — 예약 밖 비트를 쓰면 의미가 없는 번호가 된다.");
        }

        return next;
    }

    /// <summary>
    /// 번호가 0 부터 빈틈없이 이어지는가. 아키타입은 이것이 깨지면 버킷 첨자가 성립하지 않는다.
    /// </summary>
    /// <returns>빠진 번호 (오름차순). 비었으면 연속이다.</returns>
    public static ImmutableArray<int> Gaps(string masterDataDirectory, string fileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(masterDataDirectory);

        if (LayoutOf(fileName) is not { } layout)
        {
            throw new ArgumentException($"'{fileName}' 에는 번호 체계가 없다.", nameof(fileName));
        }

        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(masterDataDirectory, fileName)));

        ImmutableArray<int> used = Used(document.RootElement, layout);

        if (used.IsEmpty)
        {
            return [];
        }

        ImmutableArray<int> reserved = Reserved(document.RootElement);
        var gaps = ImmutableArray.CreateBuilder<int>();

        for (int i = layout.StartsAtZero ? 0 : 1; i < used.Max(); i++)
        {
            // 예약 구간의 빈칸은 빠진 것이 아니라 비워 둔 것이다.
            if (!used.Contains(i) && !reserved.Contains(i))
            {
                gaps.Add(i);
            }
        }

        return gaps.ToImmutable();
    }

    /// <summary>선언된 예약 구간. 없으면 빈 배열.</summary>
    public static ImmutableArray<int> ReservedBits(string masterDataDirectory, string fileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(masterDataDirectory);
        ArgumentException.ThrowIfNullOrEmpty(fileName);

        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(masterDataDirectory, fileName)));

        return Reserved(document.RootElement);
    }

    private static ImmutableArray<int> Used(JsonElement root, Layout layout)
    {
        if (!root.TryGetProperty(layout.Array, out JsonElement array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var used = ImmutableArray.CreateBuilder<int>(array.GetArrayLength());

        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.TryGetProperty(layout.Field, out JsonElement number)
                && number.ValueKind == JsonValueKind.Number)
            {
                used.Add(number.GetInt32());
            }
        }

        return used.ToImmutable();
    }

    private static ImmutableArray<int> Reserved(JsonElement root)
    {
        if (!root.TryGetProperty("reserved_bits", out JsonElement array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var reserved = ImmutableArray.CreateBuilder<int>(array.GetArrayLength());

        foreach (JsonElement bit in array.EnumerateArray())
        {
            if (bit.ValueKind == JsonValueKind.Number)
            {
                reserved.Add(bit.GetInt32());
            }
        }

        reserved.Sort();

        return reserved.ToImmutable();
    }
}
