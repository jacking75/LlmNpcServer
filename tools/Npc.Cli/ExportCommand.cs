using System.Collections.Immutable;
using Npc.Contracts;
using Npc.MasterData;
using Npc.MasterData.Authoring;

namespace Npc.Cli;

/// <summary>게임서버 연동 번들을 파일 또는 표준출력으로 내보낸다.</summary>
internal static class ExportCommand
{
    public static int Run(CliContext ctx)
    {
        ImmutableArray<string> positional = Program.Positional(ctx, "--npcs", "--zone", "--shard", "--out");
        if (positional.Length != 1 || positional[0] != "link-bundle")
        {
            ctx.Out.WriteLine("사용법: npc export link-bundle --npcs <N> [--zone a,b | --shard N] [--dynamic-roster] [--out <path>]");
            return Program.BadUsage;
        }

        string? countText = Program.Flag(ctx, "--npcs");
        if (!int.TryParse(countText, out int count) || count <= 0)
        {
            ctx.Out.WriteLine("--npcs 에 1 이상의 NPC 수를 넣는다.");
            return Program.BadUsage;
        }

        MasterDataSet data = ctx.Data;
        string instancesPath = Path.Combine(ctx.MasterData, "npc_instances.json");
        NpcInstanceTable instances = NpcInstanceTable.Load(instancesPath, data);
        string? zoneText = Program.Flag(ctx, "--zone");
        string? shardText = Program.Flag(ctx, "--shard");
        ZoneId[] zones;
        ushort shard = 0;

        if (shardText is not null)
        {
            if (!ushort.TryParse(shardText, out shard) || shard == 0)
            {
                ctx.Out.WriteLine("--shard 에 1 이상의 번호를 넣는다.");
                return Program.BadUsage;
            }

            string root = Directory.GetParent(Path.GetFullPath(ctx.MasterData))!.FullName;
            string shardPath = Path.Combine(root, ShardTable.DefaultPath);
            ShardDef? def = ShardTable.Load(shardPath, data).TryGet(shard);
            if (def is null) throw new ArgumentException($"샤드 {shard}가 {shardPath}에 없다.");
            zones = [.. def.Value.Zones];
        }
        else if (zoneText is { Length: > 0 })
        {
            string[] ids = zoneText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            zones = new ZoneId[ids.Length];
            for (int i = 0; i < ids.Length; i++)
            {
                if (!data.Zones.TryGet(ids[i], out ZoneDef def))
                    throw new ArgumentException($"존 '{ids[i]}'이 없다.");
                zones[i] = def.Code;
            }
        }
        else
        {
            zones = [];
        }

        string json = LinkBundle.Create(data, instances, count, zones, shard,
            Program.HasFlag(ctx, "--dynamic-roster"));
        if (Program.Flag(ctx, "--out") is { } path)
        {
            File.WriteAllText(path, json, new System.Text.UTF8Encoding(false));
            ctx.Out.WriteLine($"번들: {Path.GetFullPath(path)}");
        }
        else
        {
            ctx.Out.Write(json);
        }

        return Program.Ok;
    }
}
