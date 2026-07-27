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

// ── 6. 응답 수집 양식 (T5-14) ────────────────────────────────────────
//
// 양식도 산출물이다 — 사례 수와 짝 목록이 배치에 따라 달라지므로 손으로 쓰면 어긋난다.
//
// 쌍대 비교는 1부를 제출한 뒤에 한다. 짝을 먼저 알려 주면 "둘 중 하나가 A" 라는
// 정보가 Q1 에 새어 들어간다. 좌우 순서도 시드로 섞는다 — A 가 늘 왼쪽이면
// 순서 자체가 단서다.
//
// 안내문에 "구분 불가도 성공" 이라는 프레이밍을 넣지 않는다 (docs/15 §6 편향 방지).
var form = new StringBuilder(16 * 1024);

form.Append("# NPC 행동 평가 — 응답 양식\n\n");
form.Append("읽어 주셔서 감사합니다. 아래 사례는 게임 속 NPC 한 마리의 **하루 일지**입니다.\n");
form.Append(CultureInfo.InvariantCulture, $"모두 {shuffled.Length}건이고 순서대로 보시면 됩니다.\n\n");
form.Append(CultureInfo.InvariantCulture, $"사례 본문은 같은 폴더의 `case_01.md` ~ `case_{shuffled.Length:D2}.md` 에 있습니다.\n\n");
form.Append("---\n\n");

form.Append("## 1부 — 사례별 응답\n\n");
form.Append("각 사례에 대해 두 가지를 답해 주세요.\n\n");
form.Append("| 질문 | 답 |\n|---|---|\n");
form.Append("| **Q1.** 이 NPC 의 하루를 계획한 것은 무엇이라고 생각하십니까? | `llm` 또는 `human` |\n");
form.Append("| **Q2.** 이 NPC 의 행동이 상황에 얼마나 잘 맞습니까? | `1`(전혀 안 맞음) ~ `5`(매우 잘 맞음) |\n\n");
form.Append("> Q1 은 확신이 없어도 한쪽을 고릅니다. \"모르겠다\" 칸은 두지 않았습니다.\n\n");

form.Append("### 제출 형식\n\n");
form.Append("`docs/measurements/blind_eval_raw.jsonl` 에 **한 줄에 한 판정**으로 덧붙입니다.\n\n");
form.Append("```jsonl\n");
form.Append("{\"participant\":\"P01\",\"case\":1,\"q1\":\"llm\",\"q2\":4}\n");
form.Append("{\"participant\":\"P01\",\"case\":2,\"q1\":\"human\",\"q2\":3}\n");
form.Append("```\n\n");
form.Append("`participant` 는 배포 시 받은 식별자입니다. 이름을 적지 않습니다.\n\n");

form.Append("### 응답표 (복사해서 쓰세요)\n\n");
form.Append("| 사례 | Q1 (llm/human) | Q2 (1~5) |\n|---|---|---|\n");

for (int i = 0; i < shuffled.Length; i++)
{
    form.Append(CultureInfo.InvariantCulture, $"| {i + 1:D2} |  |  |\n");
}

form.Append("\n---\n\n");
form.Append("## 2부 — 쌍대 비교\n\n");
form.Append("**1부를 제출하신 뒤에** 진행합니다.\n\n");
form.Append(CultureInfo.InvariantCulture,
    $"아래 {samples.Length}개 짝은 서로 비교할 만한 사례입니다. 두 건을 나란히 놓고 ");
form.Append("**어느 쪽이 더 자연스러운가**를 골라 주세요.\n\n");
form.Append("```jsonl\n");
form.Append("{\"participant\":\"P01\",\"pair\":1,\"prefer\":\"left\"}\n");
form.Append("```\n\n");
form.Append("`prefer` 는 `left` · `right` · `tie` 중 하나입니다.\n\n");
form.Append("| 짝 | 왼쪽 | 오른쪽 | 더 자연스러운 쪽 |\n|---|---|---|---|\n");

var pairing = new StringBuilder(2 * 1024);

pairing.Append("\n## 쌍대 비교 배치\n\n");
pairing.Append("`prefer` 가 가리키는 쪽이 어느 군인지는 여기서만 안다.\n\n");
pairing.Append("| 짝 | 왼쪽 사례 | 왼쪽 군 | 오른쪽 사례 | 오른쪽 군 |\n|---|---|---|---|---|\n");

for (int p = 0; p < samples.Length; p++)
{
    Sample sample = samples[p];

    int a = Array.FindIndex(shuffled, c => c.Group == "A" && c.Sample.Index == sample.Index) + 1;
    int b = Array.FindIndex(shuffled, c => c.Group == "B" && c.Sample.Index == sample.Index) + 1;

    bool swap = (PlanHash.Mix(seed ^ 0x5EED, p) & 1u) == 1u;
    (int left, int right) = swap ? (b, a) : (a, b);

    form.Append(CultureInfo.InvariantCulture, $"| {p + 1:D2} | 사례 {left:D2} | 사례 {right:D2} |  |\n");
    pairing.Append(CultureInfo.InvariantCulture,
        $"| {p + 1:D2} | {left:D2} | {(swap ? "B" : "A")} | {right:D2} | {(swap ? "A" : "B")} |\n");
}

form.Append("\n---\n\n");
form.Append("## 배포 계획\n\n");
form.Append("| 항목 | 값 |\n|---|---|\n");
form.Append("| 목표 참가자 | 12명 (사내 기획자·개발자) |\n");
form.Append(CultureInfo.InvariantCulture, $"| 1인당 판정 | {shuffled.Length}건 + 쌍대 {samples.Length}건 |\n");
form.Append(CultureInfo.InvariantCulture, $"| 목표 표본 | 12 × {shuffled.Length} = {12 * shuffled.Length} 판정 |\n");
form.Append("| 예상 소요 | 1인 40~60분 |\n");
form.Append("| 최소 인원 | **6명.** 미만이면 결론을 내지 않고 표본을 늘린다 (docs/15 §6) |\n");

File.WriteAllText(
    Path.Combine(outDir, "response_form.md"), form.ToString().ReplaceLineEndings("\n"), utf8);

File.AppendAllText(keyPath, pairing.ToString().ReplaceLineEndings("\n"), utf8);

// ── 7. 배포용 한 장 ──────────────────────────────────────────────────
//
// 참가자에게 40개 파일을 따로 주면 순서가 섞이고 빠뜨린 사례가 생긴다.
// 안내 + 사례 40건 + 응답표를 한 파일로 묶는다. 정답 키는 여기 들어가지 않는다.
var bundle = new StringBuilder(64 * 1024);

// 한 장에는 사례가 안에 들어 있으니 "다른 파일을 보라" 는 안내를 지운다.
string intro = form.ToString()[..form.ToString().IndexOf("### 응답표", StringComparison.Ordinal)]
    .Replace(
        string.Create(CultureInfo.InvariantCulture, $"사례 본문은 같은 폴더의 `case_01.md` ~ `case_{shuffled.Length:D2}.md` 에 있습니다."),
        "사례는 이 문서 아래에 순서대로 실려 있습니다.",
        StringComparison.Ordinal);

bundle.Append(intro);
bundle.Append("---\n\n## 사례\n\n");

for (int i = 0; i < shuffled.Length; i++)
{
    bundle.Append(CultureInfo.InvariantCulture, $"### 사례 {i + 1:D2}\n\n```\n");
    bundle.Append(shuffled[i].Text.TrimEnd('\n'));
    bundle.Append("\n```\n\n");
}

bundle.Append("---\n\n");
bundle.Append(form.ToString()[form.ToString().IndexOf("### 응답표", StringComparison.Ordinal)..]);

File.WriteAllText(
    Path.Combine(outDir, "bundle.md"), bundle.ToString().ReplaceLineEndings("\n"), utf8);

Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{outDir}: case_01.md .. case_{shuffled.Length:D2}.md"));
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{outDir}/response_form.md: 응답 양식"));
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{outDir}/bundle.md: 배포용 한 장 (안내 + 사례 40건 + 응답표)"));
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{keyPath}: 정답 키 (시드 {seed})"));

return 0;

/// <summary>표본 하나. 같은 NPC 가 A·B 양쪽에 쓰인다.</summary>
internal readonly record struct Sample(
    int Index, int InstanceId, string Archetype, RegionState Region, Climate Climate);

/// <summary>제시 자료 하나.</summary>
internal readonly record struct Case(string Group, Sample Sample, string Text);
