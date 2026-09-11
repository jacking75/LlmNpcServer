using Npc.Contracts;
using Npc.MasterData;
using Npc.MasterData.Validation;

namespace Npc.Tests.MasterData;

/// <summary>
/// D-04 — 세력 표와 개체별 행동 파라미터.
///
/// <para>
/// <b>여기서 지키는 것은 "재생성해도 순찰로가 남는다" 하나다.</b>
/// <c>npc_instances.json</c> 은 <c>tools/gen_npcs.cs</c> 의 생성물이라 손편집을 그 안에 하면
/// 다음 재생성에 통째로 사라진다. 파일을 가르고 id 로 병합하는 것이 그 답이고,
/// 아래 테스트들은 그 병합이 실제로 도는지와 <b>잘못된 병합을 기동 실패로 만드는지</b>를 본다.
/// </para>
/// </summary>
public sealed class InstanceParamsTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    /// <summary>세력 code 는 파일이 정하고 1 부터다 — 0 은 "미지정" 이라 쓸 수 없다.</summary>
    [Fact]
    public void Factions_LoadWithFrozenCodes()
    {
        FactionTable table = s_data.Factions!;

        Assert.Equal(6, table.Count);

        Assert.True(table.TryGet("townsfolk", out FactionId townsfolk));
        Assert.Equal(1, townsfolk.Value);

        Assert.True(table.TryGet("town_watch", out FactionId watch));
        Assert.Equal(2, watch.Value);

        Assert.Equal("town_watch", table.NameOf(watch));
        Assert.Equal(string.Empty, table.NameOf(default));

        foreach (FactionDef faction in table.Factions)
        {
            Assert.True(faction.Code.Value >= 1, $"{faction.Id} 의 code 가 0 이다");
        }
    }

    /// <summary>code 0 은 거절한다 — 세력을 안 준 NPC 와 구분할 수 없게 된다.</summary>
    [Fact]
    public void Factions_RejectCodeZero()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => FactionTable.Parse(
                """{"version":1,"factions":[{"id":"none","code":0,"desc":"","hostile_to":[]}]}"""));

        Assert.Contains("미지정", error.Message, StringComparison.Ordinal);
    }

    /// <summary>적대 목록의 오타는 조용히 사라지면 안 된다.</summary>
    [Fact]
    public void Factions_RejectUnknownHostile()
    {
        Assert.Throws<InvalidDataException>(
            () => FactionTable.Parse(
                """
                {"version":1,"factions":[
                  {"id":"a","code":1,"desc":"","hostile_to":["typo"]}
                ]}
                """));
    }

    /// <summary>
    /// <b>세력 code 는 구조 해시에 들어간다</b> (B-04). 두 프로세스가 다른 번호를 쓰면
    /// 위병대를 도적으로 읽는데, 그 사고는 아무 로그도 남기지 않는다.
    /// </summary>
    [Fact]
    public void FactionCodes_AreStructural()
    {
        string with = StructuralHash.Compute(
            s_data.Actions, s_data.Items, s_data.Zones, s_data.Pois, s_data.Archetypes,
            s_data.Buckets, s_data.Dialogues, s_data.Factions);

        string without = StructuralHash.Compute(
            s_data.Actions, s_data.Items, s_data.Zones, s_data.Pois, s_data.Archetypes,
            s_data.Buckets, s_data.Dialogues);

        Assert.NotEqual(with, without);
        Assert.Equal(s_data.StructuralHash, with);
    }

    /// <summary>
    /// 저장소의 <c>npc_overrides.json</c> 이 실제로 얹힌다.
    ///
    /// <b>덮어쓰지 않은 필드는 그대로다</b> — 병합이지 치환이 아니다.
    /// </summary>
    [Fact]
    public void Overrides_MergeOntoTheGeneratedFile()
    {
        NpcInstanceTable table = Instances();
        NpcInstanceDef guard = Find(table, 2326);

        Assert.Equal(3, guard.PatrolRoute.Length);
        Assert.Equal(guard.PatrolRoute[0], guard.PatrolStart);
        Assert.Equal(20, guard.AggroRadiusM);
        Assert.Equal("guard_stern_01", guard.DialogueProfile);
        Assert.Equal(15, guard.ScheduleOffsetMinutes);

        Assert.True(s_data.Factions!.TryGet("town_watch", out FactionId watch));
        Assert.Equal(watch, guard.Faction);

        // 생성물 쪽 필드는 건드리지 않았다.
        Assert.True(s_data.Pois.TryGet("house_012_04", out PoiDef home));
        Assert.Equal(home.Code, guard.Home);
    }

    /// <summary>얹히지 않은 NPC 는 지금과 같다 — 전부 선택 필드다.</summary>
    [Fact]
    public void Overrides_LeaveEveryoneElseAlone()
    {
        NpcInstanceTable table = Instances();
        NpcInstanceDef plain = Find(table, 1);

        Assert.True(plain.PatrolRoute.IsDefaultOrEmpty);
        Assert.Equal(default, plain.PatrolStart);
        Assert.Equal(0, plain.AggroRadiusM);
        Assert.Equal(0, plain.Faction.Value);
        Assert.Equal(string.Empty, plain.DialogueProfile);
        Assert.Equal(0, plain.ScheduleOffsetMinutes);
    }

    /// <summary>
    /// <b>순찰로가 존을 넘으면 기동 실패다.</b> 존이 다르면 샤드가 다를 수 있고,
    /// 그때 그 NPC 는 우리가 이벤트를 받지 못하는 곳으로 걸어가 그대로 멈춘다 (A-08).
    /// </summary>
    [Fact]
    public void Overrides_RejectPatrolAcrossZones()
    {
        NpcInstanceDef guard = Find(Instances(), 2326);
        PoiId elsewhere = FirstPoiOutside(guard.Zone);

        InvalidDataException error = WriteAndLoad(
            $$"""
            {"version":1,"overrides":[
              {"id":2326,"patrol_route":["{{s_data.Pois[elsewhere].Id}}"]}
            ]}
            """);

        Assert.Contains("다른 존", error.Message, StringComparison.Ordinal);
    }

    /// <summary>모르는 id·모르는 세력·범위 밖 값은 전부 기동 실패다.</summary>
    [Theory]
    [InlineData("""{"version":1,"overrides":[{"id":999999,"aggro_radius_m":5}]}""", "npc_instances.json 에 없다")]
    [InlineData("""{"version":1,"overrides":[{"id":1,"faction":"nope"}]}""", "factions.json 에 없다")]
    [InlineData("""{"version":1,"overrides":[{"id":1,"aggro_radius_m":9999}]}""", "aggro_radius_m")]
    [InlineData("""{"version":1,"overrides":[{"id":1,"schedule_offset_min":9999}]}""", "schedule_offset_min")]
    [InlineData("""{"version":1,"overrides":[{"id":1},{"id":1}]}""", "두 번 나온다")]
    public void Overrides_RejectBadRows(string json, string expected)
    {
        InvalidDataException error = WriteAndLoad(json);

        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    /// <summary>순찰 지점 수 상한을 넘으면 기동 실패다 — 런타임 배열이 고정 폭이다.</summary>
    [Fact]
    public void Overrides_RejectTooManyWaypoints()
    {
        NpcInstanceDef guard = Find(Instances(), 2326);
        string[] route =
        [
            .. Enumerable.Repeat(
                s_data.Pois[guard.Home].Id, NpcInstanceTable.MaxPatrolWaypoints + 1),
        ];

        InvalidDataException error = WriteAndLoad(
            $$"""
            {"version":1,"overrides":[{"id":2326,"patrol_route":[{{string.Join(",", route.Select(r => $"\"{r}\""))}}]}]}
            """);

        Assert.Contains("상한", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// V13 이 같은 것을 본다. <b>로더는 던지고 검증기는 모아 준다</b> —
    /// 손편집 파일이라 틀린 줄이 여럿일 때 첫 줄만 알려 주면 고치는 데 왕복이 생긴다.
    /// </summary>
    [Fact]
    public void V13_ReportsOverrideViolations()
    {
        string directory = CopyMasterData();

        File.WriteAllText(
            Path.Combine(directory, NpcInstanceTable.OverrideFileName),
            """
            {"version":1,"overrides":[
              {"id":999999,"aggro_radius_m":5},
              {"id":1,"faction":"nope","schedule_offset_min":9999}
            ]}
            """);

        MasterDataValidationReport report = MasterDataValidator.Validate(directory);

        Assert.True(report.HasViolation("V13"));
        Assert.True(
            report.Violations.Count(v => v.File == NpcInstanceTable.OverrideFileName) >= 3,
            $"위반을 모아서 내지 않는다: {report.Violations.Length}건");
    }

    /// <summary>얹을 파일이 없으면 아무 일도 없다 — 전부 선택이다.</summary>
    [Fact]
    public void Overrides_AreOptional()
    {
        string directory = CopyMasterData();
        File.Delete(Path.Combine(directory, NpcInstanceTable.OverrideFileName));

        MasterDataSet data = MasterDataLoader.Load(directory);
        NpcInstanceTable table = NpcInstanceTable.Load(
            Path.Combine(directory, "npc_instances.json"), data);

        Assert.True(Find(table, 2326).PatrolRoute.IsDefaultOrEmpty);
    }

    // ---------------------------------------------------------------- 헬퍼

    private static NpcInstanceTable Instances() =>
        NpcInstanceTable.Load(Path.Combine(TestPaths.MasterData, "npc_instances.json"), s_data);

    private static NpcInstanceDef Find(NpcInstanceTable table, int id)
    {
        foreach (NpcInstanceDef def in table.Instances)
        {
            if (def.Id == id)
            {
                return def;
            }
        }

        Assert.Fail($"NPC {id} 이 없다.");
        return default;
    }

    private static PoiId FirstPoiOutside(ZoneId zone)
    {
        foreach (PoiDef poi in s_data.Pois.Pois)
        {
            if (poi.Zone != zone)
            {
                return poi.Code;
            }
        }

        Assert.Fail("다른 존의 POI 가 없다.");
        return default;
    }

    /// <summary>임시 폴더에 마스터데이터를 복사한다. 원본을 건드리지 않는다.</summary>
    private static string CopyMasterData()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "npc-d04-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(directory);

        foreach (string file in Directory.GetFiles(TestPaths.MasterData))
        {
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        }

        foreach (string sub in Directory.GetDirectories(TestPaths.MasterData))
        {
            string target = Path.Combine(directory, Path.GetFileName(sub));
            Directory.CreateDirectory(target);

            foreach (string file in Directory.GetFiles(sub))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            }
        }

        return directory;
    }

    /// <summary>덮어쓰기 파일을 바꿔 놓고 로드한다. 던진 예외를 돌려준다.</summary>
    private static InvalidDataException WriteAndLoad(string json)
    {
        string directory = CopyMasterData();

        File.WriteAllText(Path.Combine(directory, NpcInstanceTable.OverrideFileName), json);

        MasterDataSet data = MasterDataLoader.Load(directory);

        return Assert.Throws<InvalidDataException>(
            () => NpcInstanceTable.Load(Path.Combine(directory, "npc_instances.json"), data));
    }
}
