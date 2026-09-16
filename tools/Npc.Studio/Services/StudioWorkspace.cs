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

    /// <summary>현재 카탈로그와 검증 상태를 읽는다.</summary>
    public StudioCatalog LoadCatalog()
    {
        lock (_gate)
        {
            MasterDataSet data = MasterDataLoader.Load(options.MasterData);
            MasterDataValidationReport report = MasterDataValidator.Validate(options.MasterData);
            string instancesPath = Path.Combine(options.MasterData, "npc_instances.json");
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
                options.MasterData,
                options.ReadOnly,
                archetypes,
                instances,
                ToIssues(report));
        }
    }

    /// <summary>아키타입 한 항목의 원문과 설명 카드를 읽는다.</summary>
    public StudioArchetypeDocument LoadArchetype(string id)
    {
        lock (_gate)
        {
            MasterDataSet data = MasterDataLoader.Load(options.MasterData);
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
            MasterDataSet data = MasterDataLoader.Load(options.MasterData);
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
            MasterDataSet data = MasterDataLoader.Load(options.MasterData);
            NpcInstanceTable instances = NpcInstanceTable.Load(
                Path.Combine(options.MasterData, "npc_instances.json"), data);
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
            MasterDataSet data = MasterDataLoader.Load(options.MasterData);
            NpcInstanceTable instances = NpcInstanceTable.Load(
                Path.Combine(options.MasterData, "npc_instances.json"), data);
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
            MasterDataSet data = MasterDataLoader.Load(options.MasterData);
            NpcInstanceTable instances = NpcInstanceTable.Load(
                Path.Combine(options.MasterData, "npc_instances.json"), data);
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
            MasterDataSet data = MasterDataLoader.Load(options.MasterData);
            NpcInstanceTable instances = NpcInstanceTable.Load(
                Path.Combine(options.MasterData, "npc_instances.json"), data);
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
            MasterDataSet data = MasterDataLoader.Load(options.MasterData);

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
            int code = CodeAllocator.Next(options.MasterData, "archetypes.json");

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
            return ToIssues(MasterDataValidator.Validate(options.MasterData));
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
                AtomicWrite(Path.Combine(options.MasterData, file), content);
            }

            return new StudioSaveResult(true, $"{candidates.Count}개 파일을 저장했다.", issues);
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    private void CopyMasterData(string destination)
    {
        foreach (string file in Directory.EnumerateFiles(options.MasterData))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        string localization = Path.Combine(options.MasterData, "localization");

        if (Directory.Exists(localization))
        {
            string target = Path.Combine(destination, "localization");
            Directory.CreateDirectory(target);

            foreach (string file in Directory.EnumerateFiles(localization))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
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

    private string Read(string fileName) => File.ReadAllText(Path.Combine(options.MasterData, fileName));

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
    ImmutableArray<StudioIssue> Issues);

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
public sealed record StudioSaveResult(bool Saved, string Message, ImmutableArray<StudioIssue> Issues);
