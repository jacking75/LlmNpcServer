using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Npc.Contracts;
using Npc.Core;
using Npc.Gateway;
using Npc.MasterData;

namespace Npc.Narrate;

/// <summary>
/// 명령 로그 → 사람이 읽는 하루 일지. docs/15 §6 · T5-12.
///
/// <b>LLM 을 쓰지 않는다.</b> 대사 생성이 아니라 액션 시퀀스의 기계적 번역이다.
///
/// <b>서술에 틱 번호·상관 ID·플랜 id 를 남기지 않는다.</b> 그것 자체가 A/B 를 가르는
/// 단서가 되어 블라인드 평가가 성립하지 않는다 (docs/15 §6).
/// 나가는 것은 게임 시각(HH:MM)과 사람이 읽는 명사뿐이다.
/// </summary>
public static class Program
{
    /// <summary>진입점.</summary>
    public static int Main(string[] args)
    {
        if (!NarrateOptions.TryParse(args, out NarrateOptions options, out string? error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine(NarrateOptions.Usage);
            return 1;
        }

        MasterDataSet data = MasterDataLoader.Load(options.MasterData);
        NpcInstanceTable instances = NpcInstanceTable.Load(
            Path.Combine(options.MasterData, "npc_instances.json"), data);

        var narrator = new Narrator(data, instances, options);

        // 기록의 NpcId 는 NpcStore 첨자다. 어느 인스턴스인지는 호스트가 --npcs 로 몇 마리를
        // 뽑았느냐에 달려 있으므로, 기록에서 그 수를 먼저 읽는다.
        ImmutableArray<int> present = narrator.NpcsInTrace(options.Trace);
        ImmutableArray<int> targets = options.Npcs.IsDefaultOrEmpty ? present : options.Npcs;

        if (targets.IsEmpty)
        {
            Console.Error.WriteLine("기록에 NPC 가 하나도 없다.");
            return 1;
        }

        foreach (int npc in targets)
        {
            string text = narrator.Narrate(options.Trace, npc);

            if (options.OutputDirectory is { } directory)
            {
                Directory.CreateDirectory(directory);

                string path = Path.Combine(
                    directory, string.Create(CultureInfo.InvariantCulture, $"npc_{npc:D5}.md"));

                File.WriteAllText(path, text, new UTF8Encoding(false));
                Console.WriteLine(path);
            }
            else
            {
                Console.WriteLine(text);
            }
        }

        return 0;
    }
}

/// <summary>서술기 옵션.</summary>
/// <param name="Trace"><c>--link record</c> 가 만든 jsonl.</param>
/// <param name="MasterData"><c>masterdata/</c> 경로.</param>
/// <param name="Npcs">서술할 NPC id. 비면 기록에 나오는 전부.</param>
/// <param name="OutputDirectory">건별 <c>.md</c> 를 쓸 폴더. 없으면 표준출력.</param>
/// <param name="TimeScale">기록을 만든 실행의 <c>--time-scale</c>. 게임 시각 환산에 쓴다.</param>
/// <param name="StartGameHour">기록을 만든 실행의 시작 게임 시각.</param>
/// <param name="MaxLines">한 건의 최대 줄 수. 하루 일지가 100줄이면 아무도 안 읽는다.</param>
/// <param name="Region">머리말의 지역 상태. 없으면 NPC 가 사는 존의 기본값.</param>
/// <param name="Climate">머리말의 기후. 없으면 존의 기본값.</param>
/// <param name="Cycles">몇 바퀴를 적을 것인가. 하루 일과는 loop 플랜이라 같은 사이클이 반복된다.</param>
/// <param name="Population">기록을 만든 실행의 <c>--npcs</c>. 없으면 기록에서 유추한다.</param>
public sealed record NarrateOptions(
    string Trace,
    string MasterData,
    ImmutableArray<int> Npcs,
    string? OutputDirectory,
    int TimeScale,
    int StartGameHour,
    int MaxLines,
    RegionState? Region,
    Climate? Climate,
    int Cycles,
    int? Population)
{
    /// <summary>기본 게임 시작 시각. <c>GameClock</c> 기본값과 같다.</summary>
    public const int DefaultStartHour = 6;

    /// <summary>기본 최대 줄 수.</summary>
    public const int DefaultMaxLines = 24;

    /// <summary>기본 사이클 수. 한 바퀴면 플랜 하나를 다 보는 것이다.</summary>
    public const int DefaultCycles = 1;

    /// <summary>사용법.</summary>
    public const string Usage = """
        사용법:
          Npc.Narrate --trace <link.jsonl> [옵션]

          --trace <path>          --link record 가 만든 jsonl (필수)
          --masterdata <dir>      기본 ./masterdata
          --npc <id>              서술할 NPC. 여러 번 줄 수 있다. 없으면 기록의 전부
          --out <dir>             건별 .md 출력 폴더. 없으면 표준출력
          --time-scale <n>        기록을 만든 실행의 배속 (기본 600)
          --start-hour <0~23>     기록을 만든 실행의 시작 게임 시각 (기본 6)
          --max-lines <n>         한 건의 최대 줄 수 (기본 24)
          --region <상태>         머리말의 지역 상태 (Peace|Alert|War|Disaster)
          --climate <기후>        머리말의 기후 (Fair|Cold|Storm)
          --cycles <n>            반복되는 하루 일과를 몇 바퀴 적을 것인가 (기본 1)
          --population <n>        기록을 만든 실행의 --npcs. 없으면 기록에서 유추한다
        """;

    /// <summary>인자를 읽는다. 실패하면 이유를 준다.</summary>
    public static bool TryParse(string[] args, out NarrateOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? trace = null;
        string masterData = "./masterdata";
        var npcs = ImmutableArray.CreateBuilder<int>();
        string? output = null;
        int timeScale = 600;
        int startHour = DefaultStartHour;
        int maxLines = DefaultMaxLines;
        RegionState? region = null;
        Climate? climate = null;
        int cycles = DefaultCycles;
        int? population = null;

        error = null;
        options = null!;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--trace" when i + 1 < args.Length:
                    trace = args[++i];
                    break;

                case "--masterdata" when i + 1 < args.Length:
                    masterData = args[++i];
                    break;

                case "--npc" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], CultureInfo.InvariantCulture, out int npc))
                    {
                        error = $"--npc 값이 정수가 아니다: {args[i]}";
                        return false;
                    }

                    npcs.Add(npc);
                    break;

                case "--out" when i + 1 < args.Length:
                    output = args[++i];
                    break;

                case "--time-scale" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], CultureInfo.InvariantCulture, out timeScale) || timeScale <= 0)
                    {
                        error = $"--time-scale 이 양의 정수가 아니다: {args[i]}";
                        return false;
                    }

                    break;

                case "--start-hour" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], CultureInfo.InvariantCulture, out startHour) || startHour is < 0 or > 23)
                    {
                        error = $"--start-hour 가 0~23 이 아니다: {args[i]}";
                        return false;
                    }

                    break;

                case "--max-lines" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], CultureInfo.InvariantCulture, out maxLines) || maxLines <= 0)
                    {
                        error = $"--max-lines 가 양의 정수가 아니다: {args[i]}";
                        return false;
                    }

                    break;

                case "--region" when i + 1 < args.Length:
                    if (!Enum.TryParse(args[++i], ignoreCase: true, out RegionState parsedRegion))
                    {
                        error = $"--region 값을 모른다: {args[i]}";
                        return false;
                    }

                    region = parsedRegion;
                    break;

                case "--climate" when i + 1 < args.Length:
                    if (!Enum.TryParse(args[++i], ignoreCase: true, out Climate parsedClimate))
                    {
                        error = $"--climate 값을 모른다: {args[i]}";
                        return false;
                    }

                    climate = parsedClimate;
                    break;

                case "--population" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], CultureInfo.InvariantCulture, out int parsedPopulation)
                        || parsedPopulation <= 0)
                    {
                        error = $"--population 이 양의 정수가 아니다: {args[i]}";
                        return false;
                    }

                    population = parsedPopulation;
                    break;

                case "--cycles" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], CultureInfo.InvariantCulture, out cycles) || cycles <= 0)
                    {
                        error = $"--cycles 가 양의 정수가 아니다: {args[i]}";
                        return false;
                    }

                    break;

                default:
                    error = $"모르는 인자다: {args[i]}";
                    return false;
            }
        }

        if (string.IsNullOrEmpty(trace))
        {
            error = "--trace 는 필수다.";
            return false;
        }

        options = new NarrateOptions(
            trace, masterData, npcs.ToImmutable(), output, timeScale, startHour, maxLines,
            region, climate, cycles, population);

        return true;
    }
}

/// <summary>
/// 기록 한 줄을 서술 한 줄로 옮긴다.
///
/// 옮기는 규칙은 전부 마스터데이터에서 나온다 — POI 의 <c>type</c>·<c>subtype</c> 이
/// 같은 <c>Interact</c> 를 "캐냄"과 "만듦"으로 가른다. 명령 종류만으로는 알 수 없다
/// (액션 → 명령 변환에서 액션 정체성이 사라진다, docs/01 §2.1 의 <c>emits</c>).
/// </summary>
public sealed class Narrator
{
    private readonly MasterDataSet _data;
    private readonly NpcInstanceTable _instances;
    private readonly NarrateOptions _options;

    /// <summary>기록을 만든 실행의 NPC 수. 첨자 → 인스턴스 매핑에 쓴다.</summary>
    private int _population;

    /// <summary>서술기 하나.</summary>
    public Narrator(MasterDataSet data, NpcInstanceTable instances, NarrateOptions options)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _instances = instances ?? throw new ArgumentNullException(nameof(instances));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>기록에 나오는 NPC id (오름차순).</summary>
    public ImmutableArray<int> NpcsInTrace(string trace)
    {
        var seen = new SortedSet<int>();

        foreach (LinkRecord record in Read(trace))
        {
            if (record.Command is { } command)
            {
                seen.Add(command.Npc.Value);
            }
        }

        _population = _options.Population ?? (seen.Count == 0 ? 0 : seen.Max + 1);

        return [.. seen];
    }

    /// <summary>NPC 하나의 하루 일지.</summary>
    public string Narrate(string trace, int npc) => Render(npc, Lines(trace, npc));

    /// <summary>기록 대신 이미 읽어 둔 줄들로 서술한다. 테스트가 쓴다.</summary>
    public string NarrateFrom(IEnumerable<LinkRecord> records, int npc) =>
        Render(npc, LinesFrom(records, npc));

    // ------------------------------------------------------------------ 본문

    private string Render(int npc, ImmutableArray<string> lines)
    {
        var sb = new StringBuilder(4 * 1024);

        sb.Append(Header(npc)).Append('\n');

        foreach (string line in lines)
        {
            sb.Append(line).Append('\n');
        }

        return sb.ToString();
    }

    private string Header(int npc)
    {
        NpcInstanceDef instance = Instance(npc);
        ZoneDef zone = _data.Zones[instance.Zone];

        RegionState region = _options.Region ?? zone.DefaultRegionState;
        Climate climate = _options.Climate ?? zone.DefaultClimate;
        TimeOfDay time = _data.Buckets.TimeOfDayAt(_options.StartGameHour);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"[NPC #{instance.Id} · {Lexicon.Archetype(_data.Archetypes[instance.Archetype].Id)} · "
            + $"{Lexicon.Of(region)} · {Lexicon.Of(climate)} {Lexicon.Of(time)}]");
    }

    private ImmutableArray<string> Lines(string trace, int npc) => LinesFrom(Read(trace), npc);

    private ImmutableArray<string> LinesFrom(IEnumerable<LinkRecord> records, int npc)
    {
        var lines = ImmutableArray.CreateBuilder<string>();
        string previous = string.Empty;
        string first = string.Empty;
        int cycles = 0;

        foreach (LinkRecord record in records)
        {
            string? text = record.Kind switch
            {
                RecordKind.Command when record.Command is { } c && c.Npc.Value == npc => Describe(in c),
                RecordKind.Event when record.Event is { } e && e.Npc.Value == npc => Describe(in e),
                _ => null,
            };

            if (text is null)
            {
                continue;
            }

            long tick = record.Command?.IssuedAt.Value ?? record.Event!.Value.OccurredAt.Value;
            string line = string.Create(CultureInfo.InvariantCulture, $"{Clock(tick)}  {text}");

            // 같은 문장이 연달아 나오면 한 번만 적는다 — 재시도·연출이 그대로 실리면 일지가 아니라 로그다.
            if (string.Equals(Body(line), previous, StringComparison.Ordinal))
            {
                continue;
            }

            previous = Body(line);

            // 하루 일과는 loop 플랜이라 같은 사이클이 계속 돈다. 첫 문장이 다시 나오면
            // 한 바퀴가 끝난 것이다 — 반복분을 그대로 실으면 읽는 사람이 같은 글을 스무 번 본다.
            if (first.Length == 0)
            {
                first = previous;
            }
            else if (string.Equals(previous, first, StringComparison.Ordinal) && ++cycles >= _options.Cycles)
            {
                break;
            }

            lines.Add(line);

            if (lines.Count >= _options.MaxLines)
            {
                break;
            }
        }

        return lines.ToImmutable();

        static string Body(string line) => line[Math.Min(7, line.Length)..];
    }

    /// <summary>명령 한 건의 서술. 연출 계열은 null 을 돌려 걸러낸다.</summary>
    private string? Describe(in NpcCommand command) => command.Kind switch
    {
        NpcCommandKind.MoveTo when command.TargetPoi.Value != 0 =>
            $"{Lexicon.To(Place(command.TargetPoi))} 이동{((MoveSpeed)command.Flags == MoveSpeed.Run ? " (뛰어서)" : string.Empty)}",

        NpcCommandKind.Interact => Interaction(in command),

        NpcCommandKind.InventoryChange when command.Item.Value != 0 && command.Amount > 0 =>
            $"{Item(command.Item)} {command.Amount}개를 챙김",

        NpcCommandKind.InventoryChange when command.Item.Value != 0 =>
            $"{Lexicon.With(Item(command.Item), "을", "를")} 씀",

        NpcCommandKind.CombatAction => "맞서 싸움",

        NpcCommandKind.Speak => "다른 사람과 이야기를 나눔",

        NpcCommandKind.SetVisualState when command.Visual == VisualState.Sleeping => "잠자리에 듦",

        // Spawn·Despawn·Stop·FaceTo·PlayAnimation·SetAggro 는 연출이거나 내부 신호라 일지에 남기지 않는다.
        _ => null,
    };

    /// <summary>
    /// <c>Interact</c> 하나가 채굴이기도 하고 제작이기도 하다.
    /// 무엇인지는 <b>어디에서</b> 하는가로 가른다 — 액션 정체성은 명령에 실리지 않는다.
    /// </summary>
    private string? Interaction(in NpcCommand command)
    {
        if (command.TargetPoi.Value == 0)
        {
            return null;
        }

        PoiDef poi = _data.Pois[command.TargetPoi];
        string place = Lexicon.Place(poi.Subtype);

        if (command.Item.Value == 0)
        {
            return $"{place}에서 일함";
        }

        string item = Item(command.Item);
        int count = Math.Max(1, command.Amount);

        return poi.Type switch
        {
            PoiType.Field or PoiType.Wilderness =>
                $"{place}에서 {item} {count}개를 {Lexicon.HarvestVerb(poi.Subtype)}",

            PoiType.Workplace => $"{place}에서 {item} {count}개를 만듦",

            PoiType.Market => $"{place}에서 {item} {count}개를 거래함",

            _ => $"{place}에서 {item} {count}개를 다룸",
        };
    }

    /// <summary>이벤트 한 건의 서술. 도착만 남긴다 — 나머지는 명령 쪽에 이미 나온다.</summary>
    private string? Describe(in GameEvent ev) => ev.Kind switch
    {
        GameEventKind.NpcArrived when ev.Poi.Value != 0 => $"{Place(ev.Poi)} 도착",
        _ => null,
    };

    private string Place(PoiId poi) => Lexicon.Place(_data.Pois[poi].Subtype);

    private string Item(ItemId item) => Lexicon.Item(_data.Items[item].Id);

    /// <summary>
    /// 기록의 <c>NpcId</c>(NpcStore 첨자) → 인스턴스.
    ///
    /// 호스트는 <c>--npcs N</c> 이 전체보다 작으면 <b>균등 간격</b>으로 뽑는다 —
    /// 앞에서부터 자르면 아키타입 code 순이라 대장장이만 나온다. 같은 식을 써야
    /// 서술의 머리말에 실제 아키타입이 나간다. 안 그러면 광부의 하루에
    /// "대장장이" 라고 적힌다.
    /// </summary>
    private NpcInstanceDef Instance(int index)
    {
        int population = _population > 0 ? _population : _instances.Count;
        int slot = (int)((long)index * _instances.Count / population);

        return _instances[Math.Clamp(slot, 0, _instances.Count - 1)];
    }

    /// <summary>
    /// 틱 → 게임 시각 <c>HH:MM</c>.
    /// <b>틱 번호는 절대 서술에 나가지 않는다</b> — 그것 자체가 A/B 단서다 (docs/15 §6).
    /// </summary>
    private string Clock(long tick)
    {
        long seconds = ((long)_options.StartGameHour * 3600) + (tick * _options.TimeScale / Tick.PerSecond);
        long minutes = seconds / 60 % (24 * 60);

        return string.Create(CultureInfo.InvariantCulture, $"{minutes / 60:D2}:{minutes % 60:D2}");
    }

    private static IEnumerable<LinkRecord> Read(string trace)
    {
        foreach (string line in File.ReadLines(trace))
        {
            if (line.Length == 0)
            {
                continue;
            }

            LinkRecord? record = JsonSerializer.Deserialize(line, LinkRecordJsonContext.Default.LinkRecord);

            if (record is not null)
            {
                yield return record;
            }
        }
    }
}
