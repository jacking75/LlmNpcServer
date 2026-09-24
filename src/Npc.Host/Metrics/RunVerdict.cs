namespace Npc.Host.Metrics;

/// <summary>실행 종료 시 기존 계측값으로 정상 여부를 판정한다.</summary>
public static class RunVerdict
{
    /// <summary>틱 실행 시간 예산(ms).</summary>
    public const double TickBudgetMs = 20;

    /// <summary>오류 주입이 없는 회차에서 허용하는 합성 타임아웃 비율.</summary>
    public const double TimeoutRatioLimit = 0.05;

    /// <summary>첫 번째 초과 항목. 모두 정상이면 null.</summary>
    public static string? FirstIssue(
        double p99Ms,
        long bytesPerTick,
        long eventGaps,
        long overruns,
        long linkDrops,
        long simDrops,
        long timeouts,
        long steps,
        bool checkTimeouts)
    {
        if (p99Ms > TickBudgetMs) return $"틱 p99 {p99Ms:0.###}ms (예산 {TickBudgetMs:0}ms)";
        if (bytesPerTick > 0) return $"틱 할당 {bytesPerTick}B (기준 0B)";
        if (eventGaps > 0) return $"이벤트 갭 {eventGaps} (기준 0)";
        if (overruns > 0) return $"틱 예산 초과 {overruns}회 (기준 0)";
        if (linkDrops > 0 || simDrops > 0) return $"명령 드롭 link {linkDrops}, sim {simDrops} (기준 0)";
        if (checkTimeouts && steps > 0 && (double)timeouts / steps > TimeoutRatioLimit)
            return $"합성 타임아웃 {(double)timeouts / steps:P1} (기준 {TimeoutRatioLimit:P0})";
        return null;
    }
}
