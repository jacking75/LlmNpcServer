using Npc.Contracts;
using Npc.MasterData;
using Npc.Runtime;

namespace Npc.Host.Api;

/// <summary>정적 지도에서 존 하나. 경계는 그 존 POI의 최소·최대 좌표다.</summary>
public readonly record struct WorldZone(
    ushort Code, string Id, float X, float Z, float MinX, float MinZ, float MaxX, float MaxZ);

/// <summary>정적 지도에서 POI 하나.</summary>
public readonly record struct WorldPoi(ushort Code, string Type, ushort Zone, float X, float Z);

/// <summary>기동 때 한 번 만드는 지도 바탕.</summary>
public readonly record struct WorldMap(WorldZone[] Zones, WorldPoi[] Pois);

/// <summary>1초에 한 번 이하로 만드는 지도 NPC 열 배열.</summary>
public readonly record struct WorldNpcSnapshot(
    long Tick, int[] Id, float[] X, float[] Z, ushort[] Archetype,
    byte[] Status, ushort[] Zone, byte[] ZoneState, byte[] ZonesState);

/// <summary>웹 스레드에서만 읽고 조립하는 지도 조회. 틱 루프에는 손대지 않는다.</summary>
internal sealed class WorldEndpoints
{
    /// <summary>정적 지도 경로.</summary>
    public const string MapRoute = "/world";

    /// <summary>NPC 위치 열 배열 경로.</summary>
    public const string NpcsRoute = "/world/npcs";

    /// <summary>새 스냅샷 사이의 최소 간격.</summary>
    public const long CacheMillis = 1_000;

    private readonly NpcStore _store;
    private readonly ZoneStateTable _states;
    private readonly Func<long> _tick;
    private readonly Func<long> _millis;
    private readonly object _gate = new();
    private WorldNpcSnapshot _cached;
    private long _cachedAt = long.MinValue;

    /// <summary>정적 데이터는 생성 시 한 번 조립한다.</summary>
    public WorldEndpoints(
        NpcStore store, MasterDataSet data, ZoneStateTable states,
        Func<long> tick, Func<long>? millis = null)
    {
        _store = store;
        _states = states;
        _tick = tick;
        _millis = millis ?? (() => Environment.TickCount64);

        WorldPoi[] pois = [.. data.Pois.Pois.Select(poi => new WorldPoi(
            poi.Code.Value, poi.Type.ToString().ToLowerInvariant(), poi.Zone.Value,
            poi.Pos.X, poi.Pos.Z))];
        WorldZone[] zones = [.. data.Zones.Zones.Select(zone => Bounds(zone.Code, zone.Id, pois))];
        Map = new WorldMap(zones, pois);
    }

    /// <summary>정적 지도.</summary>
    public WorldMap Map { get; }

    /// <summary>1초 캐시를 거친 NPC 위치.</summary>
    public WorldNpcSnapshot Npcs()
    {
        lock (_gate)
        {
            long now = _millis();
            if (_cachedAt != long.MinValue && now - _cachedAt < CacheMillis) return _cached;

            var id = new List<int>(_store.Count);
            var x = new List<float>(_store.Count);
            var z = new List<float>(_store.Count);
            var archetype = new List<ushort>(_store.Count);
            var status = new List<byte>(_store.Count);
            var zone = new List<ushort>(_store.Count);
            var zoneState = new List<byte>(_store.Count);

            for (int slot = 0; slot < _store.Count; slot++)
            {
                int occupant = _store.Occupant[slot];
                if (occupant == 0) continue;
                ushort code = _store.ZoneCode[slot];
                id.Add(occupant);
                x.Add(_store.Pos[slot].X);
                z.Add(_store.Pos[slot].Z);
                archetype.Add(_store.ArchetypeCode[slot]);
                status.Add(_store.StepStatus[slot]);
                zone.Add(code);
                zoneState.Add((byte)_states.RegionOf(new ZoneId(code)));
            }

            byte[] zonesState = [.. Map.Zones.Select(item =>
                (byte)_states.RegionOf(new ZoneId(item.Code)))];
            _cached = new WorldNpcSnapshot(_tick(), [.. id], [.. x], [.. z],
                [.. archetype], [.. status], [.. zone], [.. zoneState], zonesState);
            _cachedAt = now;
            return _cached;
        }
    }

    private static WorldZone Bounds(ZoneId code, string id, WorldPoi[] all)
    {
        WorldPoi[] pois = [.. all.Where(poi => poi.Zone == code.Value)];
        if (pois.Length == 0) return new WorldZone(code.Value, id, 0, 0, 0, 0, 0, 0);
        float minX = pois.Min(poi => poi.X);
        float maxX = pois.Max(poi => poi.X);
        float minZ = pois.Min(poi => poi.Z);
        float maxZ = pois.Max(poi => poi.Z);
        return new WorldZone(code.Value, id,
            (minX + maxX) / 2, (minZ + maxZ) / 2, minX, minZ, maxX, maxZ);
    }
}
