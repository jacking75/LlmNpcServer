using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Npc.MasterData;
using Npc.MasterData.Authoring;
using Npc.Narrative;

namespace Npc.Cli;

/// <summary>
/// <c>npc next-code &lt;파일&gt;</c> (F-01 · F-04).
/// <b>다음 번호가 무엇인지만 말한다.</b> 재배치는 이 도구로 불가능하다.
/// </summary>
public static class NextCodeCommand
{
    /// <summary>별칭 → 파일 이름. 터미널에서 <c>flags</c> 라고 치는 것이 자연스럽다.</summary>
    public static ImmutableDictionary<string, string> Aliases { get; } =
        ImmutableDictionary<string, string>.Empty
            .Add("items", "items.json")
            .Add("actions", "actions.json")
            .Add("archetypes", "archetypes.json")
            .Add("zones", "zones.json")
            .Add("pois", "pois.json")
            .Add("flags", "world_flags.json");

    /// <summary>돌린다.</summary>
    public static int Run(CliContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        ImmutableArray<string> args = Program.Positional(ctx);

        if (args.Length == 0)
        {
            ctx.Out.WriteLine("사용법: npc next-code " + string.Join("|", Aliases.Keys.Order(StringComparer.Ordinal)));
            return Program.BadUsage;
        }

        if (!Aliases.TryGetValue(args[0], out string? file))
        {
            throw new ArgumentException(
                $"'{args[0]}' 에는 번호 체계가 없다. "
                + string.Join("|", Aliases.Keys.Order(StringComparer.Ordinal)) + " 중 하나다.");
        }

        int next = CodeAllocator.Next(ctx.MasterData, file);
        ImmutableArray<int> reserved = CodeAllocator.ReservedBits(ctx.MasterData, file);
        ImmutableArray<int> gaps = CodeAllocator.Gaps(ctx.MasterData, file);

        if (ctx.Json)
        {
            ctx.Out.WriteLine(
                $"{{ \"file\": \"{file}\", \"next\": {next}, \"reserved\": [{string.Join(", ", reserved)}], "
                + $"\"gaps\": [{string.Join(", ", gaps)}] }}");

            return Program.Ok;
        }

        ctx.Out.WriteLine($"{file}  다음 번호 {next}");

        if (!reserved.IsEmpty)
        {
            // 예약 구간은 이어져 있지 않다 (world_flags 는 22~23 과 44~63 이다).
            // "22~63" 이라고만 쓰면 그 사이가 다 비어 있는 것처럼 읽힌다.
            int free = reserved.Count(b => b >= next);

            ctx.Out.WriteLine(
                $"  예약 {reserved.Length}칸 중 {free}칸 남음 — {string.Join(", ", reserved)}");
        }

        if (!gaps.IsEmpty)
        {
            ctx.Out.WriteLine(
                $"  경고 빠진 번호 {string.Join(", ", gaps)} — 예약 구간이 아닌데 비어 있다");
        }

        return Program.Ok;
    }
}

/// <summary>
/// <c>npc scaffold archetype &lt;id&gt; --from &lt;id&gt; --weight &lt;w&gt; [--apply]</c> (F-01 · F-04).
///
/// <b>기본은 dry-run 이다.</b> 마스터데이터를 고치는 것은 파생물 재생성과 프리베이크를 부르는
/// 결정이라, 사람이 파급표를 보고 확인한 뒤에 <c>--apply</c> 를 붙인다.
///
/// <para>
/// 7장 실습(<c>samples/ch07_beekeeper/apply.ps1</c>)의 아키타입 부분이 이 한 줄이 된다.
/// </para>
/// </summary>
public static class ScaffoldCommand
{
    /// <summary>돌린다.</summary>
    public static int Run(CliContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        ImmutableArray<string> args = Program.Positional(ctx, "--from", "--weight", "--workplace");

        if (args.Length < 2 || args[0] != "archetype")
        {
            ctx.Out.WriteLine(
                "사용법: npc scaffold archetype <id> --from <베낄 id> --weight <가중치> [--apply]");
            ctx.Out.WriteLine("  action·poi·item·interrupt·fallback 은 아직 없다 — 아키타입만 있다.");
            return Program.BadUsage;
        }

        string id = args[1];

        if (Program.Flag(ctx, "--from") is not { } fromId)
        {
            throw new ArgumentException("--from 이 필요하다. 어느 아키타입을 베낄 것인가.");
        }

        if (Program.Flag(ctx, "--weight") is not { } weightText
            || !double.TryParse(weightText, CultureInfo.InvariantCulture, out double weight))
        {
            throw new ArgumentException("--weight 가 필요하다. 예: --weight 0.004");
        }

        if (ctx.Data.Archetypes.TryGet(id, out _))
        {
            throw new ArgumentException($"'{id}' 이 이미 있다. 가중치만 바꾸려면 --weight 를 직접 편집한다.");
        }

        ArchetypeDef from = Program.Archetype(ctx, fromId);
        int code = CodeAllocator.Next(ctx.MasterData, "archetypes.json");

        ctx.Out.WriteLine($"# scaffold archetype {id} (code {code})");
        ctx.Out.WriteLine();
        ctx.Out.WriteLine($"베낄 것  `{from.Id}` — 일터 `{from.WorkplacePoiType ?? "없음"}` · 액션 {from.AllowedActions.Length}종");
        ctx.Out.WriteLine();

        // ── 가중치 재배분 3안 ──────────────────────────────────
        string? workplace = Program.Flag(ctx, "--workplace") ?? from.WorkplacePoiType;

        ImmutableArray<RebalanceProposal> proposals =
            WeightRebalancer.Propose(ctx.Data, id, weight, ctx.Population, workplace);

        ctx.Out.WriteLine("## 가중치 재배분 — 하나를 골라 적용한다");
        ctx.Out.WriteLine();

        foreach (RebalanceProposal proposal in proposals)
        {
            ctx.Out.WriteLine($"### {proposal.Strategy} (합 {proposal.Sum:F4} {(proposal.IsValid ? "✓" : "✗")})");
            ctx.Out.WriteLine();

            var sb = new StringBuilder(512);

            Md.TableHead(sb, "아키타입", "가중치", "인구");

            foreach (WeightChange change in proposal.Changes.Where(c => c.From != c.To))
            {
                Md.Row(
                    sb,
                    Md.Code(change.Archetype),
                    $"{change.From:F4} → {change.To:F4}",
                    $"{Md.N(change.PopulationFrom)} → {Md.N(change.PopulationTo)} ({change.Delta:+0;-0;0})");
            }

            ctx.Out.Write(sb.ToString());
            ctx.Out.WriteLine();
        }

        // ── 파급 ─────────────────────────────────────────────
        ctx.Out.WriteLine("## 파급");
        ctx.Out.WriteLine();
        ctx.Out.WriteLine("```");
        ctx.Out.WriteLine(ImpactAnalyzer.Describe(
            ImpactAnalyzer.Of(ctx.MasterData, ["archetypes.json", "fallback_plans.json"])));
        ctx.Out.WriteLine("```");
        ctx.Out.WriteLine();

        // ── 해야 할 일 ────────────────────────────────────────
        ctx.Out.WriteLine("## 남은 일 — 도구가 하지 않는다");
        ctx.Out.WriteLine();
        ctx.Out.WriteLine($"1. `desc` 를 쓴다. **프롬프트에 실리는 문장**이라 사람이 쓴다");
        ctx.Out.WriteLine($"2. `allowed_actions` 를 정한다 (`{from.Id}` 것을 베꼈다)");
        ctx.Out.WriteLine($"3. `workplace_poi_type` 의 POI 를 만든다 — 정원이 인구 {Population(weight, ctx)} 를 넘어야 한다 (V10)");
        ctx.Out.WriteLine($"4. `fb_{id}` 폴백 플랜을 만든다 (V7·V8)");
        ctx.Out.WriteLine("5. `npc regen` 으로 파생물을 다시 만든다");
        ctx.Out.WriteLine("6. 프리베이크를 다시 돌린다 — 프리픽스가 바뀌어 플랜 스토어가 전량 무효다");
        ctx.Out.WriteLine();

        if (!ctx.Apply)
        {
            ctx.Out.WriteLine("dry-run 이다. 실제로 고치려면 `--apply` 를 붙인다.");
            return Program.Ok;
        }

        return Apply(ctx, id, code, from, proposals[0]);
    }

    private static int Population(double weight, CliContext ctx) =>
        (int)Math.Round(weight * ctx.Population, MidpointRounding.AwayFromZero);

    /// <summary>
    /// 첫 안(<see cref="RebalanceStrategy.Largest"/>)으로 적용한다.
    ///
    /// <b>골라 주는 것이 아니라 기본값이다.</b> 다른 안을 쓰려면 dry-run 출력의 표를 보고
    /// 손으로 고친다 — 인구 분포는 세계관 결정이라 도구가 대신 정하면 근거가 사라진다.
    /// </summary>
    private static int Apply(
        CliContext ctx, string id, int code, ArchetypeDef from, RebalanceProposal proposal)
    {
        string path = Path.Combine(ctx.MasterData, "archetypes.json");
        string json = File.ReadAllText(path);

        // 1) 재배분 — 기존 줄의 가중치를 갈아 끼운다.
        foreach (WeightChange change in proposal.Changes)
        {
            if (change.From == change.To || !ctx.Data.Archetypes.TryGet(change.Archetype, out _))
            {
                continue;
            }

            json = JsonSurgeon.SetInArrayItem(
                json,
                "archetypes",
                "id",
                change.Archetype,
                "population_weight",
                change.To.ToString("0.####", CultureInfo.InvariantCulture));
        }

        // 2) 새 줄 — 베낀 아키타입의 정의를 그대로 두고 id·code·가중치만 바꾼다.
        json = JsonSurgeon.AppendToArray(json, "archetypes", Draft(ctx, id, code, from, proposal));

        File.WriteAllText(path, json, new UTF8Encoding(false));

        ctx.Out.WriteLine($"{path} 를 고쳤다 ({proposal.Strategy} 안).");
        ctx.Out.WriteLine("`npc validate` 로 확인한다 — 폴백 플랜(V7)과 POI 정원(V10)이 아직 없다.");

        return Program.Ok;
    }

    /// <summary>
    /// 새 아키타입 초안. 베낄 것의 필드를 그대로 쓰고 <c>desc</c> 만 비워 둔다 —
    /// <b>프롬프트에 실리는 문장을 도구가 지어내면 그 세계관은 아무도 쓴 적이 없는 것이 된다.</b>
    /// </summary>
    private static string Draft(
        CliContext ctx, string id, int code, ArchetypeDef from, RebalanceProposal proposal)
    {
        string source = File.ReadAllText(Path.Combine(ctx.MasterData, "archetypes.json"));
        (int start, int end) = JsonSurgeon.ItemRange(source, "archetypes", "id", from.Id);

        // 베낀 조각은 원문의 들여쓰기를 이미 달고 있다. 그대로 넣으면 AppendToArray 가
        // 한 번 더 들여써서 두 겹이 된다 — 먼저 벗긴다.
        string draft = Dedent(source[start..end]);

        draft = JsonSurgeon.SetTopLevel(draft, "id", Text(id));
        draft = JsonSurgeon.SetTopLevel(draft, "code", code.ToString(CultureInfo.InvariantCulture));
        draft = JsonSurgeon.SetTopLevel(draft, "name_key", Text("npc." + id));
        draft = JsonSurgeon.SetTopLevel(draft, "fallback_plan", Text("fb_" + id));
        draft = JsonSurgeon.SetTopLevel(
            draft, "desc", Text($"TODO: {id} 의 설명을 쓴다. 프롬프트에 실린다."));

        WeightChange mine = proposal.Changes.First(c => c.Archetype == id);

        draft = JsonSurgeon.SetTopLevel(
            draft, "population_weight", mine.To.ToString("0.####", CultureInfo.InvariantCulture));

        return draft;
    }

    /// <summary>
    /// JSON 문자열 리터럴. <b>한글을 escape 하지 않는다</b> — 마스터데이터는 사람이 읽고
    /// 고치는 파일이고, 도구가 넣은 줄만 표기가 다르면 그 파일이 두 가지 표기를 갖게 된다.
    /// </summary>
    private static string Text(string value) =>
        JsonSerializer.Serialize(value, JsonSurgeonText.Options);

    /// <summary>2번째 줄부터의 공통 들여쓰기를 벗긴다.</summary>
    private static string Dedent(string json)
    {
        string[] lines = json.ReplaceLineEndings("\n").Split('\n');

        if (lines.Length == 1)
        {
            return json;
        }

        int common = int.MaxValue;

        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0)
            {
                continue;
            }

            common = Math.Min(common, lines[i].Length - lines[i].TrimStart().Length);
        }

        if (common is 0 or int.MaxValue)
        {
            return json;
        }

        var sb = new StringBuilder(json.Length);

        sb.Append(lines[0]);

        for (int i = 1; i < lines.Length; i++)
        {
            sb.Append('\n').Append(lines[i].Length >= common ? lines[i][common..] : lines[i].TrimStart());
        }

        return sb.ToString();
    }
}

/// <summary>
/// <c>npc diff [--base &lt;rev&gt;]</c> (F-01 · F-04).
/// git 이 낸 파일 목록을 <b>사람 말 파급</b>으로 바꾼다.
/// </summary>
public static class DiffCommand
{
    /// <summary>돌린다.</summary>
    public static int Run(CliContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        string baseRev = Program.Flag(ctx, "--base") ?? "HEAD";
        ImmutableArray<string> changed = ChangedFiles(ctx, baseRev);

        ctx.Out.WriteLine($"masterdata 변경 (base: {baseRev})");
        ctx.Out.WriteLine();

        if (changed.IsEmpty)
        {
            ctx.Out.WriteLine("  변경 없음");
            return Program.Ok;
        }

        foreach (string file in changed)
        {
            ctx.Out.WriteLine($"  {file}");
        }

        ctx.Out.WriteLine();
        ctx.Out.WriteLine(ImpactAnalyzer.Describe(ImpactAnalyzer.Of(ctx.MasterData, changed)));

        return Program.Ok;
    }

    /// <summary>
    /// <c>git diff --name-only</c> 로 바뀐 마스터데이터 파일을 얻는다.
    /// <b>git 이 없으면 빈 목록이 아니라 예외다</b> — "변경 없음" 으로 보이면 판정이 거짓이 된다.
    /// </summary>
    private static ImmutableArray<string> ChangedFiles(CliContext ctx, string baseRev)
    {
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetFullPath(ctx.MasterData),
        };

        start.ArgumentList.Add("diff");
        start.ArgumentList.Add("--name-only");
        start.ArgumentList.Add(baseRev);
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(".");

        using Process? process = Process.Start(start)
            ?? throw new InvalidOperationException("git 을 실행할 수 없다.");

        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();

        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git diff 가 실패했다 (exit {process.ExitCode}): {error.Trim()}");
        }

        return
        [
            .. output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Path.GetFileName)
                .Where(f => !string.IsNullOrEmpty(f))
                .Select(f => f!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f, StringComparer.Ordinal),
        ];
    }
}

/// <summary>
/// <c>npc regen [--check]</c> (F-01 · F-04).
/// <b>낡은 것만 다시 만든다.</b> 전부 다시 만들면 거리표 계산이 매번 붙는다.
/// </summary>
public static class RegenCommand
{
    /// <summary>돌린다.</summary>
    public static int Run(CliContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        ImmutableArray<DerivedStatus> stale = DerivedArtifacts.Stale(ctx.MasterData);

        if (stale.IsEmpty)
        {
            ctx.Out.WriteLine("파생물이 전부 최신이다.");
            return Program.Ok;
        }

        foreach (DerivedStatus status in stale)
        {
            ctx.Out.WriteLine($"낡음 {status}");
        }

        if (Program.HasFlag(ctx, "--check"))
        {
            // CI 가 쓰는 모드다. 종료 코드로 판정한다.
            ctx.Out.WriteLine($"{stale.Length}건 낡음. `npc regen` 으로 다시 만든다.");
            return Program.Failed;
        }

        ctx.Out.WriteLine();
        ctx.Out.WriteLine("다시 만들려면 저장소 루트에서 순서대로 돌린다:");

        // 도구를 여기서 대신 실행하지 않는다. 생성기는 저장소 루트를 기준으로 돌고
        // 몇 분이 걸릴 수도 있어, 무엇이 실행되는지 사람이 보고 시작하는 편이 낫다.
        foreach (DerivedStatus status in stale.OrderBy(s => s.Generator, StringComparer.Ordinal))
        {
            ctx.Out.WriteLine($"  dotnet run {status.Generator}");
        }

        return Program.Ok;
    }
}
