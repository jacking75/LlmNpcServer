using System.Collections.Immutable;
using Npc.Contracts;
using Npc.Core;
using Npc.MasterData;
using Npc.TestBed.Protocol;

namespace Npc.TestClient.Render;

/// <summary>
/// 지도 그리기. docs/20 §9.3.
///
/// <para>
/// <b>마스터데이터를 직접 읽는다.</b> 게임서버가 이름과 좌표를 중계하지 않는다 —
/// 링크에도 클라이언트 프로토콜에도 문자열이 없고(§3.5), 있어서도 안 된다.
/// 클라이언트가 <c>Npc.MasterData</c> 를 참조하는 이유가 이것이다 (docs/20 §3.2).
/// </para>
///
/// <para>
/// <b>존 원은 소속 POI 로부터 계산한다.</b> <c>zones.json</c> 에 좌표가 없기 때문이고,
/// 넣어서도 안 된다 — <c>MasterDataSet.ContentHash</c> 가 바뀌면 프리베이크된 플랜이
/// 전량 무효가 된다 (docs/20 §3.2).
/// </para>
/// </summary>
public sealed class MapRenderer : IDisposable
{
    /// <summary>존 원의 여유 반경(m). 가장 먼 POI 에 이만큼을 더한다.</summary>
    public const float ZonePadding = 40f;

    /// <summary>POI 사각의 한 변(px).</summary>
    public const int PoiSize = 4;

    /// <summary>POI 를 집는 반경(px).</summary>
    public const float PickRadius = 8f;

    /// <summary>이 줌 아래에서는 POI 이름을 안 쓴다. 다 쓰면 글자가 서로를 덮는다.</summary>
    public const float LabelZoom = 0.55f;

    private readonly MasterDataSet _data;
    private readonly ZoneCircle[] _zones;
    private readonly RectangleF _worldBounds;

    private readonly Font _labelFont = new("Consolas", 7.5f);
    private readonly Font _zoneFont = new("Segoe UI", 9f, FontStyle.Bold);
    private readonly SolidBrush _labelBrush = new(Color.FromArgb(150, 200, 200, 210));
    private readonly SolidBrush _zoneLabelBrush = new(Color.FromArgb(120, 210, 210, 225));
    private readonly SolidBrush _zoneFill = new(Color.FromArgb(18, 120, 140, 190));

    /// <summary>렌더러를 만든다. 존 원은 여기서 한 번만 잰다.</summary>
    public MapRenderer(MasterDataSet data)
    {
        ArgumentNullException.ThrowIfNull(data);

        _data = data;
        _zones = BuildZoneCircles(data);
        _worldBounds = BuildWorldBounds(data, _zones);
    }

    /// <summary>카메라. 입력(T6-29)이 이것을 만진다.</summary>
    public Camera Camera { get; } = new();

    /// <summary>POI 전체를 감싸는 사각형. <c>Home</c> 이 여기에 맞춘다.</summary>
    public RectangleF WorldBounds => _worldBounds;

    /// <summary>존·POI 를 그린다. 엔티티는 T6-28 이 이 위에 얹는다.</summary>
    /// <param name="g">그래픽스.</param>
    /// <param name="viewport">그릴 영역.</param>
    /// <param name="zones">지금 존 상태. 비어 있으면 마스터데이터 기본값으로 그린다.</param>
    public void DrawWorld(Graphics g, Rectangle viewport, ZoneStates zones)
    {
        ArgumentNullException.ThrowIfNull(g);

        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        DrawZones(g, viewport, zones);
        DrawPois(g, viewport);
    }

    /// <summary>
    /// 화면 좌표에 가장 가까운 POI. <see cref="PickRadius"/> 안에 없으면 false.
    /// 마우스 오버 툴팁이 쓴다 (docs/20 §9.3).
    /// </summary>
    public bool TryPickPoi(Point screen, Rectangle viewport, out PoiDef poi)
    {
        ImmutableArray<PoiDef> pois = _data.Pois.Pois;
        float best = PickRadius * PickRadius;

        poi = null!;

        foreach (PoiDef candidate in pois)
        {
            PointF at = Camera.ToScreen(candidate.Pos.X, candidate.Pos.Z, viewport);
            float dx = at.X - screen.X;
            float dy = at.Y - screen.Y;
            float distance = (dx * dx) + (dy * dy);

            if (distance <= best)
            {
                best = distance;
                poi = candidate;
            }
        }

        return poi is not null;
    }

    /// <summary>이 존의 지금 지역 상태. 아직 못 받았으면 마스터데이터 기본값이다.</summary>
    public RegionState RegionStateOf(ZoneId zone, ZoneStates zones)
    {
        if (zones.Zones is { } states)
        {
            foreach (ZoneState state in states)
            {
                if (state.ZoneCode == zone.Value)
                {
                    return (RegionState)state.RegionState;
                }
            }
        }

        return _data.Zones[zone].DefaultRegionState;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _labelFont.Dispose();
        _zoneFont.Dispose();
        _labelBrush.Dispose();
        _zoneLabelBrush.Dispose();
        _zoneFill.Dispose();
    }

    /// <summary>
    /// 지역 상태별 테두리 색. docs/20 §9.3.
    ///
    /// <b>이 색이 데모의 절정이다</b> — 공성 시나리오에서 테두리가 빨개지는 순간
    /// 그 존의 NPC 전원이 다른 계획으로 갈아탄다 (docs/20 §11.2).
    /// </summary>
    public static Color BorderOf(RegionState state) => state switch
    {
        RegionState.War => Color.FromArgb(220, 70, 60),
        RegionState.Alert => Color.FromArgb(220, 150, 50),
        _ => Color.FromArgb(90, 95, 110),
    };

    // ---------------------------------------------------------------- 존

    private void DrawZones(Graphics g, Rectangle viewport, ZoneStates zones)
    {
        foreach (ZoneCircle circle in _zones)
        {
            PointF center = Camera.ToScreen(circle.X, circle.Z, viewport);
            float radius = Camera.Scale(circle.Radius);

            if (radius < 2f || !Intersects(viewport, center, radius))
            {
                continue;   // 화면 밖이다
            }

            var box = new RectangleF(center.X - radius, center.Y - radius, radius * 2, radius * 2);

            g.FillEllipse(_zoneFill, box);

            RegionState state = RegionStateOf(circle.Zone, zones);

            // War·Alert 는 굵게. 회색 평시와 같은 굵기면 흘긋 봐서 구별이 안 된다.
            using var pen = new Pen(BorderOf(state), state == RegionState.Peace ? 1f : 2.5f);

            g.DrawEllipse(pen, box);

            // 이름표는 원 위에 붙이되 화면 밖으로 나가지 않게 당긴다 —
            // 안 당기면 위쪽 존의 이름이 통째로 사라져 "이 원이 어디인가" 를 알 수 없다.
            float labelY = Math.Max(viewport.Top + 2f, center.Y - radius - 16f);

            g.DrawString(circle.Label, _zoneFont, _zoneLabelBrush, center.X - radius, labelY);
        }
    }

    // ---------------------------------------------------------------- POI

    private void DrawPois(Graphics g, Rectangle viewport)
    {
        ImmutableArray<PoiDef> pois = _data.Pois.Pois;
        bool labels = Camera.Zoom >= LabelZoom;
        float half = PoiSize / 2f;

        foreach (PoiDef poi in pois)
        {
            PointF at = Camera.ToScreen(poi.Pos.X, poi.Pos.Z, viewport);

            if (!viewport.Contains((int)at.X, (int)at.Y))
            {
                continue;
            }

            using var brush = new SolidBrush(ShadeOf(poi.Type));

            g.FillRectangle(brush, at.X - half, at.Y - half, PoiSize, PoiSize);

            if (labels)
            {
                g.DrawString(poi.Id, _labelFont, _labelBrush, at.X + half + 2f, at.Y - 7f);
            }
        }
    }

    /// <summary>
    /// POI 타입별 회색조. docs/20 §9.3.
    ///
    /// <b>색으로 구별하지 않는다.</b> 색은 아키타입(NPC)의 몫이고, 배경까지 색을 쓰면
    /// 화면이 무엇을 강조하는지 알 수 없게 된다.
    /// </summary>
    private static Color ShadeOf(PoiType type) => type switch
    {
        PoiType.Home => Color.FromArgb(120, 120, 128),
        PoiType.Workplace => Color.FromArgb(170, 170, 180),
        PoiType.Market => Color.FromArgb(200, 200, 210),
        PoiType.Tavern => Color.FromArgb(185, 180, 165),
        PoiType.Temple => Color.FromArgb(195, 195, 215),
        PoiType.Gate => Color.FromArgb(215, 205, 195),
        PoiType.Field => Color.FromArgb(140, 155, 140),
        _ => Color.FromArgb(105, 110, 105),
    };

    private static bool Intersects(Rectangle viewport, PointF center, float radius) =>
        center.X + radius >= viewport.Left
        && center.X - radius <= viewport.Right
        && center.Y + radius >= viewport.Top
        && center.Y - radius <= viewport.Bottom;

    // ---------------------------------------------------------------- 사전 계산

    private static ZoneCircle[] BuildZoneCircles(MasterDataSet data)
    {
        var circles = new List<ZoneCircle>(data.Zones.Count);

        foreach (ZoneDef zone in data.Zones.Zones)
        {
            ImmutableArray<PoiId> pois = data.Pois.InZone(zone.Code);

            if (pois.Length == 0)
            {
                continue;
            }

            double x = 0;
            double z = 0;

            foreach (PoiId poi in pois)
            {
                WorldPos pos = data.Pois[poi].Pos;

                x += pos.X;
                z += pos.Z;
            }

            var centerX = (float)(x / pois.Length);
            var centerZ = (float)(z / pois.Length);
            float radius = 0;

            foreach (PoiId poi in pois)
            {
                WorldPos pos = data.Pois[poi].Pos;
                float dx = pos.X - centerX;
                float dz = pos.Z - centerZ;

                radius = Math.Max(radius, MathF.Sqrt((dx * dx) + (dz * dz)));
            }

            circles.Add(new ZoneCircle(zone.Code, zone.Id, centerX, centerZ, radius + ZonePadding));
        }

        return [.. circles];
    }

    /// <summary>
    /// 그려지는 것 전부를 감싸는 사각형.
    ///
    /// <b>POI 좌표만으로 재지 않는다.</b> 존 원이 가장 먼 POI 보다 <see cref="ZonePadding"/> 만큼
    /// 더 나가므로, POI 로만 재면 <c>Home</c> 맞춤에서 가장자리 존의 테두리가 잘린다 —
    /// 실제로 deepwood 가 아래로 잘렸다.
    /// </summary>
    private static RectangleF BuildWorldBounds(MasterDataSet data, ZoneCircle[] zones)
    {
        ImmutableArray<PoiDef> pois = data.Pois.Pois;

        if (pois.Length == 0)
        {
            return new RectangleF(-100, -100, 200, 200);
        }

        float minX = float.MaxValue;
        float minZ = float.MaxValue;
        float maxX = float.MinValue;
        float maxZ = float.MinValue;

        foreach (PoiDef poi in pois)
        {
            minX = Math.Min(minX, poi.Pos.X);
            minZ = Math.Min(minZ, poi.Pos.Z);
            maxX = Math.Max(maxX, poi.Pos.X);
            maxZ = Math.Max(maxZ, poi.Pos.Z);
        }

        foreach (ZoneCircle circle in zones)
        {
            minX = Math.Min(minX, circle.X - circle.Radius);
            minZ = Math.Min(minZ, circle.Z - circle.Radius);
            maxX = Math.Max(maxX, circle.X + circle.Radius);
            maxZ = Math.Max(maxZ, circle.Z + circle.Radius);
        }

        return RectangleF.FromLTRB(minX, minZ, maxX, maxZ);
    }

    /// <summary>존 하나의 배경 원. 기동 시 한 번 잰다.</summary>
    private readonly record struct ZoneCircle(ZoneId Zone, string Label, float X, float Z, float Radius);
}
