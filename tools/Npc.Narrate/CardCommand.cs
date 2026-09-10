using Npc.Contracts;
using Npc.MasterData;
using Npc.Narrative;

namespace Npc.Narrate;

/// <summary>
/// <c>Npc.Narrate card …</c> · <c>explain interrupts</c> (F-03).
///
/// <b>F-01 의 <c>npc</c> CLI 가 올 자리다.</b> 그때까지 <c>Npc.Narrative</c> 를 손으로 쓸 방법이
/// 있어야 한다 — 라이브러리만 만들고 껍질을 미루면 검수자는 여전히 JSON 을 열어 본다.
/// 같은 함수를 부르므로 CLI 가 오면 출력이 바뀌지 않는다.
/// </summary>
public static class CardCommand
{
    /// <summary>서브커맨드 이름.</summary>
    public const string Name = "card";

    /// <summary><c>explain</c> 서브커맨드 이름.</summary>
    public const string ExplainName = "explain";

    /// <summary>사용법.</summary>
    public const string Usage = """
        사용법:
          Npc.Narrate card archetype <id> [--masterdata <dir>] [--population <n>]
          Npc.Narrate card npc <index>   [--masterdata <dir>]
          Npc.Narrate card roster <id>   [--masterdata <dir>]
          Npc.Narrate explain interrupts [--masterdata <dir>]
          Npc.Narrate explain fallback <archetype-id> [--masterdata <dir>]

        시각도 난수도 섞지 않는다 — 같은 masterdata 면 바이트 동일이다.
        """;

    /// <summary>돌린다. 0 = 성공, 2 = 인자 오류.</summary>
    public static int Run(string subcommand, string[] args, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        string masterData = "./masterdata";
        int population = ArchetypeCard.PopulationBase;
        var positional = new List<string>(2);

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--masterdata" when i + 1 < args.Length:
                    masterData = args[++i];
                    break;

                case "--population" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out population) || population <= 0)
                    {
                        output.WriteLine($"--population 값이 잘못됐다: {args[i]}");
                        return 2;
                    }

                    break;

                case "--help" or "-h":
                    output.WriteLine(Usage);
                    return 0;

                default:
                    if (args[i].StartsWith('-'))
                    {
                        output.WriteLine($"모르는 인자: {args[i]}");
                        output.WriteLine(Usage);
                        return 2;
                    }

                    positional.Add(args[i]);
                    break;
            }
        }

        if (positional.Count == 0)
        {
            output.WriteLine(Usage);
            return 2;
        }

        MasterDataSet data = MasterDataLoader.Load(masterData);

        return subcommand == ExplainName
            ? Explain(data, masterData, positional, output)
            : Card(data, masterData, positional, population, output);
    }

    private static int Card(
        MasterDataSet data, string masterData, List<string> positional, int population, TextWriter output)
    {
        switch (positional[0])
        {
            case "archetype" when positional.Count > 1:
                if (!data.Archetypes.TryGet(positional[1], out ArchetypeDef def))
                {
                    output.WriteLine($"아키타입 '{positional[1]}' 이 없다.");
                    return 2;
                }

                output.Write(ArchetypeCard.Render(data, def.Code, population));
                return 0;

            case "npc" when positional.Count > 1:
                if (!int.TryParse(positional[1], out int index) || index < 0)
                {
                    output.WriteLine($"NPC 첨자가 잘못됐다: {positional[1]}");
                    return 2;
                }

                output.Write(InstanceCard.Render(data, Instances(data, masterData), index));
                return 0;

            case "roster" when positional.Count > 1:
                if (!data.Archetypes.TryGet(positional[1], out ArchetypeDef roster))
                {
                    output.WriteLine($"아키타입 '{positional[1]}' 이 없다.");
                    return 2;
                }

                output.Write(InstanceCard.RenderRoster(data, Instances(data, masterData), roster.Code));
                return 0;

            default:
                output.WriteLine(Usage);
                return 2;
        }
    }

    private static int Explain(
        MasterDataSet data, string masterData, List<string> positional, TextWriter output)
    {
        _ = masterData;

        switch (positional[0])
        {
            case "interrupts":
                output.Write(InterruptExplain.Render(data));
                return 0;

            case "fallback" when positional.Count > 1:
                if (!data.Archetypes.TryGet(positional[1], out ArchetypeDef def))
                {
                    output.WriteLine($"아키타입 '{positional[1]}' 이 없다.");
                    return 2;
                }

                if (data.Fallbacks?.For(def.Code) is not { } plan)
                {
                    output.WriteLine($"'{def.FallbackPlanId}' 을 찾지 못했다 — fallback_plans.json 을 확인한다.");
                    return 2;
                }

                output.Write(PlanExplain.Render(data, plan));
                return 0;

            default:
                output.WriteLine(Usage);
                return 2;
        }
    }

    private static NpcInstanceTable Instances(MasterDataSet data, string masterData) =>
        NpcInstanceTable.Load(Path.Combine(masterData, "npc_instances.json"), data);
}
