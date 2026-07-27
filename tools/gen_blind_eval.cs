// 블라인드 A/B 평가 자료 생성기. docs/15 §6 · T5-13.
//
// 실행:
//   dotnet run tools/gen_blind_eval.cs -- --trace-a <A.jsonl> --trace-b <B.jsonl> --population 200
//
// 앞 단계 (두 기록을 만드는 법) — 두 실행은 --planstore 만 다르다.
// 같은 NPC·같은 세계·같은 시각이라 짝 매칭이 정의상 완벽하다.
//
//   1) A군 플랜 생성 — 이 도구가 --plan-only 로 필요한 --only 글롭을 찍어 준다
//        dotnet run --project tools/Npc.Prebake -- --planstore artifacts/blind_eval/planstore_a \
//          --only "blacksmith@*.Peace.Fair" --only "farmer@*.Peace.Fair" ...
//   2) A군 기록
//        dotnet run -c Release --project src/Npc.Host -- --link record \
//          --trace artifacts/blind_eval/run_a.jsonl --planstore artifacts/blind_eval/planstore_a \
//          --npcs 200 --time-scale 600 --days 1 --max-speed --no-dashboard
//   3) B군 기록 — 플랜 스토어를 주지 않으면 아키타입 폴백 40개로 돈다 (= 사람이 짠 플랜)
//        dotnet run -c Release --project src/Npc.Host -- --link record \
//          --trace artifacts/blind_eval/run_b.jsonl --planstore artifacts/blind_eval/empty \
//          --npcs 200 --time-scale 600 --days 1 --max-speed --no-dashboard
//
// 산출:
//   artifacts/blind_eval/case_01.md .. case_40.md   제시 순서대로. 어느 군인지 드러나지 않는다
//   docs/measurements/blind_eval_key.md             정답 키 + 배치 시드
//
// artifacts/ 는 .gitignore 대상이므로 정답 키와 시드만 docs/measurements/ 에 남는다 (T5-13).
// 무작위화도 시드 고정이다 — 참가자를 추가하거나 다시 돌릴 때 같은 배치를 재현해야 한다.
#:project ../src/Npc.MasterData/Npc.MasterData.csproj
#:project ../src/Npc.Gateway/Npc.Gateway.csproj
#:project Npc.Narrate/Npc.Narrate.csproj

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Npc.Core;
using Npc.Core.Plan;
using Npc.MasterData;
using Npc.Narrate;

// ── 인자 ──────────────────────────────────────────────────────────────
string? traceA = null;
string? traceB = null;
string masterDataDir = "./masterdata";
string outDir = "./artifacts/blind_eval";
string keyPath = "./docs/measurements/blind_eval_key.md";
int population = 200;
int pairs = 20;
int seed = 20260727;
int timeScale = 600;
bool planOnly = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--trace-a" when i + 1 < args.Length: traceA = args[++i]; break;
        case "--trace-b" when i + 1 < args.Length: traceB = args[++i]; break;
        case "--masterdata" when i + 1 < args.Length: masterDataDir = args[++i]; break;
        case "--out" when i + 1 < args.Length: outDir = args[++i]; break;
        case "--key" when i + 1 < args.Length: keyPath = args[++i]; break;
        case "--population" when i + 1 < args.Length: population = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--pairs" when i + 1 < args.Length: pairs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--seed" when i + 1 < args.Length: seed = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--time-scale" when i + 1 < args.Length: timeScale = int.Parse(args[++i], CultureInfo.InvariantCulture); break;

        // 앞 단계에 필요한 --only 글롭만 찍고 끝낸다. 기록이 아직 없을 때 쓴다.
        case "--plan-only": planOnly = true; break;

        default: throw new ArgumentException($"모르는 인자다: {args[i]}");
    }
}

MasterDataSet data = MasterDataLoader.Load(masterDataDir);
NpcInstanceTable instances = NpcInstanceTable.Load(
    Path.Combine(masterDataDir, "npc_instances.json"), data);

// ── 1. 표본 20 — 아키타입이 서로 다르게, 결정론적으로 ─────────────────
//
// 호스트가 --npcs N 으로 뽑는 것과 같은 균등 간격 식을 쓴다. 앞에서부터 자르면
// 아키타입 code 순이라 대장장이·목수만 나온다.
var chosen = ImmutableArray.CreateBuilder<Sample>(pairs);
var used = new HashSet<int>();

for (int index = 0; index < population && chosen.Count < pairs; index++)
{
    NpcInstanceDef instance = instances[(int)((long)index * instances.Count / population)];

    if (!used.Add(instance.Archetype.Value))
    {
        continue;
    }

    ZoneDef zone = data.Zones[instance.Zone];

    chosen.Add(new Sample(
        index,
        instance.Id,
        data.Archetypes[instance.Archetype].Id,
        zone.DefaultRegionState,
        zone.DefaultClimate));
}

if (chosen.Count < pairs)
{
    throw new InvalidOperationException(
        $"population {population} 에서 서로 다른 아키타입 {pairs} 종을 못 뽑았다 ({chosen.Count}종).");
}

ImmutableArray<Sample> samples = chosen.ToImmutable();

if (planOnly)
{
    Console.WriteLine("# A군 플랜 생성에 필요한 --only 글롭");
    Console.WriteLine();

    foreach (Sample sample in samples)
    {
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  --only \"{sample.Archetype}@*.{sample.Region}.{sample.Climate}\""));
    }

    Console.WriteLine();
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"버킷 {samples.Length * BucketKey.TimeOfDayCount}개"));
    return 0;
}

if (traceA is null || traceB is null)
{
    Console.Error.WriteLine("--trace-a 와 --trace-b 가 필요하다. 먼저 --plan-only 로 앞 단계를 확인한다.");
    return 1;
}

// ── 2. 서술 ──────────────────────────────────────────────────────────
var options = new NarrateOptions(
    Trace: traceA,
    MasterData: masterDataDir,
    Npcs: [],
    OutputDirectory: null,
    TimeScale: timeScale,
    StartGameHour: NarrateOptions.DefaultStartHour,
    MaxLines: NarrateOptions.DefaultMaxLines,
    Region: null,
    Climate: null,
    Cycles: NarrateOptions.DefaultCycles,
    Population: population);

var narrator = new Narrator(data, instances, options);

var cases = ImmutableArray.CreateBuilder<Case>(samples.Length * 2);

foreach (Sample sample in samples)
{
    cases.Add(new Case("A", sample, narrator.Narrate(traceA, sample.Index)));
    cases.Add(new Case("B", sample, narrator.Narrate(traceB, sample.Index)));
}

// ── 3. 제시 순서 무작위화 — 시드 고정 ────────────────────────────────
//
// Random 을 쓰지 않는다 (CLAUDE.md §2.3). SplitMix 해시로 섞으면 같은 시드에서
// 언제나 같은 배치가 나오고, 참가자를 추가할 때 그대로 재현된다.
Case[] shuffled = [.. cases];

for (int i = shuffled.Length - 1; i > 0; i--)
{
    int j = (int)(PlanHash.Mix(seed, i) % (uint)(i + 1));
    (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
}

// ── 4. 산출 ──────────────────────────────────────────────────────────
Directory.CreateDirectory(outDir);

foreach (string stale in Directory.GetFiles(outDir, "case_*.md"))
{
    File.Delete(stale);
}

var utf8 = new UTF8Encoding(false);

for (int i = 0; i < shuffled.Length; i++)
{
    string path = Path.Combine(outDir, string.Create(CultureInfo.InvariantCulture, $"case_{i + 1:D2}.md"));
    var body = new StringBuilder(2 * 1024);

    body.Append(CultureInfo.InvariantCulture, $"## 사례 {i + 1:D2}\n\n```\n");
    body.Append(shuffled[i].Text.TrimEnd('\n'));
    body.Append("\n```\n");

    File.WriteAllText(path, body.ToString().ReplaceLineEndings("\n"), utf8);
}

// ── 5. 정답 키 — 자료와 같은 폴더에 두지 않는다 ──────────────────────
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(keyPath))!);

var key = new StringBuilder(8 * 1024);

key.Append("# 블라인드 A/B 평가 — 정답 키\n\n");
key.Append("> `tools/gen_blind_eval.cs` 가 만든다. **참가자에게 주지 않는다.**\n");
key.Append("> 자료 자체는 `artifacts/blind_eval/` 에 있고 그쪽은 `.gitignore` 대상이다.\n\n");
key.Append(CultureInfo.InvariantCulture, $"| 배치 시드 | `{seed}` |\n|---|---|\n");
key.Append(CultureInfo.InvariantCulture, $"| 표본 | {samples.Length}쌍 = {shuffled.Length}건 |\n");
key.Append(CultureInfo.InvariantCulture, $"| 모집단 | {population} (호스트 `--npcs`) |\n");
key.Append(CultureInfo.InvariantCulture, $"| 배속 | {timeScale} |\n");
key.Append(CultureInfo.InvariantCulture, $"| A군 기록 | `{Path.GetFileName(traceA)}` (프리베이크 플랜) |\n");
key.Append(CultureInfo.InvariantCulture, $"| B군 기록 | `{Path.GetFileName(traceB)}` (아키타입 폴백 = 사람이 짠 플랜) |\n\n");

key.Append("## 배치\n\n");
key.Append("| 제시번호 | 군 | NPC | 아키타입 | 지역상태 | 기후 |\n|---|---|---|---|---|---|\n");

for (int i = 0; i < shuffled.Length; i++)
{
    Case item = shuffled[i];

    key.Append(CultureInfo.InvariantCulture,
        $"| {i + 1:D2} | {item.Group} | #{item.Sample.InstanceId} | {item.Sample.Archetype} | "
        + $"{item.Sample.Region} | {item.Sample.Climate} |\n");
}

key.Append("\n## 짝 매칭\n\n");
key.Append("같은 NPC 가 두 번 나온다 — A군과 B군에서 **같은 세계·같은 시각**을 살았고 플랜만 다르다.\n");
key.Append("아키타입·시간대·지역상태를 따로 맞출 필요가 없는 것은 그 때문이다.\n\n");
key.Append("| NPC | 아키타입 | A 제시번호 | B 제시번호 |\n|---|---|---|---|\n");

foreach (Sample sample in samples)
{
    int a = Array.FindIndex(shuffled, c => c.Group == "A" && c.Sample.Index == sample.Index) + 1;
    int b = Array.FindIndex(shuffled, c => c.Group == "B" && c.Sample.Index == sample.Index) + 1;

    key.Append(CultureInfo.InvariantCulture,
        $"| #{sample.InstanceId} | {sample.Archetype} | {a:D2} | {b:D2} |\n");
}

File.WriteAllText(keyPath, key.ToString().ReplaceLineEndings("\n"), utf8);

Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{outDir}: case_01.md .. case_{shuffled.Length:D2}.md"));
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{keyPath}: 정답 키 (시드 {seed})"));

return 0;

/// <summary>표본 하나. 같은 NPC 가 A·B 양쪽에 쓰인다.</summary>
internal readonly record struct Sample(
    int Index, int InstanceId, string Archetype, RegionState Region, Climate Climate);

/// <summary>제시 자료 하나.</summary>
internal readonly record struct Case(string Group, Sample Sample, string Text);
