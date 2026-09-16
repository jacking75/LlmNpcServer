using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
                        p.Code == npc.Workplace))]);
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

    /// <summary>
    /// 기존 아키타입을 복제해 새 직업을 만든다. 원본에서 인구를 나누므로 가중치 합과 같은 일터 정원을 보존한다.
    /// 폴백 플랜과 버킷 수도 같은 트랜잭션에서 만든다.
    /// </summary>
    public StudioSaveResult CreateArchetype(string id, string fromId, double weight)
    {
        EnsureWritable();
        ValidateNewId(id);

        lock (_gate)
        {
            MasterDataSet data = MasterDataLoader.Load(_directory);

            if (!data.Archetypes.TryGet(fromId, out ArchetypeDef sourceDef))
            {
                throw new ArgumentException($"복제할 아키타입 '{fromId}'이 없다.", nameof(fromId));
            }

            if (data.Archetypes.TryGet(id, out _))
            {
                throw new ArgumentException($"아키타입 '{id}'이 이미 있다.", nameof(id));
            }

            if (weight <= 0 || weight >= sourceDef.PopulationWeight)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(weight),
                    $"가중치는 0보다 크고 복제 원본 {fromId}의 {sourceDef.PopulationWeight:F4}보다 작아야 한다.");
            }

            string archetypes = Read("archetypes.json");
            (int start, int end) = JsonSurgeon.ItemRange(archetypes, "archetypes", "id", fromId);
            string draft = archetypes[start..end];
            int code = CodeAllocator.Next(_directory, "archetypes.json");

            draft = JsonSurgeon.SetTopLevel(draft, "id", Text(id));
            draft = JsonSurgeon.SetTopLevel(draft, "code", code.ToString(CultureInfo.InvariantCulture));
            draft = JsonSurgeon.SetTopLevel(draft, "name_key", Text("npc." + id));
            draft = JsonSurgeon.SetTopLevel(
                draft,
                "desc",
                Text($"TODO: {id}의 설명을 쓴다. 프롬프트에 그대로 실린다."));
            draft = JsonSurgeon.SetTopLevel(draft, "fallback_plan", Text("fb_" + id));
            draft = JsonSurgeon.SetTopLevel(
                draft,
                "population_weight",
                weight.ToString("0.####", CultureInfo.InvariantCulture));

            double sourceWeight = sourceDef.PopulationWeight - weight;
            archetypes = JsonSurgeon.SetInArrayItem(
                archetypes,
                "archetypes",
                "id",
                fromId,
                "population_weight",
                sourceWeight.ToString("0.####", CultureInfo.InvariantCulture));
            archetypes = JsonSurgeon.AppendToArray(archetypes, "archetypes", draft);

            string fallbacks = Read("fallback_plans.json");
            (start, end) = JsonSurgeon.ItemRange(
                fallbacks,
                "plans",
                "id",
                sourceDef.FallbackPlanId);
            string fallback = fallbacks[start..end];
            fallback = JsonSurgeon.SetTopLevel(fallback, "id", Text("fb_" + id));
            fallback = JsonSurgeon.SetTopLevel(fallback, "archetype", Text(id));
            fallbacks = JsonSurgeon.AppendToArray(fallbacks, "plans", fallback);

            string buckets = Read("context_buckets.json");
            buckets = JsonSurgeon.SetTopLevel(
                buckets,
                "total_keys",
                ((data.Archetypes.Count + 1) * BucketSpace.PerArchetype).ToString(CultureInfo.InvariantCulture));

            return ValidateAndWrite(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["archetypes.json"] = archetypes,
                ["fallback_plans.json"] = fallbacks,
                ["context_buckets.json"] = buckets,
            });
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

    private StudioSaveResult ValidateAndWrite(Dictionary<string, string> candidates)
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

            try
            {
                _ = MasterDataLoader.Load(temporary);
                _ = NpcInstanceTable.Load(Path.Combine(temporary, "npc_instances.json"), MasterDataLoader.Load(temporary));
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException)
            {
                return new StudioSaveResult(
                    false,
                    "로더가 후보를 거절해 저장하지 않았다.",
                    [new StudioIssue("LOAD", ex.Message, string.Empty, string.Empty, string.Empty)]);
            }

            foreach ((string file, string content) in candidates)
            {
                AtomicWrite(Path.Combine(_directory, file), content);
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

/// <summary>순찰 경로 선택기에 표시할 같은 지역 POI 설명.</summary>
public sealed record StudioPoiChoice(
    string Id,
    string Type,
    string Subtype,
    int Capacity,
    float X,
    float Z,
    bool IsHome,
    bool IsWorkplace);

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
