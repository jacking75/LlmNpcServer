using System.Globalization;
using Npc.Contracts;

namespace Npc.Conformance.Checks;

/// <summary>
/// C1 — 핸드셰이크와 재동기화 (B-07).
///
/// <para>
/// <b>재동기화 순서가 규약이다.</b> NPC 서버는 <c>NpcSpawned</c> 를 받아야 그 NPC 가 세계에
/// 있다고 보고 명령을 내기 시작한다. 존 상태가 먼저 오면 그 사이의 상태 변화가
/// <b>아무도 안 듣는 구간</b>에 떨어지고, 증상은 "재기동하면 몇몇 NPC 가 엉뚱한 플랜으로
/// 돈다" 로만 나타난다.
/// </para>
///
/// <para>
/// <b>존 상태와 날씨는 섞여도 된다.</b> 규약이 요구하는 것은 "존마다 둘 다 한 번씩" 이지
/// "전자 전부 → 후자 전부" 가 아니다 — 대역 구현은 존별로 둘을 나란히 낸다.
/// </para>
/// </summary>
public sealed class HandshakeCheck : IConformanceCheck
{
    /// <summary>이벤트 프레임 하나에 실을 수 있는 상한. docs/reference_link.html §5.5.</summary>
    public const int MaxEventsPerFrame = 256;

    /// <inheritdoc/>
    public string Id => "C1.handshake";

    /// <inheritdoc/>
    public string Title => "핸드셰이크 · 재동기화";

    /// <inheritdoc/>
    public CheckResult Run(Observation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        var violations = new List<string>();

        if (!observation.Connected)
        {
            return CheckResult.Fail(
                Id, Title, $"링크가 붙지 않았다: {observation.NegotiationDetail}", ["연결 실패"]);
        }

        if (observation.ProtocolVersion is < 1 or > 2)
        {
            violations.Add($"프로토콜 버전이 범위 밖이다: {observation.ProtocolVersion}");
        }

        // 스폰이 존 상태보다 먼저다.
        int lastSpawn = -1;
        int firstZone = int.MaxValue;

        for (int i = 0; i < observation.Events.Length; i++)
        {
            switch (observation.Events[i].Event.Kind)
            {
                case GameEventKind.NpcSpawned:
                    lastSpawn = i;
                    break;

                case GameEventKind.ZoneStateChanged:
                case GameEventKind.WeatherChanged:
                    firstZone = Math.Min(firstZone, i);
                    break;

                default:
                    break;
            }
        }

        int spawns = observation.Count(GameEventKind.NpcSpawned);

        if (spawns < observation.NpcCount)
        {
            violations.Add(
                $"재동기화 스폰이 모자란다: {spawns}건 · 로스터 {observation.NpcCount}");
        }

        if (firstZone != int.MaxValue && lastSpawn > firstZone)
        {
            violations.Add(
                $"존 상태({firstZone}번째)가 스폰({lastSpawn}번째)보다 먼저 왔다");
        }

        // 존마다 상태·날씨가 한 번씩은 와야 한다.
        var zoneStates = new HashSet<ushort>();
        var weather = new HashSet<ushort>();

        foreach (Observed observed in observation.Of(GameEventKind.ZoneStateChanged))
        {
            zoneStates.Add(observed.Event.Zone.Value);
        }

        foreach (Observed observed in observation.Of(GameEventKind.WeatherChanged))
        {
            weather.Add(observed.Event.Zone.Value);
        }

        if (observation.ZoneCount > 0)
        {
            if (zoneStates.Count < observation.ZoneCount)
            {
                violations.Add(
                    $"ZoneStateChanged 가 온 존이 {zoneStates.Count}개 · 기대 {observation.ZoneCount}");
            }

            if (weather.Count < observation.ZoneCount)
            {
                violations.Add(
                    $"WeatherChanged 가 온 존이 {weather.Count}개 · 기대 {observation.ZoneCount}");
            }
        }

        // 프레임당 상한.
        var perFrame = new Dictionary<int, int>();

        foreach (Observed observed in observation.Events)
        {
            perFrame[observed.Frame] = perFrame.GetValueOrDefault(observed.Frame) + 1;
        }

        int worst = 0;

        foreach ((int frame, int count) in perFrame)
        {
            worst = Math.Max(worst, count);

            if (count > MaxEventsPerFrame)
            {
                violations.Add($"프레임 {frame} 에 이벤트 {count}건 (상한 {MaxEventsPerFrame})");
            }
        }

        string detail = string.Create(
            CultureInfo.InvariantCulture,
            $"protocol {observation.ProtocolVersion} · contract minor {observation.ContractMinor} · "
            + $"features 0x{observation.Features:X} · 스폰 {spawns}/{observation.NpcCount} · "
            + $"존 {zoneStates.Count}/{observation.ZoneCount} · 프레임 최대 {worst}건");

        return violations.Count == 0
            ? CheckResult.Pass(Id, Title, detail)
            : CheckResult.Fail(Id, Title, detail, violations);
    }
}
