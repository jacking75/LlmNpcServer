using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npc.Contracts;

namespace Npc.MasterData.Authoring;

/// <summary>다른 언어의 게임서버가 핸드셰이크와 스폰에 필요한 값을 읽는 번들.</summary>
public static class LinkBundle
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>호스트와 같은 로스터 선택·해시 함수로 번들을 만든다.</summary>
    public static string Create(
        MasterDataSet data, NpcInstanceTable instances, int count,
        ZoneId[]? zones = null, ushort shard = 0, bool dynamicRoster = false)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        zones ??= [];
        NpcRoster roster = NpcRoster.Select(instances, count, zones);
        if (roster.Count == 0) throw new ArgumentException("선택한 존에 NPC가 없다.", nameof(zones));

        string rosterHash = dynamicRoster
            ? NpcRoster.HashOf(instances.Instances) : roster.Hash;
        var commands = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (CommandResponseSpec spec in CommandResponses.All)
        {
            commands.Add(spec.Command.ToString(), new
            {
                Respond = new[] { spec.Success.ToString() },
                AlternativeRespond = spec.AlternativeSuccess?.ToString(),
                Progress = spec.Progress?.ToString(),
                TimeOwner = spec.TimeOwner switch
                {
                    CommandTimeOwner.GameServer => "game_server",
                    CommandTimeOwner.NpcServer => "npc_server",
                    _ => "instant",
                },
                Failures = spec.Failures.Select(f => f.ToString()).ToArray(),
            });
        }

        var bundle = new
        {
            BundleVersion = 1,
            Contract = new { Major = ContractVersion.Major, Minor = ContractVersion.Minor },
            TickRateHz = Tick.PerSecond,
            Note = "Hello.timeScale은 NPC 서버의 --time-scale과 같아야 한다. 실행 인자라 번들에 싣지 않는다.",
            Hashes = new { Structural = data.StructuralHash, Content = data.ContentHash, Roster = rosterHash },
            Roster = new
            {
                NpcCount = roster.Count,
                ZoneFilter = zones.Select(z => z.Value).ToArray(),
                Shard = shard,
                ZoneMask = ShardTable.MaskOf(zones),
                Dynamic = dynamicRoster,
                Npcs = roster.Npcs.Select((npc, slot) => new
                {
                    Slot = slot,
                    NpcId = npc.Id,
                    Archetype = npc.Archetype.Value,
                    Zone = npc.Zone.Value,
                    HomePoi = npc.Home.Value,
                    WorkPoi = npc.Workplace.Value,
                    Spawn = new { npc.Spawn.X, npc.Spawn.Y, npc.Spawn.Z },
                    Faction = npc.Faction.Value,
                    Instance = shard,
                }).ToArray(),
            },
            Zones = data.Zones.Zones.Select(z => new
            {
                Code = z.Code.Value,
                z.Id,
                DefaultRegionState = (byte)z.DefaultRegionState,
                DefaultClimate = (byte)z.DefaultClimate,
            }).ToArray(),
            Pois = data.Pois.Pois.Select(p => new
            {
                Code = p.Code.Value,
                p.Id,
                Zone = p.Zone.Value,
                Type = p.Type.ToString().ToLowerInvariant(),
                Pos = new { p.Pos.X, p.Pos.Y, p.Pos.Z },
                p.Capacity,
            }).ToArray(),
            Archetypes = data.Archetypes.Archetypes.Select(a => new { Code = a.Code.Value, a.Id }).ToArray(),
            Items = data.Items.Items.Select(i => new { Code = i.Code.Value, i.Id }).ToArray(),
            Recipes = data.Items.Recipes.Select(r => new
            {
                Item = data.Items.TryGet(r.Id, out ItemDef? item) ? item.Code.Value : 0,
                DurationS = r.DurationSeconds,
                Inputs = r.Inputs.Select(i => new { Item = i.Item.Value, i.Count }).ToArray(),
                Outputs = r.Outputs.Select(i => new { Item = i.Item.Value, i.Count }).ToArray(),
            }).ToArray(),
            Time = new
            {
                StartGameHour = 6,
                TimeOfDayHours = Enum.GetValues<Core.TimeOfDay>().ToDictionary(
                    t => t.ToString(), t => new[] { data.Buckets.GameHoursOf(t).From, data.Buckets.GameHoursOf(t).To }),
            },
            Commands = commands,
        };

        return JsonSerializer.Serialize(bundle, s_json) + "\n";
    }
}
