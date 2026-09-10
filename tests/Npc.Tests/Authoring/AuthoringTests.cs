using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Npc.MasterData;
using Npc.MasterData.Authoring;
using Npc.MasterData.Validation;

namespace Npc.Tests.Authoring;

/// <summary>
/// F-04 편집 안전장치.
///
/// <b>이 도구들의 유일한 목적은 손으로 하던 계산을 없애는 것이다.</b> 그러니 도구가 낸 결과가
/// 기존 규칙(V5·V10·무효화 표)과 어긋나면 도구가 있는 편이 더 나쁘다 — 사람은 최소한
/// 문서를 다시 읽기라도 했다.
/// </summary>
public sealed class AuthoringTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    // ── CodeAllocator ──────────────────────────────────────────

    [Fact]
    public void CodeAllocator_KnowsEveryNumberedFile()
    {
        foreach (CodeAllocator.Layout layout in CodeAllocator.Layouts)
        {
            Assert.True(
                File.Exists(Path.Combine(TestPaths.MasterData, layout.File)),
                $"{layout.File} 이 없다. Layouts 표가 실제 파일과 어긋났다.");

            int next = CodeAllocator.Next(TestPaths.MasterData, layout.File);

            Assert.True(next >= 0, $"{layout.File}: 다음 번호가 {next} 다.");
        }
    }

    /// <summary>아키타입의 다음 code 는 현재 개수와 같아야 한다 — 0 부터 빈틈없이 이어지므로.</summary>
    [Fact]
    public void CodeAllocator_NextArchetypeIsTheCount()
    {
        Assert.Equal(
            s_data.Archetypes.Count, CodeAllocator.Next(TestPaths.MasterData, "archetypes.json"));

        Assert.Empty(CodeAllocator.Gaps(TestPaths.MasterData, "archetypes.json"));
    }

    /// <summary>
    /// <b>비트는 예약 구간을 먼저 채운다.</b> "최대 + 1" 로 두면 22~23 처럼 중간에 비워 둔
    /// 구간을 영원히 못 쓴다 — 그리고 64비트를 다 쓰면 확장 여지가 없다.
    /// </summary>
    [Fact]
    public void CodeAllocator_FillsReservedBitsFirst()
    {
        ImmutableArray<int> reserved = CodeAllocator.ReservedBits(TestPaths.MasterData, "world_flags.json");

        Assert.NotEmpty(reserved);

        int next = CodeAllocator.Next(TestPaths.MasterData, "world_flags.json");

        Assert.Contains(next, reserved);
        Assert.Equal(reserved.Min(), next);
    }

    /// <summary>예약 구간의 빈칸은 "빠진 번호" 가 아니다 — 일부러 비워 둔 것이다.</summary>
    [Fact]
    public void CodeAllocator_DoesNotReportReservedBitsAsGaps()
    {
        Assert.Empty(CodeAllocator.Gaps(TestPaths.MasterData, "world_flags.json"));
    }

    [Fact]
    public void CodeAllocator_RejectsUnnumberedFile()
    {
        Assert.Null(CodeAllocator.LayoutOf("interrupts.json"));
        Assert.Throws<ArgumentException>(
            () => CodeAllocator.Next(TestPaths.MasterData, "interrupts.json"));
    }

    // ── WeightRebalancer ───────────────────────────────────────

    /// <summary>세 안 모두 V5(합 1.0 ±0.001)를 만족해야 한다. 아니면 제안이 아니라 오답이다.</summary>
    [Fact]
    public void WeightRebalancer_EveryProposalSatisfiesV5()
    {
        // 일터 타입을 주면 세 안이 다 나온다 — 안 주면 같은 계열 안을 만들 수 없다.
        ImmutableArray<RebalanceProposal> proposals =
            WeightRebalancer.Propose(s_data, "beekeeper", 0.004, workplacePoiType: "farm");

        Assert.Equal(Enum.GetValues<RebalanceStrategy>().Length, proposals.Length);

        foreach (RebalanceProposal proposal in proposals)
        {
            Assert.True(
                proposal.IsValid,
                $"{proposal.Strategy}: 합이 {proposal.Sum:F6} 다. 1.0 ±0.001 이어야 한다 (V5).");

            Assert.Contains(proposal.Changes, c => c.Archetype == "beekeeper" && c.To == 0.004);
        }
    }

    /// <summary>제안은 결정론이어야 한다 — 같은 입력에 다른 안이 나오면 검토가 성립하지 않는다.</summary>
    [Fact]
    public void WeightRebalancer_IsDeterministic()
    {
        // ImmutableArray 의 등가는 참조 비교다 — 내용을 문자열로 편다.
        static string[] Flatten(ImmutableArray<RebalanceProposal> proposals) =>
            [.. proposals.SelectMany(
                p => p.Changes.Select(c => $"{p.Strategy} {c.Archetype} {c.From:F6}->{c.To:F6}"))];

        string[] first = Flatten(WeightRebalancer.Propose(s_data, "beekeeper", 0.004, workplacePoiType: "farm"));
        string[] second = Flatten(WeightRebalancer.Propose(s_data, "beekeeper", 0.004, workplacePoiType: "farm"));

        for (int i = 0; i < Math.Min(first.Length, second.Length); i++)
        {
            Assert.Equal(first[i], second[i]);
        }

        Assert.Equal(first.Length, second.Length);

        // 일터 타입을 모르면 같은 계열 안이 빠진다. 던지지 않고 나머지를 낸다.
        Assert.Equal(
            Enum.GetValues<RebalanceStrategy>().Length - 1,
            WeightRebalancer.Propose(s_data, "beekeeper", 0.004).Length);
    }

    /// <summary>Largest 안은 인구가 가장 많은 하나에서만 뗀다 — 표가 가장 적게 흔들린다.</summary>
    [Fact]
    public void WeightRebalancer_LargestTakesFromOneArchetype()
    {
        RebalanceProposal proposal = WeightRebalancer.Propose(
            s_data, "beekeeper", 0.004, ArchetypeCard_Population, RebalanceStrategy.Largest);

        ImmutableArray<WeightChange> donors = [.. proposal.Changes.Where(c => c.To < c.From)];

        WeightChange donor = Assert.Single(donors);
        double heaviest = s_data.Archetypes.Archetypes.Max(a => a.PopulationWeight);

        Assert.Equal(heaviest, donor.From, 4);
        Assert.True(donor.Delta < 0, "인구가 줄지 않았다.");
    }

    /// <summary>Proportional 안은 전원에게서 뗀다.</summary>
    [Fact]
    public void WeightRebalancer_ProportionalTouchesEveryone()
    {
        RebalanceProposal proposal = WeightRebalancer.Propose(
            s_data, "beekeeper", 0.004, ArchetypeCard_Population, RebalanceStrategy.Proportional);

        Assert.Equal(s_data.Archetypes.Count + 1, proposal.Changes.Length);
    }

    /// <summary>줄이는 방향은 재배분이 아니다 — 남는 몫을 어디에 줄지는 다른 결정이다.</summary>
    [Fact]
    public void WeightRebalancer_RejectsShrinking()
    {
        ArchetypeDef biggest = s_data.Archetypes.Archetypes.MaxBy(a => a.PopulationWeight)!;

        Assert.Throws<ArgumentException>(
            () => WeightRebalancer.Propose(s_data, biggest.Id, biggest.PopulationWeight / 2));
    }

    private const int ArchetypeCard_Population = 5_000;

    // ── JsonSurgeon ────────────────────────────────────────────

    /// <summary>
    /// <b>무변경 편집은 바이트 동일이어야 한다.</b> 이것이 성립하지 않으면 도구가 만든 diff 를
    /// 사람이 읽을 수 없고, 그러면 아무도 도구를 쓰지 않는다.
    /// </summary>
    [Theory]
    [InlineData("archetypes.json", "archetypes", "id")]
    [InlineData("items.json", "items", "id")]
    [InlineData("actions.json", "actions", "id")]
    [InlineData("zones.json", "zones", "id")]
    [InlineData("pois.json", "pois", "id")]
    [InlineData("world_flags.json", "flags", "id")]
    [InlineData("interrupts.json", "rules", "id")]
    public void Surgeon_NoOpEditIsByteIdentical(string file, string array, string key)
    {
        string json = File.ReadAllText(Path.Combine(TestPaths.MasterData, file));

        using JsonDocument document = JsonDocument.Parse(json);

        JsonElement first = document.RootElement.GetProperty(array)[0];
        string id = first.GetProperty(key).GetString()!;

        // 같은 값으로 다시 쓴다. 한 바이트도 바뀌면 안 된다.
        foreach (JsonProperty field in first.EnumerateObject())
        {
            if (field.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                continue;
            }

            string edited = JsonSurgeon.SetInArrayItem(
                json, array, key, id, field.Name, field.Value.GetRawText());

            Assert.Equal(json, edited);
        }
    }

    [Fact]
    public void Surgeon_AppendKeepsIndentationAndParses()
    {
        string json = File.ReadAllText(Path.Combine(TestPaths.MasterData, "zones.json"));

        using JsonDocument before = JsonDocument.Parse(json);
        int count = before.RootElement.GetProperty("zones").GetArrayLength();

        string edited = JsonSurgeon.AppendToArray(
            json, "zones", """{ "id": "probe_zone", "code": 99, "name_key": "zone.probe", "adjacent": [] }""");

        using JsonDocument after = JsonDocument.Parse(edited);

        Assert.Equal(count + 1, after.RootElement.GetProperty("zones").GetArrayLength());
        Assert.Equal(
            "probe_zone",
            after.RootElement.GetProperty("zones")[count].GetProperty("id").GetString());

        // 앞부분은 한 글자도 변하지 않는다.
        int firstDiff = 0;
        while (firstDiff < json.Length && firstDiff < edited.Length && json[firstDiff] == edited[firstDiff])
        {
            firstDiff++;
        }

        Assert.True(
            firstDiff > json.Length / 2,
            $"편집이 {firstDiff} 번째 글자부터 달라졌다 — 서식이 보존되지 않았다.");
    }

    [Fact]
    public void Surgeon_SetsTopLevelScalar()
    {
        string json = File.ReadAllText(Path.Combine(TestPaths.MasterData, "context_buckets.json"));
        string edited = JsonSurgeon.SetTopLevel(json, "total_keys", "2952");

        using JsonDocument after = JsonDocument.Parse(edited);

        Assert.Equal(2952, after.RootElement.GetProperty("total_keys").GetInt32());
        // 자릿수가 같으므로 길이도 같다 — 값 하나만 갈아 끼운다는 것이 이 도구의 전부다.
        Assert.Equal(json.Length, edited.Length);
    }

    /// <summary>한글이 들어간 파일도 오프셋이 맞아야 한다 — 바이트와 char 이 다르다.</summary>
    [Fact]
    public void Surgeon_HandlesMultiByteText()
    {
        string json = File.ReadAllText(Path.Combine(TestPaths.MasterData, "archetypes.json"));

        Assert.True(
            Encoding.UTF8.GetByteCount(json) > json.Length, "이 파일에 한글이 없다 — 시험이 성립하지 않는다.");

        string edited = JsonSurgeon.SetInArrayItem(
            json, "archetypes", "id", "blacksmith", "population_weight", "0.0130");

        using JsonDocument after = JsonDocument.Parse(edited);

        JsonElement blacksmith = after.RootElement.GetProperty("archetypes")
            .EnumerateArray().First(a => a.GetProperty("id").GetString() == "blacksmith");

        Assert.Equal(0.0130, blacksmith.GetProperty("population_weight").GetDouble(), 4);

        // 마지막 아키타입의 설명이 깨지지 않았는지 — 오프셋이 밀렸으면 여기서 터진다.
        JsonElement archetypes = after.RootElement.GetProperty("archetypes");

        Assert.Equal(
            s_data.Archetypes.Archetypes[^1].Description,
            archetypes[archetypes.GetArrayLength() - 1].GetProperty("desc").GetString());
    }

    [Fact]
    public void Surgeon_ThrowsOnMissingTarget()
    {
        const string Json = """{ "items": [ { "id": "a" } ] }""";

        Assert.Throws<InvalidOperationException>(() => JsonSurgeon.ArrayRange(Json, "nope"));
        Assert.Throws<InvalidOperationException>(() => JsonSurgeon.ValueRange(Json, "nope"));
        Assert.Throws<InvalidOperationException>(
            () => JsonSurgeon.ItemRange(Json, "items", "id", "zzz"));
    }

    // ── DerivedArtifacts ───────────────────────────────────────

    /// <summary>커밋된 파생물은 커밋된 입력과 맞아야 한다. 어긋나면 누가 재생성을 빼먹은 것이다.</summary>
    [Fact]
    public void Derived_CommittedArtifactsAreFresh()
    {
        ImmutableArray<DerivedStatus> stale = DerivedArtifacts.Stale(TestPaths.MasterData);

        Assert.True(
            stale.IsEmpty,
            "낡은 파생물: " + string.Join(" · ", stale.Select(s => s.ToString()))
            + " — 해당 생성기를 다시 돌리고 derived.lock.json 을 같이 커밋한다.");
    }

    /// <summary>로더가 같은 판정을 실어 준다 — 호출부가 잠금 파일을 직접 읽지 않아도 되게.</summary>
    [Fact]
    public void Derived_LoaderCarriesTheVerdict()
    {
        Assert.Empty(s_data.StaleArtifacts);
    }

    /// <summary>입력이 바뀌면 낡은 것으로 잡혀야 한다. 이 판정이 안 되면 잠금 파일이 장식이다.</summary>
    [Fact]
    public void Derived_DetectsChangedInput()
    {
        string directory = NewTempCopy();

        try
        {
            Assert.Empty(DerivedArtifacts.Stale(directory));

            // 입력을 한 글자 바꾼다.
            string zones = Path.Combine(directory, "zones.json");
            File.WriteAllText(zones, File.ReadAllText(zones) + "\n");

            ImmutableArray<DerivedStatus> stale = DerivedArtifacts.Stale(directory);

            Assert.Equal(DerivedArtifacts.Known.Length, stale.Length);
            Assert.All(stale, s => Assert.Contains("zones.json", s.Reason, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>기록이 없으면 낡은 것이다 — 무엇으로 만들었는지 모르는 파일을 최신이라 부를 수 없다.</summary>
    [Fact]
    public void Derived_TreatsMissingRecordAsStale()
    {
        string directory = NewTempCopy();

        try
        {
            File.Delete(Path.Combine(directory, DerivedArtifacts.FileName));

            ImmutableArray<DerivedStatus> stale = DerivedArtifacts.Stale(directory);

            Assert.Equal(DerivedArtifacts.Known.Length, stale.Length);
            Assert.All(stale, s => Assert.Contains("기록이 없다", s.Reason, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary><c>Record</c> 는 다른 파생물의 기록을 지우지 않는다 — 생성기는 따로 돈다.</summary>
    [Fact]
    public void Derived_RecordKeepsOtherEntries()
    {
        string directory = NewTempCopy();

        try
        {
            DerivedArtifacts.Record(directory, "poi_distances.bin");

            Assert.Empty(DerivedArtifacts.Stale(directory));

            string json = File.ReadAllText(Path.Combine(directory, DerivedArtifacts.FileName));

            Assert.Contains("npc_instances.json", json, StringComparison.Ordinal);

            // 한글을 escape 하지 않는다 — 사람이 diff 로 읽는 파일이다.
            Assert.DoesNotContain("\\u", json, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ── ImpactAnalyzer ─────────────────────────────────────────

    /// <summary>
    /// <b><c>PlanStoreValidator</c> 와 같은 판정이어야 한다.</b> 두 곳이 다른 답을 내면
    /// 어느 쪽을 믿고 프리베이크를 돌릴지 알 수 없다.
    /// </summary>
    [Fact]
    public void Impact_AgreesWithPlanStoreValidator()
    {
        foreach (string path in Directory.GetFiles(TestPaths.MasterData))
        {
            string file = Path.GetFileName(path);
            Impact impact = ImpactAnalyzer.Of(TestPaths.MasterData, [file]);

            // 프리픽스가 바뀌면 파일별 표와 무관하게 전량이다 — 그 경우만 다를 수 있다.
            InvalidationScope expected = ImpactAnalyzer.PrefixInputs.Contains(file, StringComparer.OrdinalIgnoreCase)
                ? InvalidationScope.Full
                : Npc.Planning.PlanStoreValidator.ScopeOf(file);

            Assert.Equal(expected, impact.Scope);
        }
    }

    [Fact]
    public void Impact_ArchetypeChangeIsTheHeaviest()
    {
        Impact impact = ImpactAnalyzer.Of(TestPaths.MasterData, ["archetypes.json"]);

        Assert.Equal(InvalidationScope.Full, impact.Scope);
        Assert.True(impact.PrefixChanged, "아키타입 카탈로그는 프리픽스에 실린다.");
        Assert.True(impact.NeedsGameServerRedeploy, "아키타입 code 는 구조 해시에 든다.");
        Assert.True(impact.NeedsPrebake);

        // npc_instances 는 아키타입을 입력으로 쓴다 → 재생성 대상이다.
        Assert.Contains(impact.Regenerate, r => r.Artifact == "npc_instances.json");
    }

    [Fact]
    public void Impact_InterruptChangeIsCheap()
    {
        Impact impact = ImpactAnalyzer.Of(TestPaths.MasterData, ["interrupts.json"]);

        Assert.Equal(InvalidationScope.None, impact.Scope);
        Assert.False(impact.PrefixChanged);
        Assert.False(impact.NeedsGameServerRedeploy);
        Assert.Empty(impact.Regenerate);
    }

    [Fact]
    public void Impact_DescribesEveryDimension()
    {
        string text = ImpactAnalyzer.Describe(ImpactAnalyzer.Of(TestPaths.MasterData, ["pois.json"]));

        Assert.Contains("구조 해시", text, StringComparison.Ordinal);
        Assert.Contains("프리픽스", text, StringComparison.Ordinal);
        Assert.Contains("플랜", text, StringComparison.Ordinal);
        Assert.Contains("파생물", text, StringComparison.Ordinal);
    }

    // ── V12 · V13 ──────────────────────────────────────────────

    /// <summary>V12 — duty_hours 없이 근무 액션을 허용하면 실패한다.</summary>
    [Fact]
    public void V12_RejectsDutyActionWithoutDutyHours()
    {
        string directory = NewTempCopy();

        try
        {
            string path = Path.Combine(directory, "archetypes.json");
            string json = File.ReadAllText(path);

            // 근무 시간이 없는 아키타입을 하나 골라 Guard 를 허용한다.
            ArchetypeDef victim = s_data.Archetypes.Archetypes.First(a => a.DutyHours.IsEmpty);
            string actions = JsonSerializer.Serialize(
                victim.AllowedActions.Select(a => s_data.ActionName(a)).Append("Guard").Order(StringComparer.Ordinal));

            File.WriteAllText(
                path, JsonSurgeon.SetInArrayItem(json, "archetypes", "id", victim.Id, "allowed_actions", actions));

            MasterDataValidationReport report = MasterDataValidator.Validate(directory);

            MasterDataViolation violation = Assert.Single(report.Violations, v => v.Code == "V12");

            Assert.Contains(victim.Id, violation.Detail, StringComparison.Ordinal);
            Assert.Contains("Guard", violation.Detail, StringComparison.Ordinal);
            Assert.NotEmpty(violation.FixHint);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>V13 — 인스턴스가 없는 POI 를 가리키면 실패한다.</summary>
    [Fact]
    public void V13_RejectsDanglingPoiReference()
    {
        string directory = NewTempCopy();

        try
        {
            string path = Path.Combine(directory, "npc_instances.json");
            string json = File.ReadAllText(path);

            using JsonDocument document = JsonDocument.Parse(json);
            string home = document.RootElement.GetProperty("npcs")[0].GetProperty("home_poi").GetString()!;

            File.WriteAllText(path, json.Replace($"\"{home}\"", "\"no_such_poi\"", StringComparison.Ordinal));

            MasterDataValidationReport report = MasterDataValidator.Validate(directory);

            Assert.Contains(report.Violations, v => v.Code == "V13" && v.Detail.Contains("no_such_poi", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>V13 — 파일이 없으면 실패가 아니라 건너뜀이다. validate 만 돌려도 성립해야 한다.</summary>
    [Fact]
    public void V13_SkipsWhenInstancesAreMissing()
    {
        string directory = NewTempCopy();

        try
        {
            File.Delete(Path.Combine(directory, "npc_instances.json"));

            MasterDataValidationReport report = MasterDataValidator.Validate(directory);

            Assert.DoesNotContain(report.Violations, v => v.Code == "V13");
            Assert.Contains(report.Skipped, s => s.Code == "V13");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>지금 커밋된 마스터데이터는 V12·V13 을 통과한다.</summary>
    [Fact]
    public void V12AndV13_PassOnTheRepository()
    {
        MasterDataValidationReport report = MasterDataValidator.Validate(TestPaths.MasterData);

        Assert.DoesNotContain(report.Violations, v => v.Code is "V12" or "V13");
    }

    private static string NewTempCopy()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "npc-f04-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(directory);

        foreach (string file in Directory.GetFiles(TestPaths.MasterData))
        {
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        }

        foreach (string dir in Directory.GetDirectories(TestPaths.MasterData))
        {
            string target = Path.Combine(directory, Path.GetFileName(dir));
            Directory.CreateDirectory(target);

            foreach (string file in Directory.GetFiles(dir))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            }
        }

        return directory;
    }
}
