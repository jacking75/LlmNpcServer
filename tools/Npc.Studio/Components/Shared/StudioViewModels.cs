using System.Collections.Immutable;
using Npc.MasterData;
using Npc.Narrative;

namespace Npc.Studio.Components.Shared;

/// <summary>행동 칩 하나 (T05·T10).</summary>
/// <param name="Id">액션 id.</param>
/// <param name="Name">한국어 표기.</param>
/// <param name="Allowed">이 직업이 쓸 수 있는가.</param>
/// <param name="Description">액션 설명.</param>
public readonly record struct StudioActionChip(string Id, string Name, bool Allowed, string Description);

/// <summary>하루 띠 한 칸 (T06·T22).</summary>
/// <param name="Label">칸에 적는 말.</param>
/// <param name="Title">hover 설명.</param>
/// <param name="Share">전체 중 차지하는 비율 (0~1).</param>
/// <param name="Color">칸 색.</param>
/// <param name="Index">세그먼트 첨자. 클릭하면 그 시각으로 간다.</param>
/// <param name="Bad">실패·건너뜀인가.</param>
public readonly record struct TimelineCell(
    string Label, string Title, double Share, string Color, int Index, bool Bad);

/// <summary>
/// 화면이 쓰는 작은 조립 함수들. <b>계산은 코어에 있다</b> — 여기는 색과 배치뿐이다.
/// </summary>
public static class StudioView
{
    /// <summary>
    /// 주소에 질의 항목 하나를 붙인다 (H10).
    ///
    /// <b>회귀 — 이미 <c>?</c> 가 있는 주소에 <c>?</c> 를 또 붙였다.</b> 그러면
    /// <c>tab</c> 값이 <c>"edit?field=…"</c> 가 되어 탭 <c>switch</c> 의 <c>default</c>
    /// (고급 JSON)로 떨어졌다 — "고치러 가기" 가 초보자를 원문 편집기에 버렸다.
    /// </summary>
    /// <param name="url">기존 주소.</param>
    /// <param name="key">질의 이름.</param>
    /// <param name="value">질의 값. 비면 주소를 그대로 돌려준다.</param>
    public static string WithQuery(string url, string key, string value)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (string.IsNullOrEmpty(value))
        {
            return url;
        }

        char separator = url.Contains('?', StringComparison.Ordinal) ? '&' : '?';

        return url + separator + key + "=" + Uri.EscapeDataString(value);
    }

    /// <summary>액션 카테고리별 칩. 허용·미허용을 한 표에 담는다.</summary>
    /// <param name="data">마스터데이터.</param>
    /// <param name="def">아키타입.</param>
    public static ImmutableArray<(string Category, ImmutableArray<StudioActionChip> Chips)> ActionChips(
        MasterDataSet data, ArchetypeDef def)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(def);

        var rows = ImmutableArray.CreateBuilder<(string, ImmutableArray<StudioActionChip>)>();

        foreach (ActionCategory category in Enum.GetValues<ActionCategory>())
        {
            ImmutableArray<StudioActionChip> chips =
            [
                .. data.Actions.Actions
                    .Where(a => a.Category == category)
                    .Select(a => new StudioActionChip(
                        a.Id, Lexicon.Action(a.Id), def.Allows(a.Code), a.Description)),
            ];

            if (!chips.IsEmpty)
            {
                rows.Add((Lexicon.Of(category), chips));
            }
        }

        return rows.ToImmutable();
    }

    /// <summary>예측 세그먼트를 띠 칸으로. 폭은 <b>예측 시간</b>에 비례한다.</summary>
    /// <param name="forecast">예측.</param>
    public static ImmutableArray<TimelineCell> Cells(Forecast forecast)
    {
        ArgumentNullException.ThrowIfNull(forecast);

        int total = Math.Max(1, forecast.CoveredSeconds);
        var cells = ImmutableArray.CreateBuilder<TimelineCell>(forecast.Segments.Length);

        for (int i = 0; i < forecast.Segments.Length; i++)
        {
            ForecastSegment segment = forecast.Segments[i];

            if (segment.Seconds <= 0)
            {
                continue;
            }

            int clock = (forecast.StartClockSeconds + segment.StartSeconds) % DayForecast.DaySeconds;

            cells.Add(new TimelineCell(
                segment.Seconds * 40 >= total ? Lexicon.Action(segment.ActionId) : string.Empty,
                $"{clock / 3600:00}:{clock % 3600 / 60:00} · {segment.Caption}",
                segment.Seconds / (double)total,
                ColorOf(segment.Kind),
                i,
                segment.Code.Length > 0));
        }

        return cells.ToImmutable();
    }

    /// <summary>세그먼트 색. 부록 E 의 상태 구분과 같은 뜻이다.</summary>
    public static string ColorOf(SegmentKind kind) => kind switch
    {
        SegmentKind.Move => "#4a78c2",
        SegmentKind.Act => "#e8873a",
        SegmentKind.Sleep => "#3b4a7a",
        SegmentKind.Wait => "#6b7689",
        SegmentKind.Skipped => "#8d97a5",
        _ => "#a03a4c",
    };

    /// <summary>세그먼트 배지 기호 (부록 E).</summary>
    public static string BadgeOf(SegmentKind kind) => kind switch
    {
        SegmentKind.Move => "→",
        SegmentKind.Act => "⚒",
        SegmentKind.Sleep => "z",
        SegmentKind.Wait => "…",
        SegmentKind.Skipped => "✕",
        _ => "!",
    };

    /// <summary>POI 유형 색. 지도 점이 이것으로 갈린다 (부록 E).</summary>
    public static string PoiColor(PoiType type) => type switch
    {
        PoiType.Home => "#8d97a5",
        PoiType.Workplace => "#4a78c2",
        PoiType.Market => "#d9b23c",
        PoiType.Tavern => "#e8873a",
        PoiType.Temple => "#8a6bc9",
        PoiType.Gate => "#9a7b5a",
        PoiType.Field => "#5aa85a",
        PoiType.Wilderness => "#7fbf5f",
        _ => "#667085",
    };

    /// <summary>게임 초 → <c>07:30</c>.</summary>
    public static string Clock(int seconds)
    {
        int wrapped = ((seconds % DayForecast.DaySeconds) + DayForecast.DaySeconds) % DayForecast.DaySeconds;

        return $"{wrapped / 3600:00}:{wrapped % 3600 / 60:00}";
    }

    /// <summary>초 → "약 3분".</summary>
    public static string Duration(int seconds) =>
        seconds < 60 ? $"{seconds}초" : seconds < 3600 ? $"{seconds / 60}분" : $"{seconds / 3600}시간 {seconds % 3600 / 60}분";
}
