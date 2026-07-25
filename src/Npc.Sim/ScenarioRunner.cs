using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Npc.Contracts;
using Npc.Core;
using Npc.MasterData;

namespace Npc.Sim;

/// <summary>시나리오 한 줄. docs/02 §5.</summary>
/// <param name="AtTick">이 틱에 주입한다.</param>
/// <param name="Kind">주입할 이벤트. KillSwitch 면 null.</param>
/// <param name="Zone">대상 존.</param>
/// <param name="Code">RegionState / Climate / TimeOfDay 의 ordinal.</param>
/// <param name="KillSwitchTarget">"T1" / "T2". 이벤트가 아니라 티어 차단 신호다.</param>
public sealed record ScenarioStep(
    long AtTick,
    GameEventKind? Kind,
    ZoneId Zone,
    byte Code,
    string? KillSwitchTarget);

/// <summary>
/// 시나리오 스크립트 주입기. docs/02 §5.
///
/// jsonl 의 <c>at_tick</c> 에 이벤트를 밀어넣는다. 공성·재해·킬스위치가 이걸로 온다.
/// <b>±0틱</b>에 정확히 발생해야 한다 — 시나리오 A/B/C 의 재현성이 여기 달려 있다.
///
/// KillSwitch 는 이벤트가 아니다. LLM 티어를 끊는 신호이고, P1 에는 LLM 이 없으므로
/// 발생 사실만 기록한다. P4·P5 의 시나리오 C 가 이 목록을 본다.
/// </summary>
public sealed class ScenarioRunner
{
    private readonly ImmutableArray<ScenarioStep> _steps;
    private readonly List<string> _killSwitches = [];
    private int _next;

    private ScenarioRunner(ImmutableArray<ScenarioStep> steps) => _steps = steps;

    /// <summary>틱 오름차순으로 정렬된 스텝.</summary>
    public ImmutableArray<ScenarioStep> Steps => _steps;

    /// <summary>주입한 이벤트 수.</summary>
    public int Injected { get; private set; }

    /// <summary>발생한 킬스위치 대상. 발생 순서대로.</summary>
    public IReadOnlyList<string> KillSwitchesFired => _killSwitches;

    /// <summary>남은 스텝이 있는가.</summary>
    public bool HasMore => _next < _steps.Length;

    /// <summary>빈 시나리오.</summary>
    public static ScenarioRunner Empty { get; } = new([]);

    /// <summary>jsonl 파일에서 읽는다.</summary>
    public static ScenarioRunner Load(string path, MasterDataSet data) =>
        Parse(File.ReadLines(path), data);

    /// <summary>jsonl 줄들에서 읽는다.</summary>
    public static ScenarioRunner Parse(IEnumerable<string> lines, MasterDataSet data)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(data);

        var steps = ImmutableArray.CreateBuilder<ScenarioStep>();

        foreach (string line in lines)
        {
            string text = line.Trim();

            if (text.Length == 0 || text.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;

            if (!root.TryGetProperty("at_tick", out JsonElement atTick))
            {
                continue;   // _comment 줄
            }

            steps.Add(ParseStep(root, atTick.GetInt64(), data));
        }

        // 틱 오름차순, 같은 틱이면 파일 순서. 순서가 결정론이어야 리플레이가 일치한다.
        ImmutableArray<ScenarioStep> ordered = [.. steps.ToImmutable()
            .Select((s, i) => (Step: s, Index: i))
            .OrderBy(x => x.Step.AtTick)
            .ThenBy(x => x.Index)
            .Select(x => x.Step)];

        return new ScenarioRunner(ordered);
    }

    /// <summary>이 틱에 예약된 것을 전부 주입한다.</summary>
    public void Tick(Tick now, SimWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);

        while (_next < _steps.Length && _steps[_next].AtTick <= now.Value)
        {
            ScenarioStep step = _steps[_next++];

            if (step.KillSwitchTarget is { } target)
            {
                _killSwitches.Add(target);
                continue;
            }

            if (step.Kind is not { } kind)
            {
                continue;
            }

            world.Emit(new GameEvent
            {
                Kind = kind,
                Sequence = 0,
                OccurredAt = now,
                Zone = step.Zone,
                Code = step.Code,
            });

            Injected++;
        }
    }

    private static ScenarioStep ParseStep(JsonElement root, long atTick, MasterDataSet data)
    {
        string name = root.GetProperty("event").GetString()!;

        if (string.Equals(name, "KillSwitch", StringComparison.Ordinal))
        {
            return new ScenarioStep(
                atTick, null, default, 0, root.GetProperty("target").GetString());
        }

        if (!Enum.TryParse(name, out GameEventKind kind))
        {
            throw new InvalidDataException($"시나리오: 모르는 이벤트 '{name}'.");
        }

        ZoneId zone = default;
        if (root.TryGetProperty("zone", out JsonElement zoneElement))
        {
            string zoneId = zoneElement.GetString()!;

            if (!data.Zones.TryGet(zoneId, out ZoneDef def))
            {
                throw new InvalidDataException($"시나리오: zones.json 에 없는 존 '{zoneId}'.");
            }

            zone = def.Code;
        }

        byte code = 0;
        if (root.TryGetProperty("code", out JsonElement codeElement))
        {
            code = ParseCode(kind, codeElement);
        }

        return new ScenarioStep(atTick, kind, zone, code, null);
    }

    /// <summary>code 는 이벤트 종류에 따라 다른 열거형이다 (docs/02 §3.3).</summary>
    private static byte ParseCode(GameEventKind kind, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.GetByte();
        }

        string text = element.GetString()!;

        return kind switch
        {
            GameEventKind.ZoneStateChanged when Enum.TryParse(text, out RegionState state) => (byte)state,
            GameEventKind.WeatherChanged when Enum.TryParse(text, out Climate climate) => (byte)climate,
            GameEventKind.GameTimeChanged when Enum.TryParse(text, out TimeOfDay time) => (byte)time,
            _ => byte.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte raw)
                ? raw
                : throw new InvalidDataException($"시나리오: {kind} 의 code '{text}' 를 모른다."),
        };
    }
}
