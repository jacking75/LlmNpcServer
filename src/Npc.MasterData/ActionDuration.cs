using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;

namespace Npc.MasterData;

/// <summary>
/// 스텝 하나의 소요 시간 (T22). <b>지터를 뺀 기대값</b>이다.
///
/// <para>
/// <b>여기가 유일한 구현이다.</b> <c>SimWorld</c>(게임서버 대역)와 동작 예측이 각자 계산하면
/// 그 둘은 반드시 갈라진다 — 그때 화면은 "3분" 이라 적고 실제는 5분이 걸리는데, 어느 쪽이
/// 맞는지 알 방법이 없다. Sim 은 이 값에 <c>±JitterPercent</c> 결정론 지터만 더한다.
/// </para>
///
/// <para>
/// <b>난수도 시각도 쓰지 않는다</b> (CLAUDE.md §2.3). 시계는 인자로 받는다.
/// </para>
/// </summary>
public static class ActionDuration
{
    /// <summary>
    /// 뛸 때의 미터당 시간 배수. <c>MovementSim.RunSpeedFactor</c> 와 같은 값이다 —
    /// 실제 이동은 그 클래스가 계산하고, 여기는 그 기대값이다.
    /// </summary>
    public const double RunSpeedFactor = 0.6;

    /// <summary>
    /// 이 스텝이 걸리는 게임 초. 0 이하는 1 로 올린다.
    /// </summary>
    /// <param name="data">마스터데이터.</param>
    /// <param name="action">액션 정의.</param>
    /// <param name="step">컴파일된 스텝.</param>
    /// <param name="from">현재 위치 POI. 0 이면 모름.</param>
    /// <param name="to">목표 POI. 0 이면 모름.</param>
    /// <param name="clockSeconds">지금 게임 시각(초). <c>until_time</c> 이 이것을 본다.</param>
    public static double Seconds(
        MasterDataSet data,
        ActionDef action,
        in CompiledStep step,
        PoiId from,
        PoiId to,
        double clockSeconds)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(action);

        double seconds = action.Duration.Kind switch
        {
            DurationKind.Fixed => action.Duration.BaseSeconds,
            DurationKind.Distance => Travel(data, action, step, from, to),
            DurationKind.Param => step.Count > 0 ? step.Count : action.Duration.BaseSeconds,
            DurationKind.UntilTime => UntilTime(data, action, step, clockSeconds),
            _ => action.Duration.BaseSeconds,
        };

        return seconds <= 0 ? 1 : seconds;
    }

    /// <summary>
    /// 이동 소요. 거리를 모르면(같은 곳·미정·연결 없음) 기본 시간이다.
    /// <c>speed</c> 열거 인자가 <c>run</c> 이면 미터당 시간에 <see cref="RunSpeedFactor"/> 를 곱한다.
    /// </summary>
    private static double Travel(MasterDataSet data, ActionDef action, in CompiledStep step, PoiId from, PoiId to)
    {
        if (from.Value == 0 || to.Value == 0 || from == to)
        {
            return action.Duration.BaseSeconds;
        }

        float distance = data.Pois.Distance(from, to);

        if (float.IsInfinity(distance))
        {
            return action.Duration.BaseSeconds;
        }

        double perMeter = action.Duration.PerMeterSeconds * (IsRunning(action, step) ? RunSpeedFactor : 1.0);

        return action.Duration.BaseSeconds + (distance * perMeter);
    }

    /// <summary>
    /// 이 스텝이 뛰는가. <b>액션 id 를 코드에 박지 않는다</b> — <c>run</c> 을 값으로 갖는
    /// 열거 파라미터를 찾아 그 첨자를 본다 (CLAUDE.md §2.4).
    /// </summary>
    public static bool IsRunning(ActionDef action, in CompiledStep step)
    {
        ArgumentNullException.ThrowIfNull(action);

        foreach (ParamDef param in action.Params)
        {
            if (param.Type != ParamType.Enum || !param.EnumValues.Contains("run"))
            {
                continue;
            }

            return step.ArgFlags < param.EnumValues.Length
                && string.Equals(param.EnumValues[step.ArgFlags], "run", StringComparison.Ordinal);
        }

        return false;
    }

    /// <summary>
    /// 지정한 시간대가 될 때까지. 이미 지났으면 다음 날 그 시각이다.
    /// <b>여기에는 지터를 주지 않는다</b> — "아침까지 잔다" 는 아침에 일어난다는 뜻이고,
    /// 흔들면 하루가 24시간이 아니게 되어 오차가 사이클마다 누적된다.
    /// </summary>
    private static double UntilTime(
        MasterDataSet data, ActionDef action, in CompiledStep step, double clockSeconds)
    {
        ParamDef? param = action.Param(action.Duration.Param ?? string.Empty);

        if (param is null || step.ArgFlags >= param.EnumValues.Length)
        {
            return action.Duration.BaseSeconds;
        }

        if (!Enum.TryParse(param.EnumValues[step.ArgFlags], out TimeOfDay target))
        {
            return action.Duration.BaseSeconds;
        }

        (int from, int _) = data.Buckets.GameHoursOf(target);

        double nowHours = clockSeconds / 3600.0 % 24.0;
        double waitHours = from - nowHours;

        if (waitHours <= 0)
        {
            waitHours += 24;
        }

        return waitHours * 3600.0;
    }
}
