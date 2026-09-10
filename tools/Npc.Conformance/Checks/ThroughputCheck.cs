using System.Globalization;
using Npc.Contracts;

namespace Npc.Conformance.Checks;

/// <summary>
/// C7 — 처리량 (B-07).
///
/// <para>
/// <b>드롭은 우리 쪽에서 센다.</b> 명령 링이 넘치면 <see cref="CommandPriority.Cosmetic"/>
/// 부터 버리는데, 그것이 일어났다는 것은 게임서버가 우리 배치를 못 따라오고 있다는 뜻이다.
/// </para>
///
/// <para>
/// <b>이벤트 백로그는 프레임당 건수로 본다.</b> 게임서버가 이벤트를 모아 두었다가 한꺼번에
/// 쏟으면 프레임당 건수가 튄다 — 상한 256 을 안 넘겨도 평균의 몇 배가 되는 프레임이 있으면
/// 어딘가에서 밀리고 있다.
/// </para>
/// </summary>
public sealed class ThroughputCheck : IConformanceCheck
{
    /// <summary>프레임당 건수가 평균의 이 배를 넘으면 백로그로 본다.</summary>
    public const int BurstFactor = 8;

    /// <summary>이 건수 아래는 버스트로 세지 않는다. 작은 수의 배수는 의미가 없다.</summary>
    public const int BurstFloor = 32;

    /// <inheritdoc/>
    public string Id => "C7.throughput";

    /// <inheritdoc/>
    public string Title => "처리량 — 드롭 0 · 이벤트 백로그 없음";

    /// <inheritdoc/>
    public CheckResult Run(Observation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        var violations = new List<string>();

        if (observation.CommandsDropped > 0)
        {
            violations.Add(
                $"명령 드롭 {observation.CommandsDropped}건 — 게임서버가 배치를 못 따라온다");
        }

        // 재동기화 프레임은 원래 크다(로스터 전원). 그것을 버스트로 세면 항상 실패한다.
        var perFrame = new Dictionary<int, int>();
        int firstNonResync = int.MaxValue;

        foreach (Observed observed in observation.Events)
        {
            perFrame[observed.Frame] = perFrame.GetValueOrDefault(observed.Frame) + 1;

            if (observed.Event.Kind is not (GameEventKind.NpcSpawned
                or GameEventKind.ZoneStateChanged
                or GameEventKind.WeatherChanged))
            {
                firstNonResync = Math.Min(firstNonResync, observed.Frame);
            }
        }

        int[] steady = [.. perFrame.Where(p => p.Key >= firstNonResync).Select(p => p.Value)];

        string burst = "버스트 미판정 (재동기화 뒤 프레임이 없다)";

        if (steady.Length >= 4)
        {
            double average = steady.Average();
            int worst = steady.Max();

            burst = string.Create(
                CultureInfo.InvariantCulture,
                $"프레임당 평균 {average:F1}건 · 최대 {worst}건");

            if (worst >= BurstFloor && worst > average * BurstFactor)
            {
                violations.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"프레임 하나에 {worst}건 — 평균 {average:F1}건의 {worst / average:F1}배다. 이벤트를 모아 두었다가 쏟고 있다"));
            }
        }

        string detail = string.Create(
            CultureInfo.InvariantCulture,
            $"명령 {observation.Commands.Length}건 · 드롭 {observation.CommandsDropped} · "
            + $"프레임 {perFrame.Count}개 · {burst}");

        return violations.Count == 0
            ? CheckResult.Pass(Id, Title, detail)
            : CheckResult.Fail(Id, Title, detail, violations);
    }
}
