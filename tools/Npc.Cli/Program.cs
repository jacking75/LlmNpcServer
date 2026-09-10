using System.Collections.Immutable;
using Npc.MasterData;

namespace Npc.Cli;

/// <summary>
/// <c>npc</c> CLI (F-01).
///
/// <b>여기에 로직이 없다.</b> 인자를 읽고 코어를 부르고 출력한다 — 판단은
/// <c>Npc.Narrative</c>·<c>Npc.MasterData.Authoring</c>·검증기에 있고, MCP 서버(E-03)와
/// Studio(F-02)가 같은 함수를 부른다. CLI 에만 있는 규칙을 만들면 세 껍질이 다르게 답한다.
///
/// <para>
/// <b>시각도 난수도 쓰지 않는다.</b> 같은 저장소 상태면 같은 출력이라 골든으로 회귀를 잡을 수 있다.
/// </para>
/// </summary>
public static class Program
{
    /// <summary>성공.</summary>
    public const int Ok = 0;

    /// <summary>검사 실패 (위반이 있다·낡았다).</summary>
    public const int Failed = 1;

    /// <summary>인자 오류.</summary>
    public const int BadUsage = 2;

    /// <summary>사용법.</summary>
    public const string Usage = """
        npc — NPC 서버 마스터데이터·플랜 도구

        사용법: npc <명령> [인자] [옵션]

        검증·설명
          validate                       V1~V13 + 로더 + 파생물 신선도
          explain archetype|action|poi|item|flag|interrupt <id>
          card archetype <id> | npc <첨자> | roster <id>
          timeline archetype <id>        24시간 띠 (근무·폴백 스텝)
          hints [--out <path>]           검증 오류 사전. --out 은 markdown 을 생성한다

        편집
          next-code items|pois|actions|archetypes|zones|flags
          scaffold archetype <id> --from <id> --weight <w> [--apply]
          diff [--base <rev>]            사람 말 diff + 파급
          regen [--check]                낡은 파생물만 재생성

        플랜
          plan validate <파일> [--bucket <키>]
          plan explain <파일> | narrate <파일>
          buckets [--archetype <id>] [--state missing|fallback|pinned|generated]
          pin <버킷>

        옵션
          --masterdata <dir>   기본 ./masterdata
          --planstore <dir>    기본 ./planstore
          --json               기계가 읽는 출력 (지원하는 명령만)
          --apply              파일을 실제로 고친다 (기본은 dry-run)
          --population <n>     인구표 기준 NPC 수 (기본 5000)

        미구현 — 의존 태스크를 기다린다
          plan repair (C-05) · review (F-06) · serve (B-08) · plan dryrun 은 validate 에 포함
        """;

    /// <summary>진입점.</summary>
    public static int Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return Run(args, Console.Out, Console.Error);
    }

    /// <summary>테스트가 부르는 실체. 표준출력·표준오류를 주입받는다.</summary>
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            output.WriteLine(Usage);
            return args.Length == 0 ? BadUsage : Ok;
        }

        string command = args[0];

        if (!TryParse(args[1..], output, out CliContext? context))
        {
            return BadUsage;
        }

        try
        {
            return Dispatch(command, context!, output, error);
        }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException
            or DirectoryNotFoundException or InvalidOperationException or ArgumentException)
        {
            // 스택 추적을 내지 않는다 — 터미널에서 읽는 사람에게 도움이 안 되고,
            // 정말 필요하면 라이브러리를 직접 부르는 테스트가 있다.
            error.WriteLine(ex.Message);
            return Failed;
        }
    }

    private static int Dispatch(string command, CliContext ctx, TextWriter output, TextWriter error) =>
        command switch
        {
            "validate" => ValidateCommand.Run(ctx),
            "explain" => ExplainCommand.Run(ctx),
            "card" => CardCommand.Run(ctx),
            "timeline" => TimelineCommand.Run(ctx),
            "hints" => HintsCommand.Run(ctx),
            "next-code" => NextCodeCommand.Run(ctx),
            "scaffold" => ScaffoldCommand.Run(ctx),
            "diff" => DiffCommand.Run(ctx),
            "regen" => RegenCommand.Run(ctx),
            "plan" => PlanCommand.Run(ctx),
            "buckets" => BucketsCommand.Run(ctx),
            "pin" => PinCommand.Run(ctx),

            // 의존 태스크가 없다. "지원하지 않는다" 와 "아직 없다" 는 다른 말이라 구별해 낸다.
            "repair" => Pending(error, command, "C-05 검증 실패율 개선"),
            "review" => Pending(error, command, "F-06 검수 워크플로 v2"),
            "serve" => Pending(error, command, "B-08 읽기 전용 질의 API"),

            _ => Unknown(command, output, error),
        };

    private static int Pending(TextWriter error, string command, string task)
    {
        error.WriteLine($"`npc {command}` 는 아직 없다 — {task} 를 기다린다.");
        return BadUsage;
    }

    private static int Unknown(string command, TextWriter output, TextWriter error)
    {
        error.WriteLine($"모르는 명령: {command}");
        output.WriteLine(Usage);
        return BadUsage;
    }

    private static bool TryParse(string[] args, TextWriter output, out CliContext? context)
    {
        context = null;

        string masterData = "./masterdata";
        string planStore = "./planstore";
        bool json = false;
        bool apply = false;
        int population = 5_000;
        var positional = ImmutableArray.CreateBuilder<string>(args.Length);

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--masterdata" when i + 1 < args.Length:
                    masterData = args[++i];
                    break;

                case "--planstore" when i + 1 < args.Length:
                    planStore = args[++i];
                    break;

                case "--json":
                    json = true;
                    break;

                case "--apply":
                    apply = true;
                    break;

                case "--population" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out population) || population <= 0)
                    {
                        output.WriteLine($"--population 값이 잘못됐다: {args[i]}");
                        return false;
                    }

                    break;

                case "--help" or "-h":
                    output.WriteLine(Usage);
                    return false;

                default:
                    // 명령별 플래그(--from · --weight · --bucket …)는 각 명령이 읽는다.
                    positional.Add(args[i]);
                    break;
            }
        }

        context = new CliContext
        {
            MasterData = masterData,
            PlanStore = planStore,
            Json = json,
            Apply = apply,
            Population = population,
            Out = output,
            Args = positional.ToImmutable(),
        };

        return true;
    }

    /// <summary>
    /// 명령별 플래그 값 읽기. <c>--from shepherd</c> 처럼 위치 인자 목록에 남아 있는 것을 찾는다.
    /// </summary>
    internal static string? Flag(CliContext ctx, string name)
    {
        for (int i = 0; i < ctx.Args.Length - 1; i++)
        {
            if (string.Equals(ctx.Args[i], name, StringComparison.Ordinal))
            {
                return ctx.Args[i + 1];
            }
        }

        return null;
    }

    /// <summary>값 없는 플래그가 있는가.</summary>
    internal static bool HasFlag(CliContext ctx, string name) =>
        ctx.Args.Contains(name, StringComparer.Ordinal);

    /// <summary>플래그와 그 값을 뺀 순수 위치 인자.</summary>
    internal static ImmutableArray<string> Positional(CliContext ctx, params string[] valueFlags)
    {
        var kept = ImmutableArray.CreateBuilder<string>(ctx.Args.Length);

        for (int i = 0; i < ctx.Args.Length; i++)
        {
            if (valueFlags.Contains(ctx.Args[i], StringComparer.Ordinal))
            {
                i++;   // 값도 건너뛴다
                continue;
            }

            if (ctx.Args[i].StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            kept.Add(ctx.Args[i]);
        }

        return kept.ToImmutable();
    }

    /// <summary>아키타입을 찾는다. 없으면 무엇이 있는지 알려 준다 — 오타가 가장 흔한 실패다.</summary>
    internal static ArchetypeDef Archetype(CliContext ctx, string id)
    {
        if (ctx.Data.Archetypes.TryGet(id, out ArchetypeDef def))
        {
            return def;
        }

        string near = string.Join(
            " · ",
            ctx.Data.Archetypes.Archetypes
                .Where(a => a.Id.StartsWith(id[..Math.Min(3, id.Length)], StringComparison.Ordinal))
                .Select(a => a.Id));

        throw new ArgumentException(
            $"아키타입 '{id}' 이 없다." + (near.Length == 0 ? string.Empty : $" 비슷한 것: {near}"));
    }
}
