using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
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

/// <summary>검증 문제.</summary>
public sealed record StudioIssue(string Code, string Detail, string File, string Path, string FixHint);

/// <summary>저장 결과.</summary>
public sealed record StudioSaveResult(bool Saved, string Message, ImmutableArray<StudioIssue> Issues);
