using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.MasterData;
using Npc.MasterData.Authoring;
using Npc.MasterData.Validation;
using Npc.Narrative;

namespace Npc.Studio.Services;

/// <summary>
/// Studio의 파일 경계다. 화면은 JSON 파일을 직접 쓰지 않고 이 서비스를 통해 검증된 변경만 저장한다.
/// </summary>
public sealed class StudioWorkspace(StudioOptions options)
{
    private static readonly ImmutableHashSet<string> s_editableFiles =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "world_flags.json", "items.json", "actions.json", "zones.json", "pois.json",
            "archetypes.json", "context_buckets.json", "interrupts.json", "fallback_plans.json",
            "dialogue_lines.json", "factions.json", "npc_overrides.json");

    private readonly object _gate = new();
    private static readonly JsonSerializerOptions s_indentedJson = new(JsonSurgeonText.Options)
    {
        WriteIndented = true,
    };

    /// <summary>
    /// 지금 보고 있는 디렉터리 (T30). 원본이거나 연습장이다.
    /// <b><c>_gate</c> 안에서만 바꾼다</b> — 읽는 중에 바뀌면 절반은 원본, 절반은 연습장을 읽는다.
    /// </summary>
    private string _directory = options.MasterData;

    /// <summary>연습장 이름. 원본을 보고 있으면 빈 문자열이다.</summary>
    private string _sandbox = string.Empty;

    /// <summary>연습장 이름 (T30).</summary>
    public string SandboxName
    {
        get { lock (_gate) { return _sandbox; } }
    }

    /// <summary>지금 읽고 쓰는 디렉터리. 연습장이면 연습장 경로다.</summary>
    public string CurrentDirectory
    {
        get { lock (_gate) { return _directory; } }
    }

    /// <summary>원본 마스터데이터 경로. 연습장에서도 바뀌지 않는다.</summary>
    public string OriginDirectory => options.MasterData;

    /// <summary>
    /// 연습장을 연다 (T30). 원본을 통째로 복사하고 이후 저장은 전부 그 사본으로 간다.
    ///
    /// <b>초보자가 손대지 못하는 가장 큰 이유가 "망가뜨릴까 봐" 다.</b> 실습서는
    /// <c>lab/&lt;이름&gt;/masterdata</c> 사본을 쓰라고 하는데 Studio 에는 그 개념이 없었다.
    /// </summary>
    /// <param name="name">연습장 이름. 파일 이름에 쓸 수 있는 글자만.</param>
    public string OpenSandbox(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string safe = new([.. name.Trim().Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-')]);

        lock (_gate)
        {
            string target = Path.Combine(SandboxRoot(), "studio-" + safe, "masterdata");

            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            Directory.CreateDirectory(target);
            CopyFrom(options.MasterData, target);

            _directory = target;
            _sandbox = safe;

            return target;
        }
    }

    /// <summary>연습장을 닫고 원본으로 돌아간다. 파일은 남는다.</summary>
    public void CloseSandbox()
    {
        lock (_gate)
        {
            _directory = options.MasterData;
            _sandbox = string.Empty;
        }
    }

    /// <summary>연습장을 지우고 원본으로 돌아간다.</summary>
    public void DiscardSandbox()
    {
        lock (_gate)
        {
            if (_sandbox.Length == 0)
            {
                return;
            }

            string folder = Path.GetDirectoryName(_directory)!;

            _directory = options.MasterData;
            _sandbox = string.Empty;

            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    /// <summary>
    /// 연습장이 원본과 다른 파일 (이름 오름차순). 바이트 비교다 — 서식까지 같아야 같다고 본다.
    /// </summary>
    public ImmutableArray<string> SandboxChanges()
    {
        lock (_gate)
        {
            if (_sandbox.Length == 0)
            {
                return [];
            }

            var changed = ImmutableArray.CreateBuilder<string>();

            foreach (string file in s_editableFiles.OrderBy(f => f, StringComparer.Ordinal))
            {
                string mine = Path.Combine(_directory, file);
                string origin = Path.Combine(options.MasterData, file);

                if (!File.Exists(mine) || !File.Exists(origin))
                {
                    continue;
                }

                if (!File.ReadAllBytes(mine).AsSpan().SequenceEqual(File.ReadAllBytes(origin)))
                {
                    changed.Add(file);
                }
            }

            return changed.ToImmutable();
        }
    }

    /// <summary>
    /// 연습장에서 바뀐 파일을 원본에 옮긴다. <b>원본에서 다시 검증한다</b> —
    /// 연습장에서 통과한 것이 원본에서도 통과한다는 보장은 없다 (생성물이 다를 수 있다).
    /// </summary>
    public StudioSaveResult ApplyToOrigin()
    {
        EnsureWritable();

        lock (_gate)
        {
            ImmutableArray<string> changed = SandboxChangesLocked();

            if (changed.IsEmpty)
            {
                return new StudioSaveResult(false, "연습장에서 바뀐 파일이 없다.", []);
            }

            var candidates = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (string file in changed)
            {
                candidates[file] = File.ReadAllText(Path.Combine(_directory, file));
            }

            string sandbox = _directory;
            _directory = options.MasterData;

            try
            {
                StudioSaveResult result = ValidateAndWrite(candidates);

                return result.Saved
                    ? result with { Message = $"연습장의 {changed.Length}개 파일을 원본에 적용했다." }
                    : result;
            }
            finally
            {
                _directory = sandbox;
            }
        }
    }

    private ImmutableArray<string> SandboxChangesLocked()
    {
        if (_sandbox.Length == 0)
        {
            return [];
        }

        var changed = ImmutableArray.CreateBuilder<string>();

        foreach (string file in s_editableFiles.OrderBy(f => f, StringComparer.Ordinal))
        {
            string mine = Path.Combine(_directory, file);
            string origin = Path.Combine(options.MasterData, file);

            if (File.Exists(mine) && File.Exists(origin)
                && !File.ReadAllBytes(mine).AsSpan().SequenceEqual(File.ReadAllBytes(origin)))
            {
                changed.Add(file);
            }
        }

        return changed.ToImmutable();
    }

    /// <summary>
    /// 연습장을 둘 곳. 저장소 안이면 <c>lab/</c> (실습서와 같은 자리, gitignore 됨),
    /// 밖이면 <c>%LOCALAPPDATA%\NpcStudio\lab\</c>.
    /// </summary>
    private string SandboxRoot()
    {
        DirectoryInfo? directory = new(options.MasterData);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NpcServer.sln")))
            {
                return Path.Combine(directory.FullName, "lab");
            }

            directory = directory.Parent;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NpcStudio", "lab");
    }

    /// <summary>
    /// 마스터데이터와 명단을 읽어 함수에 넘긴다 (T24·T27).
    ///
    /// <b>읽기 경로를 하나로 묶는 유일한 이유는 <c>_gate</c> 다</b> — 연습장 전환(T30)이
    /// 디렉터리를 바꾸므로, 읽는 도중에 바뀌면 절반은 원본 절반은 연습장을 읽는다.
    /// </summary>
    /// <typeparam name="T">돌려줄 값.</typeparam>
    /// <param name="read">읽기 함수. 명단이 없으면 두 번째 인자가 null 이다.</param>
    public T WithData<T>(Func<MasterDataSet, NpcInstanceTable?, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        lock (_gate)
        {
            MasterDataSet data = MasterDataLoader.Load(_directory);

            return read(data, TryLoadInstances(data));
        }
    }

    /// <summary>현재 카탈로그와 검증 상태를 읽는다.</summary>
    public StudioCatalog LoadCatalog()
    {
        lock (_gate)
        {
            MasterDataSet data = MasterDataLoader.Load(_directory);
            MasterDataValidationReport report = MasterDataValidator.Validate(_directory);
            string instancesPath = Path.Combine(_directory, "npc_instances.json");
            int instances = File.Exists(instancesPath)
                ? NpcInstanceTable.Load(instancesPath, data).Count
                : 0;

            ImmutableArray<StudioArchetype> archetypes =
            [
                .. data.Archetypes.Archetypes.Select(a => new StudioArchetype(
                    a.Id,
                    a.Code.Value,
                    a.Description,
                    a.PopulationWeight,
                    (int)Math.Round(a.PopulationWeight * ArchetypeCard.PopulationBase),
                    a.WorkplacePoiType ?? "—",
                    a.CombatCapable)),
            ];

            return new StudioCatalog(
                _directory,
                options.ReadOnly,
                archetypes,
                instances,
                ToIssues(report))
            {
                PoiCount = data.Pois.Count,
                ZoneCount = data.Zones.Count,
                InterruptCount = data.Interrupts.Count,
                ActionCount = data.Actions.Count,
                ItemCount = data.Items.Items.Length,
                StaleArtifacts = [.. DerivedArtifacts.Stale(_directory).Select(s => s.Artifact)],
                Sandbox = SandboxName,
            };
        }
    }

    /// <summary>아키타입 한 항목의 원문과 설명 카드를 읽는다.</summary>
    public StudioArchetypeDocument LoadArchetype(string id)
    {
        lock (_gate)
        {
            MasterDataSet data = MasterDataLoader.Load(_directory);
            string source = Read("archetypes.json");
            (int start, int end) = JsonSurgeon.ItemRange(source, "archetypes", "id", id);

            return new StudioArchetypeDocument(id, source[start..end], ArchetypeCard.Render(data, id));
        }
    }

    /// <summary>저장하지 않은 아키타입 JSON을 현재 마스터데이터와 합쳐 설명 카드로 만든다.</summary>
    public string PreviewArchetype(string id, string itemJson)
    {
        EnsureItemId(itemJson, id);

        lock (_gate)
        {
            MasterDataSet data = MasterDataLoader.Load(_directory);
            string source = Read("archetypes.json");
            (int start, int end) = JsonSurgeon.ItemRange(source, "archetypes", "id", id);
            string candidate = source[..start] + itemJson + source[end..];
            ArchetypeTable archetypes = ArchetypeTable.Parse(candidate, data.Actions, data.Items);

            if (!archetypes.TryGet(id, out ArchetypeDef draft))
            {
                throw new InvalidDataException($"아키타입 '{id}' 초안을 읽지 못했다.");
            }

            return ArchetypeCard.Render(data, draft);
        }
    }

    /// <summary>생성된 NPC를 아키타입·지역으로 탐색할 수 있는 가벼운 목록으로 읽는다.</summary>
    public ImmutableArray<StudioNpcSummary> LoadNpcDirectory()
    {
        lock (_gate)
        {
            MasterDataSet data = MasterDataLoader.Load(_directory);
            NpcInstanceTable instances = NpcInstanceTable.Load(
                Path.Combine(_directory, "npc_instances.json"), data);
            HashSet<int> overridden = LoadOverrideIds();
            var result = ImmutableArray.CreateBuilder<StudioNpcSummary>(instances.Count);

            foreach (NpcInstanceDef npc in instances.Instances)
            {
                string workplace = npc.Workplace == default ? "—" : data.Pois[npc.Workplace].Id;
                string faction = npc.Faction == default || data.Factions is null
                    ? "—"
                    : data.Factions.NameOf(npc.Faction);

                result.Add(new StudioNpcSummary(
                    npc.Id,
                    data.Archetypes[npc.Archetype].Id,
                    data.Zones[npc.Zone].Id,
                    data.Pois[npc.Home].Id,
                    workplace,
                    faction,
                    overridden.Contains(npc.Id)));
            }

            return result.ToImmutable();
        }
    }

    /// <summary>생성된 NPC 한 명을 사람이 읽는 카드로 보여 준다.</summary>
    public string LoadNpcCard(int id)
    {
        lock (_gate)
        {
            MasterDataSet data = MasterDataLoader.Load(_directory);
            NpcInstanceTable instances = NpcInstanceTable.Load(
                Path.Combine(_directory, "npc_instances.json"), data);
            int index = id - 1;

            if ((uint)index >= (uint)instances.Count || instances[index].Id != id)
            {
                throw new ArgumentOutOfRangeException(nameof(id), $"NPC {id}가 없다.");
            }

            return InstanceCard.Render(data, instances, index);
        }
    }

    /// <summary>
    /// 아키타입 하나를 <b>개요 보기</b>에 필요한 만큼 읽는다 (T05).
    /// JSON 원문 대신 이 값들이 섹션 카드가 된다.
    /// </summary>
    /// <param name="id">아키타입 id.</param>
    public StudioArchetypeView LoadArchetypeView(string id)
    {
        lock (_gate)
        {
            MasterDataSet data = MasterDataLoader.Load(_directory);

            if (!data.Archetypes.TryGet(id, out ArchetypeDef def))
            {
                throw new ArgumentException($"아키타입 '{id}' 이 없다.", nameof(id));
            }

            NpcInstanceTable? instances = TryLoadInstances(data);

            return BuildView(data, def, instances);
        }
    }

    /// <summary>저장하지 않은 초안으로 개요를 만든다 (T25). 나머지 마스터데이터는 현재 값이다.</summary>
    /// <param name="id">아키타입 id.</param>
    /// <param name="itemJson">초안 JSON.</param>
    public StudioArchetypeView PreviewArchetypeView(string id, string itemJson)
    {
        EnsureItemId(itemJson, id);

        lock (_gate)
        {
            MasterDataSet data = MasterDataLoader.Load(_directory);
            string source = Read("archetypes.json");
            (int start, int end) = JsonSurgeon.ItemRange(source, "archetypes", "id", id);
            string candidate = source[..start] + itemJson + source[end..];
            ArchetypeTable archetypes = ArchetypeTable.Parse(candidate, data.Actions, data.Items);

            if (!archetypes.TryGet(id, out ArchetypeDef draft))
            {
                throw new InvalidDataException($"아키타입 '{id}' 초안을 읽지 못했다.");
            }

            return BuildView(data, draft, TryLoadInstances(data));
        }
    }

    /// <summary>
    /// NPC 한 명을 <b>누구인가 · 어디 사는가 · 무엇을 하는가</b> 로 읽는다 (T07).
    /// 지도를 그릴 좌표와 개별 설정 폼이 같이 온다.
    /// </summary>
    /// <param name="id">NPC 번호.</param>
    public StudioNpcOverview LoadNpcOverview(int id)
    {
        lock (_gate)
        {
            MasterDataSet data = MasterDataLoader.Load(_directory);
            NpcInstanceTable instances = NpcInstanceTable.Load(
                Path.Combine(_directory, "npc_instances.json"), data);
            int index = id - 1;

            if ((uint)index >= (uint)instances.Count || instances[index].Id != id)
            {
                throw new ArgumentOutOfRangeException(nameof(id), $"NPC {id}가 없다.");
            }

            NpcInstanceDef npc = instances[index];
            InstanceFacts facts = InstanceCard.Facts(data, npc);

            // 순찰로가 없는 NPC 는 이 배열이 default 다 — 그대로 Select 하면 NullReference 다.
            ImmutableArray<string> route = npc.PatrolRoute.IsDefaultOrEmpty
                ? []
                : [.. npc.PatrolRoute.Select(p => data.Pois[p].Id)];

            return new StudioNpcOverview(
                npc,
                facts,
                InstanceCard.Sentence(data, npc),
                Lexicon.ArchetypeName(data, facts.ArchetypeId),
                Lexicon.Zone(data, facts.ZoneId),
                Lexicon.Group(facts.ArchetypeId),
                PoiChoices(data, npc),
                route);
        }
    }

    private StudioArchetypeView BuildView(MasterDataSet data, ArchetypeDef def, NpcInstanceTable? instances)
    {
        ArchetypeFacts facts = ArchetypeCard.Facts(data, def);
        CompiledPlan? fallback = facts.Fallback;

        ImmutableArray<StepTrace> trace = fallback is null
            ? []
            : PlanExplain.Trace(data, fallback, fallback.Bucket, def.Code);

        LoopVerdict loop = fallback is null
            ? new LoopVerdict(false, "하루 일과를 찾지 못했다.")
            : PlanExplain.LoopOf(fallback, trace.IsEmpty ? data.InitialFlags(fallback.Bucket) : trace[^1].After);

        return new StudioArchetypeView(
            facts,
            Lexicon.ArchetypeName(data, def.Id),
            Lexicon.Group(def.Id),
            trace,
            loop,
            ByZone(data, def, instances, facts.Population),
            ArchetypeLint.Run(data, def, instances));
    }

    /// <summary>
    /// 지역별 인구 (T33). 명단이 있으면 세고, 없으면(새 직업) 일터 정원 비례로 예측한다.
    /// </summary>
    private static ImmutableArray<StudioZoneCount> ByZone(
        MasterDataSet data, ArchetypeDef def, NpcInstanceTable? instances, int population)
    {
        if (instances is not null && instances.Instances.Any(n => n.Archetype == def.Code))
        {
            return
            [
                .. instances.Instances
                    .Where(n => n.Archetype == def.Code)
                    .GroupBy(n => n.Zone)
                    .Select(g => new StudioZoneCount(
                        data.Zones[g.Key].Id, Lexicon.Zone(data, data.Zones[g.Key].Id), g.Count(), false))
                    .OrderByDescending(z => z.Count)
                    .ThenBy(z => z.ZoneId, StringComparer.Ordinal),
            ];
        }

        return
        [
            .. PlacementForecast.ByZone(data, def, population)
                .Select(z => new StudioZoneCount(
                    data.Zones[z.Zone].Id, Lexicon.Zone(data, data.Zones[z.Zone].Id), z.Count, true)),
        ];
    }

    private NpcInstanceTable? TryLoadInstances(MasterDataSet data)
    {
        string path = Path.Combine(_directory, "npc_instances.json");

        return File.Exists(path) ? NpcInstanceTable.Load(path, data) : null;
    }

    private static ImmutableArray<StudioPoiChoice> PoiChoices(MasterDataSet data, NpcInstanceDef npc)
    {
        ZoneId zone = npc.Zone;
        PoiId home = npc.Home;
        PoiId workplace = npc.Workplace;

        return
        [
            .. data.Pois.Pois
                .Where(p => p.Zone == zone)
                .OrderBy(p => p.Type)
                .ThenBy(p => p.Subtype, StringComparer.Ordinal)
                .ThenBy(p => p.Id, StringComparer.Ordinal)
                .Select(p => new StudioPoiChoice(
                    p.Id,
                    PoiTypeLabel(p.Type),
                    p.Subtype,
                    p.Capacity,
                    p.Pos.X,
                    p.Pos.Z,
                    p.Code == home,
                    p.Code == workplace)
                {
                    Kind = p.Type,
                    Label = Lexicon.PlaceName(p),
                }),
        ];
    }

    /// <summary>개별 NPC의 손편집 오버라이드와 선택 가능한 같은 지역 POI·세력을 읽는다.</summary>
    public StudioNpcOverrideEditor LoadNpcOverride(int id)
    {
        lock (_gate)
        {
            MasterDataSet data = MasterDataLoader.Load(_directory);
            NpcInstanceTable instances = NpcInstanceTable.Load(
                Path.Combine(_directory, "npc_instances.json"), data);
            int index = id - 1;
            if ((uint)index >= (uint)instances.Count || instances[index].Id != id)
            {
                throw new ArgumentOutOfRangeException(nameof(id), $"NPC {id}가 없다.");
            }

            NpcInstanceDef npc = instances[index];
            JsonObject root = JsonNode.Parse(Read(NpcInstanceTable.OverrideFileName))?.AsObject()
                ?? throw new InvalidDataException("npc_overrides.json을 읽지 못했다.");
            JsonArray overrides = root["overrides"]?.AsArray()
                ?? throw new InvalidDataException("npc_overrides.json에 overrides 배열이 없다.");
            JsonObject? item = FindOverride(overrides, id);
            ImmutableArray<string> route = item?["patrol_route"] is JsonArray routeNode
                ? [.. routeNode.Select(n => n?.GetValue<string>() ?? string.Empty).Where(v => v.Length > 0)]
                : [];

            return new StudioNpcOverrideEditor(
                id,
                item is not null,
                route,
                item?["aggro_radius_m"]?.GetValue<int>(),
                item?["faction"]?.GetValue<string>() ?? string.Empty,
                item?["dialogue_profile"]?.GetValue<string>() ?? string.Empty,
                item?["schedule_offset_min"]?.GetValue<int>(),
                data.Factions is null ? [] : [.. data.Factions.Factions.Select(f => f.Id)],
                [.. data.Pois.Pois
                    .Where(p => p.Zone == npc.Zone)
                    .OrderBy(p => p.Type)
                    .ThenBy(p => p.Subtype, StringComparer.Ordinal)
                    .ThenBy(p => p.Id, StringComparer.Ordinal)
                    .Select(p => new StudioPoiChoice(
                        p.Id,
                        PoiTypeLabel(p.Type),
                        p.Subtype,
                        p.Capacity,
                        p.Pos.X,
                        p.Pos.Z,
                        p.Code == npc.Home,
                        p.Code == npc.Workplace)
                    {
                        Kind = p.Type,
                        Label = Lexicon.PlaceName(p),
                    })]);
        }
    }

    /// <summary>개별 NPC 오버라이드를 추가·교체한다. 빈 초안이면 해당 항목을 제거한다.</summary>
    public StudioSaveResult SaveNpcOverride(StudioNpcOverrideDraft draft)
    {
        EnsureWritable();
        ArgumentNullException.ThrowIfNull(draft);

        lock (_gate)
        {
            MasterDataSet data = MasterDataLoader.Load(_directory);
            NpcInstanceTable instances = NpcInstanceTable.Load(
                Path.Combine(_directory, "npc_instances.json"), data);
            int index = draft.Id - 1;
            if ((uint)index >= (uint)instances.Count || instances[index].Id != draft.Id)
            {
                throw new ArgumentOutOfRangeException(nameof(draft), $"NPC {draft.Id}가 없다.");
            }

            string source = Read(NpcInstanceTable.OverrideFileName);
            JsonObject root = JsonNode.Parse(source)?.AsObject()
                ?? throw new InvalidDataException("npc_overrides.json을 읽지 못했다.");
            JsonArray overrides = root["overrides"]?.AsArray()
                ?? throw new InvalidDataException("npc_overrides.json에 overrides 배열이 없다.");
            JsonObject? existing = FindOverride(overrides, draft.Id);
            if (existing is not null) overrides.Remove(existing);

            bool empty = draft.PatrolRoute.IsEmpty
                && draft.AggroRadiusM is null
                && string.IsNullOrWhiteSpace(draft.Faction)
                && string.IsNullOrWhiteSpace(draft.DialogueProfile)
                && draft.ScheduleOffsetMinutes is null;

            if (!empty)
            {
                var item = new JsonObject { ["id"] = draft.Id };
                if (!draft.PatrolRoute.IsEmpty)
                {
                    var route = new JsonArray();
                    foreach (string poi in draft.PatrolRoute) route.Add(poi);
                    item["patrol_route"] = route;
                }
                if (draft.AggroRadiusM is { } aggro) item["aggro_radius_m"] = aggro;
                if (!string.IsNullOrWhiteSpace(draft.Faction)) item["faction"] = draft.Faction.Trim();
                if (!string.IsNullOrWhiteSpace(draft.DialogueProfile)) item["dialogue_profile"] = draft.DialogueProfile.Trim();
                if (draft.ScheduleOffsetMinutes is { } offset) item["schedule_offset_min"] = offset;
                overrides.Add(item);
            }

            string candidate = root.ToJsonString(s_indentedJson) + Environment.NewLine;
            StudioSaveResult result = ValidateAndWrite(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [NpcInstanceTable.OverrideFileName] = candidate,
            });

            return result.Saved
                ? result with { Message = empty ? $"NPC {draft.Id} 오버라이드를 삭제했다." : $"NPC {draft.Id} 오버라이드를 저장했다." }
                : result;
        }
    }

    /// <summary>사람이 편집할 수 있는 JSON 파일 원문을 읽는다.</summary>
    public string LoadFile(string fileName)
    {
        EnsureEditable(fileName);

        lock (_gate)
        {
            return Read(fileName);
        }
    }

    /// <summary>아키타입 항목을 교체한다. 전체 검증과 로더를 모두 통과해야 저장한다.</summary>
    public StudioSaveResult SaveArchetype(string id, string itemJson)
    {
        EnsureWritable();
        EnsureItemId(itemJson, id);

        lock (_gate)
        {
            string source = Read("archetypes.json");
            (int start, int end) = JsonSurgeon.ItemRange(source, "archetypes", "id", id);
            string candidate = source[..start] + itemJson + source[end..];

            return ValidateAndWrite(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["archetypes.json"] = candidate,
            });
        }
    }

    /// <summary>일반 JSON 파일을 저장한다. 생성물은 이 경로에 들어오지 못한다.</summary>
    public StudioSaveResult SaveFile(string fileName, string json)
    {
        EnsureWritable();
        EnsureEditable(fileName);

        using (JsonDocument.Parse(json))
        {
        }

        lock (_gate)
        {
            return ValidateAndWrite(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [fileName] = json,
            });
        }
    }

    // ------------------------------------------------------------------ 폼 편집 (T10 · T11)

    /// <summary>아키타입 하나를 폼으로 읽는다 (T10).</summary>
    /// <param name="id">아키타입 id.</param>
    public StudioArchetypeForm LoadArchetypeForm(string id)
    {
        lock (_gate)
        {
            return FormOf(ItemJson(id));
        }
    }

    /// <summary>
    /// 폼을 저장한다 (T10). <b>바뀐 필드만 고친다</b> — 통째로 다시 직렬화하면
    /// 서식·키 순서·주석 배열이 무너지고 그 diff 는 리뷰할 수 없다.
    /// </summary>
    /// <param name="form">저장할 폼.</param>
    public StudioSaveResult SaveArchetypeForm(StudioArchetypeForm form)
    {
        EnsureWritable();
        ArgumentNullException.ThrowIfNull(form);

        lock (_gate)
        {
            string item = ApplyForm(ItemJson(form.Id), form);
            string source = Read("archetypes.json");
            (int start, int end) = JsonSurgeon.ItemRange(source, "archetypes", "id", form.Id);

            return ValidateAndWrite(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["archetypes.json"] = source[..start] + item + source[end..],
            });
        }
    }

    /// <summary>폼 초안을 저장하지 않고 정의로 읽는다 (T25).</summary>
    /// <param name="form">초안.</param>
    public ArchetypeDef PreviewArchetypeForm(StudioArchetypeForm form)
    {
        ArgumentNullException.ThrowIfNull(form);

        lock (_gate)
        {
            MasterDataSet data = MasterDataLoader.Load(_directory);
            string source = Read("archetypes.json");
            (int start, int end) = JsonSurgeon.ItemRange(source, "archetypes", "id", form.Id);
            string candidate = source[..start] + ApplyForm(source[start..end], form) + source[end..];
            ArchetypeTable table = ArchetypeTable.Parse(candidate, data.Actions, data.Items);

            return table.TryGet(form.Id, out ArchetypeDef draft)
                ? draft
                : throw new InvalidDataException($"아키타입 '{form.Id}' 초안을 읽지 못했다.");
        }
    }

    /// <summary>
    /// 폼 편집기가 고를 수 있는 값들 (T10). 마스터데이터에서 읽는다 —
    /// 일터 유형·레시피·아이템을 코드에 하드코딩하지 않는다.
    /// </summary>
    public StudioArchetypeChoices LoadArchetypeChoices()
    {
        lock (_gate)
        {
            MasterDataSet data = MasterDataLoader.Load(_directory);

            return new StudioArchetypeChoices(
                [.. data.Pois.Pois.Where(p => p.Type == PoiType.Home).Select(p => p.Subtype).Distinct().Order(StringComparer.Ordinal)],
                [.. data.Pois.Pois.Where(p => p.Type != PoiType.Home).Select(p => p.Subtype).Distinct().Order(StringComparer.Ordinal)],
                [.. data.Items.Recipes.Select(r => r.Id).Order(StringComparer.Ordinal)],
                [.. data.Items.Items.Select(i => i.Id).Order(StringComparer.Ordinal)],
                [.. data.Actions.Actions.Select(a => a.Id)]);
        }
    }

    /// <summary>
    /// 새 장소를 <c>pois.json</c> 맨 뒤에 넣는다 (T13).
    ///
    /// <b><c>code</c> 는 <see cref="CodeAllocator"/> 가 정한다</b> — 눈으로 세면 중복·예약 구간 침범이 난다.
    /// 저장 뒤에는 거리표가 낡는다 (파급 패널이 그것을 말한다).
    /// </summary>
    /// <param name="draft">새 장소 초안.</param>
    public StudioSaveResult AppendPoi(StudioNewPlace draft)
    {
        EnsureWritable();
        ArgumentNullException.ThrowIfNull(draft);

        lock (_gate)
        {
            string source = Read("pois.json");
            int code = CodeAllocator.Next(_directory, "pois.json");
            // AppendToArray 가 배열의 들여쓰기를 붙인다 — 여기서는 0 기준으로 만든다.
            const string Nested = "  ";

            var fields = new List<(string, string)>(10)
            {
                ("id", StudioJsonFormat.Text(draft.Id)),
                ("code", StudioJsonFormat.Number(code)),
                ("zone", StudioJsonFormat.Text(draft.Zone)),
                ("type", StudioJsonFormat.Text(draft.Type)),
                ("subtype", StudioJsonFormat.Text(draft.Subtype)),
                ("pos", StudioJsonFormat.Object(
                    [
                        ("x", draft.X.ToString("0.##", CultureInfo.InvariantCulture)),
                        ("y", "0"),
                        ("z", draft.Z.ToString("0.##", CultureInfo.InvariantCulture)),
                    ],
                    Nested)),
                ("capacity", StudioJsonFormat.Number(draft.Capacity)),
                ("open_hours", StudioJsonFormat.Object(
                    [("from", StudioJsonFormat.Text(draft.OpenFrom)), ("to", StudioJsonFormat.Text(draft.OpenTo))],
                    Nested)),
            };

            if (!draft.AllowedArchetypes.IsDefaultOrEmpty)
            {
                fields.Add(("allowed_archetypes", StudioJsonFormat.Strings(draft.AllowedArchetypes, Nested)));
            }

            if (!draft.Resources.IsDefaultOrEmpty)
            {
                fields.Add(("resources", StudioJsonFormat.Strings(draft.Resources, Nested)));
            }

            StudioSaveResult result = ValidateAndWrite(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["pois.json"] = JsonSurgeon.AppendToArray(
                        source, "pois", StudioJsonFormat.Object(fields, string.Empty)),
                },
                loaderGate: false);

            return result.Saved
                ? result with
                {
                    Message = $"장소 {draft.Id} (code {code}) 를 만들었다. "
                        + "거리표(poi_distances.bin)가 낡았다 — 다시 만들어야 서버가 뜬다.",
                }
                : result;
        }
    }

    // ------------------------------------------------------------------ 하루 일과 폼 (T11)

    /// <summary>하루 일과를 폼으로 읽는다 (T11).</summary>
    /// <param name="archetypeId">직업 id.</param>
    public StudioFallbackForm LoadFallbackForm(string archetypeId)
    {
        lock (_gate)
        {
            (string item, string _) = FallbackItem(archetypeId);

            return FallbackOf(item);
        }
    }

    /// <summary>
    /// 하루 일과를 저장한다 (T11). 스텝의 키 순서는 <c>action → args → timeout_s</c> 로 고정한다 —
    /// 순서가 회차마다 다르면 diff 가 읽히지 않는다.
    /// </summary>
    /// <param name="form">저장할 폼.</param>
    public StudioSaveResult SaveFallbackForm(StudioFallbackForm form)
    {
        EnsureWritable();
        ArgumentNullException.ThrowIfNull(form);

        lock (_gate)
        {
            return ValidateAndWrite(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["fallback_plans.json"] = FallbackCandidate(form),
            });
        }
    }

    /// <summary>저장하지 않은 하루 일과를 컴파일해 판정한다 (T11).</summary>
    /// <param name="form">초안.</param>
    public StudioFallbackPreview PreviewFallbackForm(StudioFallbackForm form)
    {
        ArgumentNullException.ThrowIfNull(form);

        lock (_gate)
        {
            MasterDataSet data = MasterDataLoader.Load(_directory);

            if (!data.Archetypes.TryGet(form.Archetype, out ArchetypeDef def))
            {
                return new StudioFallbackPreview(null, [], new LoopVerdict(false, "직업을 찾지 못했다."), "직업을 찾지 못했다.");
            }

            try
            {
                PlanTable table = PlanTable.Parse(FallbackCandidate(form), data);
                CompiledPlan? plan = table.For(def.Code);

                if (plan is null)
                {
                    return new StudioFallbackPreview(null, [], new LoopVerdict(false, "플랜을 찾지 못했다."), "플랜을 찾지 못했다.");
                }

                ImmutableArray<StepTrace> trace = PlanExplain.Trace(data, plan, plan.Bucket, def.Code);
                WorldFlags final = trace.IsEmpty ? data.InitialFlags(plan.Bucket) : trace[^1].After;

                return new StudioFallbackPreview(plan, trace, PlanExplain.LoopOf(plan, final), string.Empty);
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException)
            {
                return new StudioFallbackPreview(null, [], new LoopVerdict(false, ex.Message), ex.Message);
            }
        }
    }

    /// <summary>스텝 편집기가 고를 수 있는 값들 (T11).</summary>
    /// <param name="archetypeId">직업 id.</param>
    public StudioStepChoices LoadStepChoices(string archetypeId)
    {
        lock (_gate)
        {
            MasterDataSet data = MasterDataLoader.Load(_directory);

            if (!data.Archetypes.TryGet(archetypeId, out ArchetypeDef def))
            {
                throw new ArgumentException($"아키타입 '{archetypeId}' 이 없다.", nameof(archetypeId));
            }

            return new StudioStepChoices(
                [.. data.Actions.Actions.Where(a => def.Allows(a.Code)).Select(a => new StudioActionInfo(
                    a.Id,
                    Lexicon.Action(a.Id),
                    a.DefaultTimeoutSeconds,
                    [.. a.Params.Select(p => new StudioParamInfo(
                        p.Name, p.Type.ToString(), p.Required, p.EnumValues, p.Min, p.Max))]))],
                [.. PoiSymbols.Names.Skip(1).Select(s => new StudioSymbolInfo(
                    s,
                    Lexicon.Of(PoiSymbols.TryParse(s, out PoiSymbol symbol) ? symbol : PoiSymbol.None),
                    PoiSymbols.TryParse(s, out PoiSymbol parsed) && data.CanBindSymbol(def.Code, parsed)))],
                [.. data.Items.Items.Select(i => i.Id).Order(StringComparer.Ordinal)],
                [.. data.Items.Recipes.Select(r => r.Id).Order(StringComparer.Ordinal)]);
        }
    }

    private (string Item, string Source) FallbackItem(string archetypeId)
    {
        MasterDataSet data = MasterDataLoader.Load(_directory);

        if (!data.Archetypes.TryGet(archetypeId, out ArchetypeDef def))
        {
            throw new ArgumentException($"아키타입 '{archetypeId}' 이 없다.", nameof(archetypeId));
        }

        string source = Read("fallback_plans.json");
        (int start, int end) = JsonSurgeon.ItemRange(source, "plans", "id", def.FallbackPlanId);

        return (source[start..end], source);
    }

    private string FallbackCandidate(StudioFallbackForm form)
    {
        string source = Read("fallback_plans.json");
        (int start, int end) = JsonSurgeon.ItemRange(source, "plans", "id", form.Id);
        string item = source[start..end];
        string indent = StudioJsonFormat.IndentOf(item, "id", "      ");

        item = JsonSurgeon.SetOrAddTopLevel(item, "goal", StudioJsonFormat.Text(form.Goal), "steps");
        item = JsonSurgeon.SetOrAddTopLevel(
            item,
            "steps",
            StudioJsonFormat.Raw(form.Steps.Select(step => StepJson(step, indent + "  ")), indent),
            "loop");

        return source[..start] + item + source[end..];
    }

    /// <summary>스텝 한 줄. 키 순서는 <c>action → args → timeout_s</c> 다.</summary>
    private static string StepJson(StudioPlanStep step, string indent)
    {
        // `args` 는 비어 있어도 쓴다 — 스키마가 객체를 요구한다 (V1.SCHEMA "스텝에 args 객체가 없다").
        var fields = new List<(string, string)>(3)
        {
            ("action", StudioJsonFormat.Text(step.Action)),
            ("args", StudioJsonFormat.Object(step.Args.Select(a => (a.Name, a.Raw)), indent + "  ")),
            ("timeout_s", StudioJsonFormat.Number(step.TimeoutSeconds)),
        };

        return StudioJsonFormat.Object(fields, indent);
    }

    private static StudioFallbackForm FallbackOf(string item)
    {
        using JsonDocument document = JsonDocument.Parse(item);
        JsonElement root = document.RootElement;

        var steps = ImmutableArray.CreateBuilder<StudioPlanStep>();

        if (root.TryGetProperty("steps", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement step in list.EnumerateArray())
            {
                // 파일에 적힌 순서를 그대로 둔다 — 정렬하면 무변경 저장이 바이트 동일이 아니게 된다.
                ImmutableArray<StudioArg> args = step.TryGetProperty("args", out JsonElement a)
                        && a.ValueKind == JsonValueKind.Object
                    ? [.. a.EnumerateObject().Select(p => new StudioArg(p.Name, p.Value.GetRawText()))]
                    : [];

                steps.Add(new StudioPlanStep(
                    step.GetProperty("action").GetString() ?? string.Empty,
                    args,
                    step.TryGetProperty("timeout_s", out JsonElement t) ? t.GetInt32() : 0));
            }
        }

        return new StudioFallbackForm(
            root.GetProperty("id").GetString()!,
            root.TryGetProperty("archetype", out JsonElement arch) ? arch.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("goal", out JsonElement goal) ? goal.GetString() ?? string.Empty : string.Empty,
            steps.ToImmutable(),
            root.TryGetProperty("loop", out JsonElement loop) && loop.GetBoolean(),
            root.TryGetProperty("on_step_fail", out JsonElement fail) ? fail.GetString() ?? "skip" : "skip");
    }

    /// <summary>
    /// 인구 재배분 안을 적용한다 (T10). <b>여러 줄을 한 트랜잭션에서 고친다</b> —
    /// 절반만 적용되면 가중치 합이 1.0 이 아닌 상태로 남는다.
    /// </summary>
    /// <param name="proposal">고른 안.</param>
    /// <param name="skip">폼이 따로 들고 있는 아키타입 id. 이 줄은 건드리지 않는다.</param>
    public StudioSaveResult ApplyRebalance(RebalanceProposal proposal, string skip)
    {
        EnsureWritable();

        lock (_gate)
        {
            string source = Read("archetypes.json");

            foreach (WeightChange change in proposal.Changes)
            {
                if (string.Equals(change.Archetype, skip, StringComparison.Ordinal)
                    || Math.Abs(change.From - change.To) <= 1e-9)
                {
                    continue;
                }

                source = JsonSurgeon.SetInArrayItem(
                    source, "archetypes", "id", change.Archetype, "population_weight", StudioJsonFormat.Weight(change.To));
            }

            return ValidateAndWrite(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["archetypes.json"] = source,
            });
        }
    }

    private string ItemJson(string id)
    {
        string source = Read("archetypes.json");
        (int start, int end) = JsonSurgeon.ItemRange(source, "archetypes", "id", id);

        return source[start..end];
    }

    private static StudioArchetypeForm FormOf(string item)
    {
        using JsonDocument document = JsonDocument.Parse(item);
        JsonElement root = document.RootElement;

        JsonElement traits = root.TryGetProperty("traits", out JsonElement t) ? t : default;

        return new StudioArchetypeForm(
            root.GetProperty("id").GetString()!,
            root.TryGetProperty("desc", out JsonElement desc) ? desc.GetString() ?? string.Empty : string.Empty,
            Strings(root, "allowed_actions"),
            root.TryGetProperty("home_poi_type", out JsonElement home) ? home.GetString() ?? "house" : "house",
            root.TryGetProperty("workplace_poi_type", out JsonElement work) && work.ValueKind == JsonValueKind.String
                ? work.GetString()
                : null,
            Strings(root, "primary_recipes"),
            Trait(traits, "diligence"),
            Trait(traits, "sociability"),
            Trait(traits, "courage"),
            Trait(traits, "greed"),
            Strings(root, "default_goals"),
            Inventory(root),
            Duty(root),
            root.TryGetProperty("combat_capable", out JsonElement combat) && combat.GetBoolean(),
            root.TryGetProperty("population_weight", out JsonElement weight) ? weight.GetDouble() : 0);
    }

    /// <summary>
    /// 폼과 원문을 비교해 <b>바뀐 필드만</b> 고친다. 무변경이면 원문을 그대로 돌려준다 —
    /// <c>SaveArchetypeForm_WithoutChanges_IsByteIdentical</c> 이 그것을 강제한다.
    /// </summary>
    private static string ApplyForm(string item, StudioArchetypeForm form)
    {
        StudioArchetypeForm before = FormOf(item);
        string indent = StudioJsonFormat.IndentOf(item, "id", "      ");

        if (!string.Equals(before.Desc, form.Desc, StringComparison.Ordinal))
        {
            item = JsonSurgeon.SetOrAddTopLevel(item, "desc", StudioJsonFormat.Text(form.Desc), "allowed_actions");
        }

        if (!before.AllowedActions.SequenceEqual(form.AllowedActions, StringComparer.Ordinal))
        {
            item = JsonSurgeon.SetOrAddTopLevel(
                item, "allowed_actions", StudioJsonFormat.Strings(form.AllowedActions, indent), "home_poi_type");
        }

        if (!string.Equals(before.HomePoiType, form.HomePoiType, StringComparison.Ordinal))
        {
            item = JsonSurgeon.SetOrAddTopLevel(
                item, "home_poi_type", StudioJsonFormat.Text(form.HomePoiType), "workplace_poi_type");
        }

        if (!string.Equals(before.WorkplacePoiType, form.WorkplacePoiType, StringComparison.Ordinal))
        {
            item = form.WorkplacePoiType is { Length: > 0 } workplace
                ? JsonSurgeon.SetOrAddTopLevel(item, "workplace_poi_type", StudioJsonFormat.Text(workplace), "primary_recipes")
                : JsonSurgeon.SetOrAddTopLevel(item, "workplace_poi_type", "null", "primary_recipes");
        }

        if (!before.PrimaryRecipes.SequenceEqual(form.PrimaryRecipes, StringComparer.Ordinal))
        {
            item = JsonSurgeon.SetOrAddTopLevel(
                item, "primary_recipes", StudioJsonFormat.Strings(form.PrimaryRecipes, indent), "traits");
        }

        if (before.Diligence != form.Diligence || before.Sociability != form.Sociability
            || before.Courage != form.Courage || before.Greed != form.Greed)
        {
            item = JsonSurgeon.SetOrAddTopLevel(
                item,
                "traits",
                StudioJsonFormat.Object(
                    [
                        ("diligence", StudioJsonFormat.Number(form.Diligence)),
                        ("sociability", StudioJsonFormat.Number(form.Sociability)),
                        ("courage", StudioJsonFormat.Number(form.Courage)),
                        ("greed", StudioJsonFormat.Number(form.Greed)),
                    ],
                    indent),
                "default_goals");
        }

        if (!before.DefaultGoals.SequenceEqual(form.DefaultGoals, StringComparer.Ordinal))
        {
            item = JsonSurgeon.SetOrAddTopLevel(
                item, "default_goals", StudioJsonFormat.Strings(form.DefaultGoals, indent), "initial_inventory");
        }

        if (!before.InitialInventory.SequenceEqual(form.InitialInventory))
        {
            item = JsonSurgeon.SetOrAddTopLevel(
                item,
                "initial_inventory",
                StudioJsonFormat.Raw(
                    form.InitialInventory.Select(line => StudioJsonFormat.Object(
                        [("item", StudioJsonFormat.Text(line.Item)), ("count", StudioJsonFormat.Number(line.Count))],
                        indent + "  ")),
                    indent),
                "duty_hours");
        }

        if (!before.DutyHours.SequenceEqual(form.DutyHours))
        {
            item = form.DutyHours.IsEmpty
                ? JsonSurgeon.RemoveTopLevel(item, "duty_hours")
                : JsonSurgeon.SetOrAddTopLevel(
                    item,
                    "duty_hours",
                    StudioJsonFormat.Strings(form.DutyHours.Select(d => d.ToString()), indent),
                    "fallback_plan");
        }

        if (before.CombatCapable != form.CombatCapable)
        {
            item = JsonSurgeon.SetOrAddTopLevel(
                item, "combat_capable", StudioJsonFormat.Bool(form.CombatCapable), "population_weight");
        }

        if (Math.Abs(before.PopulationWeight - form.PopulationWeight) > 1e-9)
        {
            item = JsonSurgeon.SetOrAddTopLevel(
                item, "population_weight", StudioJsonFormat.Weight(form.PopulationWeight));
        }

        return item;
    }

    private static ImmutableArray<string> Strings(JsonElement root, string property) =>
        root.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Select(e => e.GetString() ?? string.Empty).Where(s => s.Length > 0)]
            : [];

    private static int Trait(JsonElement traits, string name) =>
        traits.ValueKind == JsonValueKind.Object && traits.TryGetProperty(name, out JsonElement value)
            ? value.GetInt32()
            : 50;

    private static ImmutableArray<StudioInventoryLine> Inventory(JsonElement root) =>
        root.TryGetProperty("initial_inventory", out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Select(e => new StudioInventoryLine(
                e.GetProperty("item").GetString() ?? string.Empty,
                e.TryGetProperty("count", out JsonElement c) ? c.GetInt32() : 1))]
            : [];

    private static ImmutableArray<TimeOfDay> Duty(JsonElement root) =>
        root.TryGetProperty("duty_hours", out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray()
                .Select(e => Enum.TryParse(e.GetString(), out TimeOfDay time) ? (TimeOfDay?)time : null)
                .Where(t => t is not null)
                .Select(t => t!.Value)]
            : [];

    /// <summary>
    /// 마법사가 만든 새 직업을 한 트랜잭션으로 넣는다 (T12).
    ///
    /// <para>
    /// <b>중간에 깨진 상태를 만들지 않는다</b> — 직업만 있고 하루 일과가 없으면 V7 로 기동이 막히고,
    /// 버킷 수를 안 올리면 V6 이 걸린다. 표시 이름(V14)까지 같이 넣는다.
    /// </para>
    /// </summary>
    /// <param name="draft">마법사 초안.</param>
    public StudioSaveResult CreateArchetype(StudioNewArchetype draft)
    {
        EnsureWritable();
        ArgumentNullException.ThrowIfNull(draft);
        ValidateNewId(draft.Id);

        lock (_gate)
        {
            MasterDataSet data = MasterDataLoader.Load(_directory);

            if (data.Archetypes.TryGet(draft.Id, out _))
            {
                throw new ArgumentException($"아키타입 '{draft.Id}' 이 이미 있다.", nameof(draft));
            }

            if (!data.Archetypes.TryGet(draft.From, out ArchetypeDef source))
            {
                throw new ArgumentException($"복제할 아키타입 '{draft.From}' 이 없다.", nameof(draft));
            }

            string archetypes = Read("archetypes.json");
            (int start, int end) = JsonSurgeon.ItemRange(archetypes, "archetypes", "id", draft.From);
            string item = archetypes[start..end];
            int code = CodeAllocator.Next(_directory, "archetypes.json");

            item = JsonSurgeon.SetTopLevel(item, "id", StudioJsonFormat.Text(draft.Id));
            item = JsonSurgeon.SetTopLevel(item, "code", StudioJsonFormat.Number(code));
            item = JsonSurgeon.SetTopLevel(item, "name_key", StudioJsonFormat.Text("npc." + draft.Id));
            item = JsonSurgeon.SetTopLevel(item, "desc", StudioJsonFormat.Text(draft.Description));
            item = JsonSurgeon.SetTopLevel(item, "fallback_plan", StudioJsonFormat.Text("fb_" + draft.Id));
            item = JsonSurgeon.SetOrAddTopLevel(
                item,
                "workplace_poi_type",
                draft.WorkplacePoiType is { Length: > 0 } workplace
                    ? StudioJsonFormat.Text(workplace)
                    : "null",
                "primary_recipes");
            item = JsonSurgeon.SetTopLevel(item, "population_weight", StudioJsonFormat.Weight(draft.Weight));

            // 재배분 안의 다른 줄을 먼저 반영하고, 새 항목을 뒤에 붙인다.
            foreach (WeightChange change in draft.Rebalance)
            {
                if (string.Equals(change.Archetype, draft.Id, StringComparison.Ordinal)
                    || Math.Abs(change.From - change.To) <= 1e-9)
                {
                    continue;
                }

                archetypes = JsonSurgeon.SetInArrayItem(
                    archetypes, "archetypes", "id", change.Archetype,
                    "population_weight", StudioJsonFormat.Weight(change.To));
            }

            archetypes = JsonSurgeon.AppendToArray(archetypes, "archetypes", StudioJsonFormat.Dedent(item));

            string fallbacks = Read("fallback_plans.json");
            (start, end) = JsonSurgeon.ItemRange(fallbacks, "plans", "id", source.FallbackPlanId);
            string plan = fallbacks[start..end];
            plan = JsonSurgeon.SetTopLevel(plan, "id", StudioJsonFormat.Text("fb_" + draft.Id));
            plan = JsonSurgeon.SetTopLevel(plan, "archetype", StudioJsonFormat.Text(draft.Id));
            fallbacks = JsonSurgeon.AppendToArray(fallbacks, "plans", StudioJsonFormat.Dedent(plan));

            string buckets = JsonSurgeon.SetTopLevel(
                Read("context_buckets.json"),
                "total_keys",
                StudioJsonFormat.Number((data.Archetypes.Count + 1) * BucketSpace.PerArchetype));

            var candidates = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["archetypes.json"] = archetypes,
                ["fallback_plans.json"] = fallbacks,
                ["context_buckets.json"] = buckets,
            };

            // 근무 허가를 같이 준다. 원본의 하루 일과가 $workplace·$nearest_field 를 쓰는데
            // 그 장소들의 allowed_archetypes 에 새 직업이 없으면 V3.UNREACHABLE_POI 로 기동이 막힌다 —
            // 사람이 손으로 해야 했던 일이고, 빠뜨리면 "만들었는데 안 돈다" 가 된다.
            if (Permit(data, draft, source) is { Length: > 0 } pois)
            {
                candidates["pois.json"] = pois;
            }

            // 표시 이름이 없으면 V14 가 잡는다 — 마법사가 받은 이름을 그대로 넣는다.
            foreach ((string locale, string name) in draft.Names)
            {
                string path = Path.Combine(
                    _directory, LocalizationTable.FolderName, locale + ".json");

                if (!File.Exists(path))
                {
                    continue;
                }

                candidates[Path.Combine(LocalizationTable.FolderName, locale + ".json")] =
                    JsonSurgeon.SetOrAddTopLevel(File.ReadAllText(path), "npc." + draft.Id, StudioJsonFormat.Text(name));
            }

            return ValidateAndWrite(candidates);
        }
    }

    /// <summary>
    /// 새 직업에게 근무 허가를 준다 (T12). 바꿀 것이 없으면 빈 문자열.
    ///
    /// <para>
    /// 대상은 두 가지다 — 새 일터 유형의 장소, 그리고 <b>복제 원본이 일하던 장소</b>.
    /// 하루 일과를 베껴 왔으므로 <c>$nearest_field</c> 같은 심볼이 원본과 같은 곳을 가리킨다.
    /// </para>
    /// </summary>
    private string Permit(MasterDataSet data, StudioNewArchetype draft, ArchetypeDef source)
    {
        var targets = new HashSet<string>(StringComparer.Ordinal);

        foreach (PoiDef poi in data.Pois.Pois)
        {
            // 제한이 없는 장소는 건드릴 것이 없다 (마스크 0 = 누구나).
            if (poi.AllowedArchetypeMask == 0 || !poi.IsWorkSite)
            {
                continue;
            }

            bool sameType = draft.WorkplacePoiType is { Length: > 0 } type
                && string.Equals(poi.Subtype, type, StringComparison.Ordinal);

            if (sameType || poi.Allows(source.Code))
            {
                _ = targets.Add(poi.Id);
            }
        }

        if (targets.Count == 0)
        {
            return string.Empty;
        }

        string pois = Read("pois.json");
        string indent = StudioJsonFormat.IndentOf(pois, "id", "    ");

        foreach (string id in targets.OrderBy(t => t, StringComparer.Ordinal))
        {
            (int start, int end) = JsonSurgeon.ItemRange(pois, "pois", "id", id);
            string item = pois[start..end];

            if (!JsonSurgeon.HasTopLevel(item, "allowed_archetypes"))
            {
                continue;
            }

            using JsonDocument document = JsonDocument.Parse(item);

            ImmutableArray<string> allowed =
            [
                .. document.RootElement.GetProperty("allowed_archetypes")
                    .EnumerateArray()
                    .Select(e => e.GetString() ?? string.Empty)
                    .Where(v => v.Length > 0),
            ];

            if (allowed.Contains(draft.Id, StringComparer.Ordinal))
            {
                continue;
            }

            item = JsonSurgeon.SetTopLevel(
                item,
                "allowed_archetypes",
                StudioJsonFormat.Strings(allowed.Add(draft.Id), indent + "  "));

            pois = pois[..start] + item + pois[end..];
        }

        return pois;
    }

    /// <summary>
    /// 전역 검색 색인 (T32). 카탈로그를 읽을 때 한 번 만든다 —
    /// <b>NPC 5,000명은 넣지 않는다</b>. 번호는 검색어에서 바로 주소로 만든다.
    /// </summary>
    public ImmutableArray<StudioSearchEntry> SearchIndex()
    {
        lock (_gate)
        {
            MasterDataSet data = MasterDataLoader.Load(_directory);
            var entries = ImmutableArray.CreateBuilder<StudioSearchEntry>(256);

            foreach (ArchetypeDef def in data.Archetypes.Archetypes)
            {
                entries.Add(new StudioSearchEntry(
                    "직업", def.Id, Lexicon.ArchetypeName(data, def.Id), "/archetypes/" + def.Id));
            }

            foreach (ZoneDef zone in data.Zones.Zones)
            {
                entries.Add(new StudioSearchEntry(
                    "지역", zone.Id, Lexicon.Zone(data, zone.Id), "/places/" + zone.Id));
            }

            foreach (PoiDef poi in data.Pois.Pois)
            {
                entries.Add(new StudioSearchEntry(
                    "장소",
                    poi.Id,
                    Lexicon.PlaceName(poi),
                    $"/places/{data.Zones[poi.Zone].Id}?poi={poi.Id}"));
            }

            foreach (InterruptRule rule in data.Interrupts.Rules)
            {
                entries.Add(new StudioSearchEntry("돌발 반응", rule.Id, rule.Id, "/interrupts"));
            }

            foreach (ActionDef action in data.Actions.Actions)
            {
                entries.Add(new StudioSearchEntry("행동", action.Id, Lexicon.Action(action.Id), "/actions"));
            }

            return entries.ToImmutable();
        }
    }

    /// <summary>현재 디스크 상태를 검증한다.</summary>
    public ImmutableArray<StudioIssue> Validate()
    {
        lock (_gate)
        {
            return ToIssues(MasterDataValidator.Validate(_directory));
        }
    }

    /// <summary>
    /// 후보를 검증하고 통과하면 쓴다.
    /// </summary>
    /// <param name="candidates">파일 이름 → 내용.</param>
    /// <param name="loaderGate">
    /// 로더까지 통과시킬 것인가.
    ///
    /// <para>
    /// <b>딱 한 경우만 <c>false</c> 다 — 장소 추가(T13).</b> 장소를 하나 더하면
    /// <c>poi_distances.bin</c> 은 <b>정의상</b> 낡는다 (행렬이 옛 POI 수 기준이다).
    /// 로더는 그것을 정확히 잡아내고, 그래서 저장 자체가 막힌다 — 그런데 그 낡음을 없애려면
    /// 먼저 장소를 저장해야 한다. 순서를 뒤집을 수 없으므로 이 경로만 로더를 건너뛰고,
    /// <b>대신 파급 패널이 "거리표를 다시 만들어라" 를 즉시 띄운다</b> (T16·T17).
    /// V1~V15 검증은 그대로 돈다.
    /// </para>
    /// </param>
    private StudioSaveResult ValidateAndWrite(Dictionary<string, string> candidates, bool loaderGate = true)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "npc-studio-" + Path.GetRandomFileName());
        Directory.CreateDirectory(temporary);

        try
        {
            CopyMasterData(temporary);

            foreach ((string file, string content) in candidates)
            {
                File.WriteAllText(Path.Combine(temporary, file), content, new UTF8Encoding(false));
            }

            MasterDataValidationReport report = MasterDataValidator.Validate(temporary);
            ImmutableArray<StudioIssue> issues = ToIssues(report);

            if (!report.IsValid)
            {
                return new StudioSaveResult(false, "검증 실패로 저장하지 않았다.", issues);
            }

            if (loaderGate)
            {
                try
                {
                    MasterDataSet loaded = MasterDataLoader.Load(temporary);
                    _ = NpcInstanceTable.Load(Path.Combine(temporary, "npc_instances.json"), loaded);
                }
                catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException)
                {
                    return new StudioSaveResult(
                        false,
                        "로더가 후보를 거절해 저장하지 않았다.",
                        [new StudioIssue("LOAD", ex.Message, string.Empty, string.Empty, string.Empty)]);
                }
            }

            foreach ((string file, string content) in candidates)
            {
                string path = Path.Combine(_directory, file);

                // T18 — 덮어쓰기 직전의 원본을 남긴다. 실패해도 저장은 계속한다.
                Backup(path, Path.GetFileName(file));
                AtomicWrite(path, content);
            }

            return new StudioSaveResult(
                true,
                $"{candidates.Count}개 파일을 저장했다.",
                issues,
                [.. candidates.Keys.OrderBy(f => f, StringComparer.Ordinal)]);
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    private void CopyMasterData(string destination) => CopyFrom(_directory, destination);

    /// <summary>마스터데이터 한 벌을 복사한다. <c>localization/</c> 까지 같이 간다 (V14 가 그것을 본다).</summary>
    private static void CopyFrom(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        string localization = Path.Combine(source, LocalizationTable.FolderName);

        if (Directory.Exists(localization))
        {
            string target = Path.Combine(destination, LocalizationTable.FolderName);
            Directory.CreateDirectory(target);

            foreach (string file in Directory.EnumerateFiles(localization))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            }
        }
    }

    private static void AtomicWrite(string path, string content)
    {
        string temporary = path + ".studio.tmp";
        File.WriteAllText(temporary, content, new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    // ------------------------------------------------------------------ 되돌리기 (T18)

    /// <summary>
    /// 백업을 두는 곳. <b><c>masterdata/</c> 안에 두지 않는다</b> —
    /// <c>CopyMasterData</c>·<c>ContentHash</c> 가 폴더 전체를 보므로 백업이 입력으로 섞인다.
    ///
    /// <b>편집 대상 폴더마다 따로 둔다.</b> 한 곳에 모으면 연습장·다른 <c>masterdata/</c> 의
    /// 백업이 같은 목록에 섞이고, 되돌리기가 남의 파일을 덮어쓴다.
    /// </summary>
    private string BackupRoot =>
        Path.Combine(
            options.BackupRoot.Length > 0
                ? options.BackupRoot
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NpcStudio",
                    "backup"),
            FolderKey(_directory));

    /// <summary>백업 보존 기간(일). 지난 것은 저장할 때 지운다.</summary>
    public const int BackupDays = 30;

    /// <summary>되돌릴 수 있는 백업 (최신 순).</summary>
    public ImmutableArray<StudioBackup> Backups()
    {
        lock (_gate)
        {
            string root = BackupRoot;

            if (!Directory.Exists(root))
            {
                return [];
            }

            var list = ImmutableArray.CreateBuilder<StudioBackup>();

            foreach (string folder in Directory.EnumerateDirectories(root))
            {
                string stamp = Path.GetFileName(folder);

                foreach (string file in Directory.EnumerateFiles(folder, "*.json"))
                {
                    list.Add(new StudioBackup(stamp, Path.GetFileName(file), file, StampTime(stamp)));
                }
            }

            // 폴더 이름이 저장한 시각이다. 파일의 수정 시각으로 세우지 않는다 —
            // File.Copy 가 원본의 시각을 그대로 옮기므로 그 값은 "저장한 때" 가 아니다.
            return [.. list.OrderByDescending(b => b.Stamp, StringComparer.Ordinal)];
        }
    }

    /// <summary>
    /// 백업으로 되돌린다. <b>되돌린 상태도 검증한다</b> — 옛 파일이 지금 다른 파일과 맞는다는
    /// 보장이 없다 (그 사이에 장소를 지웠을 수 있다).
    /// </summary>
    /// <param name="backup">되돌릴 백업.</param>
    public StudioSaveResult Restore(StudioBackup backup)
    {
        EnsureWritable();
        ArgumentNullException.ThrowIfNull(backup);
        EnsureEditable(backup.File);

        lock (_gate)
        {
            return ValidateAndWrite(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [backup.File] = File.ReadAllText(backup.Path),
            });
        }
    }

    /// <summary>쓰기 직전의 원본을 백업한다. 실패해도 저장을 막지 않는다 — 백업은 보조다.</summary>
    private void Backup(string path, string file)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            string root = BackupRoot;
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string folder = Path.Combine(root, stamp);

            // 같은 초에 두 번 저장하면 앞의 백업이 덮인다 — 첫 번째 상태로 되돌릴 곳이 없어진다.
            for (int n = 2; File.Exists(Path.Combine(folder, file)); n++)
            {
                folder = Path.Combine(root, string.Create(CultureInfo.InvariantCulture, $"{stamp}-{n}"));
            }

            Directory.CreateDirectory(folder);
            File.Copy(path, Path.Combine(folder, file), overwrite: true);

            Prune(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 백업을 못 만들어도 저장은 계속한다.
        }
    }

    private static void Prune(string root)
    {
        DateTime cutoff = DateTime.Now.AddDays(-BackupDays);

        foreach (string folder in Directory.EnumerateDirectories(root))
        {
            if (Directory.GetLastWriteTime(folder) < cutoff)
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    /// <summary>폴더 경로를 파일 이름으로 쓸 수 있는 짧은 키로 바꾼다.</summary>
    private static string FolderKey(string directory)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)).ToUpperInvariant();
        byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(full));

        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    /// <summary>백업 폴더 이름에서 저장 시각을 읽는다. 읽지 못하면 <see cref="DateTime.MinValue"/>.</summary>
    private static DateTime StampTime(string stamp) =>
        stamp.Length >= 15
        && DateTime.TryParseExact(
            stamp[..15], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime at)
            ? at
            : DateTime.MinValue;

    private static void EnsureItemId(string json, string expected)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("id", out JsonElement id)
            || !string.Equals(id.GetString(), expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"편집 중에는 id를 바꿀 수 없다. 예상 id는 '{expected}'이다.");
        }
    }

    private static void ValidateNewId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)
            || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_'))
            || char.IsDigit(id[0]))
        {
            throw new ArgumentException("id는 영문자로 시작하는 영문 소문자·숫자·밑줄 조합이어야 한다.", nameof(id));
        }

        if (id.Any(char.IsUpper))
        {
            throw new ArgumentException("id에는 대문자를 쓸 수 없다.", nameof(id));
        }
    }

    private void EnsureWritable()
    {
        if (options.ReadOnly)
        {
            throw new InvalidOperationException("읽기 전용 모드에서는 저장할 수 없다.");
        }
    }

    private static void EnsureEditable(string fileName)
    {
        if (!s_editableFiles.Contains(fileName) || Path.GetFileName(fileName) != fileName)
        {
            throw new ArgumentException($"Studio에서 편집할 수 없는 파일이다: {fileName}", nameof(fileName));
        }
    }

    private string Read(string fileName) => File.ReadAllText(Path.Combine(_directory, fileName));

    private HashSet<int> LoadOverrideIds()
    {
        using JsonDocument document = JsonDocument.Parse(Read(NpcInstanceTable.OverrideFileName));
        var ids = new HashSet<int>();

        if (document.RootElement.TryGetProperty("overrides", out JsonElement overrides))
        {
            foreach (JsonElement item in overrides.EnumerateArray())
            {
                if (item.TryGetProperty("id", out JsonElement id) && id.TryGetInt32(out int value))
                {
                    ids.Add(value);
                }
            }
        }

        return ids;
    }

    private static JsonObject? FindOverride(JsonArray overrides, int id) =>
        overrides.OfType<JsonObject>().FirstOrDefault(item => item["id"]?.GetValue<int>() == id);

    private static string PoiTypeLabel(PoiType type) => type switch
    {
        PoiType.Home => "집",
        PoiType.Workplace => "일터",
        PoiType.Market => "시장",
        PoiType.Tavern => "선술집",
        PoiType.Temple => "신전",
        PoiType.Gate => "성문",
        PoiType.Field => "농경지",
        PoiType.Wilderness => "야외",
        _ => type.ToString(),
    };

    private static string Text(string value) => JsonSerializer.Serialize(value, JsonSurgeonText.Options);

    private static ImmutableArray<StudioIssue> ToIssues(MasterDataValidationReport report) =>
    [
        .. report.Violations.Select(v => new StudioIssue(v.Code, v.Detail, v.File, v.Path, v.FixHint)),
    ];
}

/// <summary>목록 화면 데이터.</summary>
public sealed record StudioCatalog(
    string Directory,
    bool ReadOnly,
    ImmutableArray<StudioArchetype> Archetypes,
    int InstanceCount,
    ImmutableArray<StudioIssue> Issues)
{
    /// <summary>장소 수. 시작 화면의 "마을 한눈에" 타일이 쓴다.</summary>
    public int PoiCount { get; init; }

    /// <summary>지역 수.</summary>
    public int ZoneCount { get; init; }

    /// <summary>돌발 반응 규칙 수.</summary>
    public int InterruptCount { get; init; }

    /// <summary>행동 수. 상한 40 중 몇 개인지 화면이 적는다.</summary>
    public int ActionCount { get; init; }

    /// <summary>아이템 수.</summary>
    public int ItemCount { get; init; }

    /// <summary>낡은 파생물 이름. 비어 있으면 최신이다 (T16·T17).</summary>
    public ImmutableArray<string> StaleArtifacts { get; init; } = [];

    /// <summary>연습장 이름 (T30). 원본을 보고 있으면 빈 문자열이다.</summary>
    public string Sandbox { get; init; } = string.Empty;

    /// <summary>지금 보고 있는 것이 연습장인가.</summary>
    public bool IsSandbox => Sandbox.Length > 0;
}

/// <summary>아키타입 목록 한 줄.</summary>
public sealed record StudioArchetype(
    string Id,
    int Code,
    string Description,
    double Weight,
    int Population,
    string Workplace,
    bool CombatCapable);

/// <summary>편집 대상과 설명 카드.</summary>
public sealed record StudioArchetypeDocument(string Id, string Json, string Card);

/// <summary>
/// 아키타입 개요 보기 한 장 (T05). JSON 원문 대신 이 값들이 섹션 카드가 된다.
/// </summary>
/// <param name="Facts">정의에서 뽑은 사실들.</param>
/// <param name="Name">한국어 표시 이름.</param>
/// <param name="Group">직군.</param>
/// <param name="Fallback">하루 일과의 스텝 판정.</param>
/// <param name="Loop">고리 판정.</param>
/// <param name="ByZone">지역별 인구 (없으면 예측).</param>
/// <param name="Lint">건강 진단 (T29).</param>
public sealed record StudioArchetypeView(
    ArchetypeFacts Facts,
    string Name,
    Lexicon.ArchetypeGroup Group,
    ImmutableArray<StepTrace> Fallback,
    LoopVerdict Loop,
    ImmutableArray<StudioZoneCount> ByZone,
    ImmutableArray<LintFinding> Lint)
{
    /// <summary>아키타입 id.</summary>
    public string Id => Facts.Def.Id;

    /// <summary>직군 색 (부록 E).</summary>
    public string Color => Lexicon.ColorOf(Group);
}

/// <summary>되돌릴 수 있는 백업 하나 (T18).</summary>
/// <param name="Stamp">백업 시각 폴더 이름.</param>
/// <param name="File">파일 이름.</param>
/// <param name="Path">백업 파일 경로.</param>
/// <param name="SavedAt">백업된 시각.</param>
public sealed record StudioBackup(string Stamp, string File, string Path, DateTime SavedAt);

/// <summary>전역 검색 결과 한 줄 (T32).</summary>
/// <param name="Kind">무엇인가 (직업·지역·장소·행동·돌발 반응).</param>
/// <param name="Key">id.</param>
/// <param name="Label">표시 이름.</param>
/// <param name="Url">갈 곳.</param>
public sealed record StudioSearchEntry(string Kind, string Key, string Label, string Url);

/// <summary>새 직업 마법사의 초안 (T12).</summary>
/// <param name="Id">새 직업 id.</param>
/// <param name="Description">설명. LLM 프롬프트에 그대로 실린다.</param>
/// <param name="From">복제 원본 id.</param>
/// <param name="Weight">인구 비율.</param>
/// <param name="WorkplacePoiType">일터 세부 유형. 없으면 null.</param>
/// <param name="Rebalance">인구를 어디서 뗄 것인가.</param>
/// <param name="Names">로케일 → 표시 이름 (V14).</param>
public sealed record StudioNewArchetype(
    string Id,
    string Description,
    string From,
    double Weight,
    string? WorkplacePoiType,
    ImmutableArray<WeightChange> Rebalance,
    ImmutableArray<(string Locale, string Name)> Names);

/// <summary>하루 일과 초안의 판정 (T11).</summary>
/// <param name="Plan">컴파일된 플랜. 실패면 null.</param>
/// <param name="Trace">스텝별 판정.</param>
/// <param name="Loop">고리 판정.</param>
/// <param name="Error">컴파일 실패 사유. 성공이면 빈 문자열.</param>
public sealed record StudioFallbackPreview(
    CompiledPlan? Plan, ImmutableArray<StepTrace> Trace, LoopVerdict Loop, string Error);

/// <summary>스텝 편집기가 고를 수 있는 값들 (T11).</summary>
/// <param name="Actions">이 직업이 쓸 수 있는 행동.</param>
/// <param name="Symbols">POI 심볼과 바인딩 가능 여부.</param>
/// <param name="Items">아이템 id.</param>
/// <param name="Recipes">레시피 id.</param>
public sealed record StudioStepChoices(
    ImmutableArray<StudioActionInfo> Actions,
    ImmutableArray<StudioSymbolInfo> Symbols,
    ImmutableArray<string> Items,
    ImmutableArray<string> Recipes);

/// <summary>스텝 편집기가 보는 행동 하나 (T11).</summary>
/// <param name="Id">액션 id.</param>
/// <param name="Name">한국어 표기.</param>
/// <param name="DefaultTimeoutSeconds">기본 최대 시간.</param>
/// <param name="Params">인자 정의.</param>
public sealed record StudioActionInfo(
    string Id, string Name, int DefaultTimeoutSeconds, ImmutableArray<StudioParamInfo> Params);

/// <summary>행동 인자 하나 (T11).</summary>
/// <param name="Name">인자 이름.</param>
/// <param name="Type">타입 이름 (<c>PoiRef</c>·<c>ItemRef</c>·<c>Enum</c>·<c>Int</c>…).</param>
/// <param name="Required">필수인가.</param>
/// <param name="EnumValues">열거 값.</param>
/// <param name="Min">정수 하한.</param>
/// <param name="Max">정수 상한.</param>
public sealed record StudioParamInfo(
    string Name, string Type, bool Required, ImmutableArray<string> EnumValues, int Min, int Max);

/// <summary>POI 심볼 하나 (T11). <b>바인딩 못 하는 심볼은 회색으로 보여 준다</b> — 고를 수는 없다.</summary>
/// <param name="Symbol">심볼 문자열 (<c>$home</c>).</param>
/// <param name="Name">한국어 표기.</param>
/// <param name="Bindable">이 직업이 붙일 수 있는가.</param>
public sealed record StudioSymbolInfo(string Symbol, string Name, bool Bindable);

/// <summary>폼 편집기가 고를 수 있는 값들 (T10). 전부 마스터데이터에서 온다.</summary>
/// <param name="HomeTypes">집 세부 유형.</param>
/// <param name="WorkplaceTypes">일터 세부 유형.</param>
/// <param name="Recipes">레시피 id.</param>
/// <param name="Items">아이템 id.</param>
/// <param name="Actions">액션 id (code 순).</param>
public sealed record StudioArchetypeChoices(
    ImmutableArray<string> HomeTypes,
    ImmutableArray<string> WorkplaceTypes,
    ImmutableArray<string> Recipes,
    ImmutableArray<string> Items,
    ImmutableArray<string> Actions);

/// <summary>지역별 인구 한 줄 (T33).</summary>
/// <param name="ZoneId">지역 id.</param>
/// <param name="ZoneName">지역 표시 이름.</param>
/// <param name="Count">인원.</param>
/// <param name="Predicted">명단에서 센 것이 아니라 예측인가.</param>
public sealed record StudioZoneCount(string ZoneId, string ZoneName, int Count, bool Predicted);

/// <summary>
/// NPC 한 명의 개요 (T07). "누구인가" 문장과 지도를 그릴 좌표가 같이 온다.
/// </summary>
/// <param name="Npc">인스턴스.</param>
/// <param name="Facts">거리·출입 계산.</param>
/// <param name="Sentence">누구인가 한 문장.</param>
/// <param name="ArchetypeName">직업 표시 이름.</param>
/// <param name="ZoneName">지역 표시 이름.</param>
/// <param name="Group">직군.</param>
/// <param name="ZonePois">같은 지역의 장소 (지도·순찰로 선택기).</param>
/// <param name="PatrolRoute">순찰로 POI id.</param>
public sealed record StudioNpcOverview(
    NpcInstanceDef Npc,
    InstanceFacts Facts,
    string Sentence,
    string ArchetypeName,
    string ZoneName,
    Lexicon.ArchetypeGroup Group,
    ImmutableArray<StudioPoiChoice> ZonePois,
    ImmutableArray<string> PatrolRoute)
{
    /// <summary>집 POI id.</summary>
    public string HomeId => Facts.Home.Id;

    /// <summary>일터 POI id. 없으면 빈 문자열.</summary>
    public string WorkplaceId => Facts.Workplace?.Id ?? string.Empty;

    /// <summary>직군 색.</summary>
    public string Color => Lexicon.ColorOf(Group);
}

/// <summary>개별 NPC 탐색 목록에 필요한 읽기 전용 요약.</summary>
public sealed record StudioNpcSummary(
    int Id,
    string Archetype,
    string Zone,
    string Home,
    string Workplace,
    string Faction,
    bool HasOverride);

/// <summary>개별 NPC 오버라이드 편집 화면 데이터.</summary>
public sealed record StudioNpcOverrideEditor(
    int Id,
    bool Exists,
    ImmutableArray<string> PatrolRoute,
    int? AggroRadiusM,
    string Faction,
    string DialogueProfile,
    int? ScheduleOffsetMinutes,
    ImmutableArray<string> Factions,
    ImmutableArray<StudioPoiChoice> ZonePois);

/// <summary>순찰 경로 선택기와 지역 지도에 표시할 같은 지역 POI 설명.</summary>
/// <param name="Id">POI id.</param>
/// <param name="Type">유형 한국어 이름.</param>
/// <param name="Subtype">세부 유형 id.</param>
/// <param name="Capacity">정원.</param>
/// <param name="X">지역 중심 기준 가로 좌표.</param>
/// <param name="Z">지역 중심 기준 세로 좌표.</param>
/// <param name="IsHome">이 NPC 의 집인가.</param>
/// <param name="IsWorkplace">이 NPC 의 일터인가.</param>
public sealed record StudioPoiChoice(
    string Id,
    string Type,
    string Subtype,
    int Capacity,
    float X,
    float Z,
    bool IsHome,
    bool IsWorkplace)
{
    /// <summary>유형. 지도 점 색이 이것으로 갈린다 (T07).</summary>
    public PoiType Kind { get; init; }

    /// <summary>사람이 읽는 장소 이름 ("집 #12").</summary>
    public string Label { get; init; } = string.Empty;
}

/// <summary>개별 NPC 오버라이드 저장 초안.</summary>
public sealed record StudioNpcOverrideDraft(
    int Id,
    ImmutableArray<string> PatrolRoute,
    int? AggroRadiusM,
    string Faction,
    string DialogueProfile,
    int? ScheduleOffsetMinutes);

/// <summary>검증 문제.</summary>
public sealed record StudioIssue(string Code, string Detail, string File, string Path, string FixHint);

/// <summary>저장 결과.</summary>
/// <param name="Saved">저장했는가.</param>
/// <param name="Message">사람이 읽는 결과 문구.</param>
/// <param name="Issues">저장 후보를 검증한 결과.</param>
/// <param name="Files">
/// 실제로 쓰인 파일 이름. 파급 패널(T16)이 이것을 모아 "이제 무엇을 해야 하나" 를 낸다 —
/// 세션이 파일 목록을 모르면 <c>ImpactAnalyzer</c> 에게 물어볼 것이 없다.
/// </param>
public sealed record StudioSaveResult(
    bool Saved,
    string Message,
    ImmutableArray<StudioIssue> Issues,
    ImmutableArray<string> Files = default)
{
    /// <summary>쓰인 파일. 기본값이면 빈 배열이다.</summary>
    public ImmutableArray<string> Files { get; init; } = Files.IsDefault ? [] : Files;
}
