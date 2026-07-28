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

    /// <summary>NPC 사각의 한 변(px). docs/20 §9.3.</summary>
    public const int NpcSize = 6;

    /// <summary>플레이어 삼각형의 한 변(px).</summary>
    public const int PlayerSize = 10;

    /// <summary>엔티티를 집는 반경(px). 좌클릭 선택이 쓴다 (docs/20 §9.4).</summary>
    public const float EntityPickRadius = 15f;

    /// <summary>
    /// 아키타입 색의 황금각(도). 40종이 색상환에 균등하게 갈린다 (docs/20 §9.3).
    ///
    /// <b>순번 × 고정 각도인 것이 요점이다.</b> 팔레트를 손으로 적으면 아키타입이 늘 때마다
    /// 색을 고르게 되고, 그러다 인접한 두 종이 같은 색이 된다.
    /// </summary>
    public const double GoldenAngleDegrees = 137.5;

    private readonly MasterDataSet _data;
    private readonly ZoneCircle[] _zones;
    private readonly RectangleF _worldBounds;

    private readonly Font _labelFont = new("Consolas", 7.5f);
    private readonly Font _zoneFont = new("Segoe UI", 9f, FontStyle.Bold);
    private readonly SolidBrush _labelBrush = new(Color.FromArgb(150, 200, 200, 210));
    private readonly SolidBrush _zoneLabelBrush = new(Color.FromArgb(120, 210, 210, 225));
    private readonly SolidBrush _zoneFill = new(Color.FromArgb(18, 120, 140, 190));

    /// <summary>
    /// 아키타입별 색과 사각 묶음. <b>색마다 한 번씩만 <c>FillRectangles</c> 를 부른다</b>
    /// (docs/20 §9.1) — 개별 <c>FillRectangle</c> 256번보다 확실히 빠르다.
    /// </summary>
    private readonly SolidBrush[] _archetypeBrushes;
    private readonly RectangleF[][] _archetypeRects;
    private readonly int[] _archetypeCounts;

    /// <summary>
    /// POI 타입별 브러시와 사각 묶음. 아키타입 쪽과 같은 이유다 — <b>프레임마다 브러시를
    /// 만들면 안 된다.</b> POI 는 243개라 개당 <c>new SolidBrush</c> 면 초당 14,000개가 나온다.
    /// </summary>
    private readonly SolidBrush[] _poiBrushes;
    private readonly RectangleF[][] _poiRects;
    private readonly int[] _poiCounts;

    /// <summary>
    /// <c>FillRectangles</c> 용 길이별 스크래치. 키는 (묶음 첨자 &lt;&lt; 16) | 길이다.
    ///
    /// 화면에 보이는 수가 흔들려도 몇 가지 길이만 오가므로 곧 재사용으로 수렴한다.
    /// </summary>
    private readonly Dictionary<int, RectangleF[]> _scratch = [];

    private readonly SolidBrush _sleepVeil = new(Color.FromArgb(150, 18, 18, 22));
    private readonly Pen _movePen = new(Color.FromArgb(70, 150, 170, 210));
    private readonly Pen _selectedPen = new(Color.FromArgb(230, 220, 190, 60), 2f);
    private readonly Pen _selectedLinePen = new(Color.FromArgb(180, 220, 190, 60), 2.5f);
    private readonly Pen _workingPen = new(Color.FromArgb(200, 245, 245, 250));
    private readonly Pen _fightingPen = new(Color.FromArgb(230, 235, 70, 60), 1.6f);
    private readonly Pen _playerPen = new(Color.FromArgb(230, 230, 235, 245), 1.5f);
    private readonly SolidBrush _playerBrush = new(Color.FromArgb(235, 120, 200, 255));

    /// <summary>렌더러를 만든다. 존 원과 아키타입 팔레트는 여기서 한 번만 만든다.</summary>
    public MapRenderer(MasterDataSet data)
    {
        ArgumentNullException.ThrowIfNull(data);

        _data = data;
        _zones = BuildZoneCircles(data);
        _worldBounds = BuildWorldBounds(data, _zones);

        int codes = 1;

        foreach (ArchetypeDef archetype in data.Archetypes.Archetypes)
        {
            codes = Math.Max(codes, archetype.Code.Value + 1);
        }

        _archetypeBrushes = new SolidBrush[codes];
        _archetypeRects = new RectangleF[codes][];
        _archetypeCounts = new int[codes];

        for (int code = 0; code < codes; code++)
        {
            _archetypeBrushes[code] = new SolidBrush(ColorOf(code));
            _archetypeRects[code] = new RectangleF[EntityInterpolator.MaxEntities];
        }

        int types = Enum.GetValues<PoiType>().Length;

        _poiBrushes = new SolidBrush[types];
        _poiRects = new RectangleF[types][];
        _poiCounts = new int[types];

        for (int type = 0; type < types; type++)
        {
            _poiBrushes[type] = new SolidBrush(ShadeOf((PoiType)type));
            _poiRects[type] = new RectangleF[data.Pois.Count];
        }
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
    /// 엔티티를 그린다. <see cref="DrawWorld"/> 뒤에 부른다.
    /// </summary>
    /// <param name="g">그래픽스.</param>
    /// <param name="viewport">그릴 영역.</param>
    /// <param name="entities">보간된 엔티티 (<see cref="EntityInterpolator.Entities"/>).</param>
    /// <param name="myPlayer">이 창의 <c>PlayerId</c>. 채운 삼각형이 된다.</param>
    /// <param name="selectedNpc">선택된 NPC 첨자. 없으면 -1.</param>
    public void DrawEntities(
        Graphics g, Rectangle viewport, ReadOnlySpan<EntityState> entities, int myPlayer, int selectedNpc)
    {
        ArgumentNullException.ThrowIfNull(g);

        Array.Clear(_archetypeCounts);

        // 이동선이 먼저다. 엔티티 밑에 깔려야 선이 점을 가리지 않는다.
        DrawMoveLines(g, viewport, entities, selectedNpc);

        foreach (EntityState entity in entities)
        {
            if (entity.Kind == (byte)EntityKind.Player)
            {
                DrawPlayer(g, viewport, entity, entity.Id == myPlayer);
                continue;
            }

            Collect(viewport, entity);
        }

        // 색마다 한 번. 개별 호출 256번보다 확실히 빠르다 (docs/20 §9.1).
        Flush(g, _archetypeBrushes, _archetypeRects, _archetypeCounts);

        DrawOutlines(g, viewport, entities, selectedNpc);
    }

    /// <summary>
    /// 화면 좌표에 가장 가까운 NPC. <see cref="EntityPickRadius"/> 안에 없으면 false.
    /// 좌클릭 선택이 쓴다 (docs/20 §9.4).
    /// </summary>
    public bool TryPickNpc(
        Point screen, Rectangle viewport, ReadOnlySpan<EntityState> entities, out int npc)
    {
        float best = EntityPickRadius * EntityPickRadius;

        npc = -1;

        foreach (EntityState entity in entities)
        {
            if (entity.Kind != (byte)EntityKind.Npc)
            {
                continue;
            }

            PointF at = Camera.ToScreen(entity.X, entity.Z, viewport);
            float dx = at.X - screen.X;
            float dy = at.Y - screen.Y;
            float distance = (dx * dx) + (dy * dy);

            if (distance <= best)
            {
                best = distance;
                npc = entity.Id;
            }
        }

        return npc >= 0;
    }

    /// <summary>
    /// 아키타입 색. 황금각 팔레트다 (docs/20 §9.3).
    ///
    /// <b>채도·명도를 고정한다.</b> 색상만 돌리면 40종이 같은 밝기로 나와서 배경 회색조와
    /// 확실히 갈리고, 어느 하나가 유난히 튀지 않는다.
    /// </summary>
    public static Color ColorOf(int archetypeCode)
    {
        var hue = (float)(archetypeCode * GoldenAngleDegrees % 360.0);

        return FromHsv(hue, 0.62f, 0.92f);
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
        _sleepVeil.Dispose();
        _movePen.Dispose();
        _selectedPen.Dispose();
        _selectedLinePen.Dispose();
        _workingPen.Dispose();
        _fightingPen.Dispose();
        _playerPen.Dispose();
        _playerBrush.Dispose();

        foreach (SolidBrush brush in _archetypeBrushes)
        {
            brush.Dispose();
        }

        foreach (SolidBrush brush in _poiBrushes)
        {
            brush.Dispose();
        }
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

    // ---------------------------------------------------------------- 엔티티

    /// <summary>
    /// 사각 하나를 아키타입 묶음에 넣는다. 실제 그리기는 색별 배치 호출이다.
    ///
    /// <b>화면 밖은 버린다.</b> AOI 가 이미 1,200m 로 잘랐지만 줌을 키우면 그 안에서도
    /// 대부분이 밖으로 나간다.
    /// </summary>
    private void Collect(Rectangle viewport, in EntityState entity)
    {
        PointF at = Camera.ToScreen(entity.X, entity.Z, viewport);

        if (!viewport.Contains((int)at.X, (int)at.Y))
        {
            return;
        }

        int code = entity.Archetype < _archetypeCounts.Length ? entity.Archetype : 0;
        int index = _archetypeCounts[code];

        if (index >= _archetypeRects[code].Length)
        {
            return;
        }

        float half = NpcSize / 2f;

        _archetypeRects[code][index] = new RectangleF(at.X - half, at.Y - half, NpcSize, NpcSize);
        _archetypeCounts[code] = index + 1;
    }

    /// <summary>
    /// <c>TargetPoi</c> 가 있는 NPC 의 이동선. 선택된 NPC 는 굵게 (docs/20 §9.3).
    ///
    /// <b>"왜 저기로 가는가" 의 절반이 이 선이다.</b> 나머지 절반은 인스펙터가 답한다.
    /// </summary>
    private void DrawMoveLines(
        Graphics g, Rectangle viewport, ReadOnlySpan<EntityState> entities, int selectedNpc)
    {
        foreach (EntityState entity in entities)
        {
            if (entity.Kind != (byte)EntityKind.Npc || entity.TargetPoi == 0)
            {
                continue;
            }

            WorldPos to = _data.Pois[new PoiId(entity.TargetPoi)].Pos;
            PointF from = Camera.ToScreen(entity.X, entity.Z, viewport);
            PointF at = Camera.ToScreen(to.X, to.Z, viewport);

            g.DrawLine(entity.Id == selectedNpc ? _selectedLinePen : _movePen, from, at);
        }
    }

    /// <summary>
    /// 겉보기 상태 테두리와 선택 하이라이트. <b>채움 뒤에 그린다</b> — 앞에 그리면 덮인다.
    /// </summary>
    private void DrawOutlines(
        Graphics g, Rectangle viewport, ReadOnlySpan<EntityState> entities, int selectedNpc)
    {
        float half = NpcSize / 2f;

        foreach (EntityState entity in entities)
        {
            if (entity.Kind != (byte)EntityKind.Npc)
            {
                continue;
            }

            PointF at = Camera.ToScreen(entity.X, entity.Z, viewport);

            if (!viewport.Contains((int)at.X, (int)at.Y))
            {
                continue;
            }

            var box = new RectangleF(at.X - half, at.Y - half, NpcSize, NpcSize);

            switch ((VisualState)entity.Visual)
            {
                case VisualState.Working:
                    g.DrawRectangle(_workingPen, box);
                    break;

                case VisualState.Fighting:
                    g.DrawRectangle(_fightingPen, box);
                    break;

                case VisualState.Sleeping:
                    // 반투명은 덧칠로 낸다. 채움을 알파로 바꾸면 색별 배치 호출이 깨진다.
                    g.FillRectangle(_sleepVeil, box);
                    break;

                default:
                    break;
            }

            if (entity.Id == selectedNpc)
            {
                float radius = NpcSize + 4f;

                g.DrawEllipse(_selectedPen, at.X - radius, at.Y - radius, radius * 2, radius * 2);
            }
        }
    }

    /// <summary>플레이어 삼각형. 내 것은 채우고 남은 외곽선이다 (docs/20 §9.3).</summary>
    private void DrawPlayer(Graphics g, Rectangle viewport, in EntityState entity, bool mine)
    {
        PointF at = Camera.ToScreen(entity.X, entity.Z, viewport);

        if (!viewport.Contains((int)at.X, (int)at.Y))
        {
            return;
        }

        float size = PlayerSize;
        float sin = MathF.Sin(entity.Heading);
        float cos = MathF.Cos(entity.Heading);

        // 머리는 heading 쪽, 밑변은 그 반대. PlayerRegistry 와 같은 규약(Atan2(x, z))이라
        // 화면의 코가 실제로 가는 쪽을 가리킨다.
        Span<PointF> shape =
        [
            Offset(at, sin, cos, 0, size * 0.7f),
            Offset(at, sin, cos, -size * 0.45f, -size * 0.5f),
            Offset(at, sin, cos, size * 0.45f, -size * 0.5f),
        ];

        PointF[] points = [shape[0], shape[1], shape[2]];

        if (mine)
        {
            g.FillPolygon(_playerBrush, points);
        }

        g.DrawPolygon(_playerPen, points);
    }

    /// <summary>로컬 좌표를 heading 만큼 돌려 화면 좌표로.</summary>
    private static PointF Offset(PointF origin, float sin, float cos, float x, float z) =>
        new(origin.X + ((x * cos) + (z * sin)), origin.Y - ((z * cos) - (x * sin)));

    /// <summary>HSV → RGB. 황금각 팔레트가 쓴다.</summary>
    private static Color FromHsv(float hue, float saturation, float value)
    {
        float chroma = value * saturation;
        float sector = hue / 60f;
        float second = chroma * (1 - Math.Abs((sector % 2) - 1));
        float match = value - chroma;

        (float r, float g, float b) = (int)sector switch
        {
            0 => (chroma, second, 0f),
            1 => (second, chroma, 0f),
            2 => (0f, chroma, second),
            3 => (0f, second, chroma),
            4 => (second, 0f, chroma),
            _ => (chroma, 0f, second),
        };

        return Color.FromArgb(
            (int)((r + match) * 255), (int)((g + match) * 255), (int)((b + match) * 255));
    }

    // ---------------------------------------------------------------- POI

    private void DrawPois(Graphics g, Rectangle viewport)
    {
        ImmutableArray<PoiDef> pois = _data.Pois.Pois;
        bool labels = Camera.Zoom >= LabelZoom;
        float half = PoiSize / 2f;

        Array.Clear(_poiCounts);

        foreach (PoiDef poi in pois)
        {
            PointF at = Camera.ToScreen(poi.Pos.X, poi.Pos.Z, viewport);

            if (!viewport.Contains((int)at.X, (int)at.Y))
            {
                continue;
            }

            var type = (int)poi.Type;

            _poiRects[type][_poiCounts[type]++] =
                new RectangleF(at.X - half, at.Y - half, PoiSize, PoiSize);

            if (labels)
            {
                g.DrawString(poi.Id, _labelFont, _labelBrush, at.X + half + 2f, at.Y - 7f);
            }
        }

        Flush(g, _poiBrushes, _poiRects, _poiCounts);
    }

    /// <summary>
    /// 색별로 한 번씩 <c>FillRectangles</c> 를 부른다.
    ///
    /// <b>슬라이스를 그대로 넘길 수 없다</b> — GDI+ 는 배열 전체를 받는다. 대신 스크래치를
    /// 한 벌 들고 필요한 길이만큼만 다시 잡는다. 화면에 보이는 수는 잘 안 바뀌므로
    /// 실제로는 첫 몇 프레임 뒤 재사용된다.
    /// </summary>
    private void Flush(Graphics g, SolidBrush[] brushes, RectangleF[][] source, int[] counts)
    {
        for (int i = 0; i < counts.Length; i++)
        {
            int count = counts[i];

            if (count == 0)
            {
                continue;
            }

            RectangleF[] scratch = Scratch(i, count);

            Array.Copy(source[i], scratch, count);
            g.FillRectangles(brushes[i], scratch);
        }
    }

    /// <summary>길이가 정확히 <paramref name="count"/> 인 스크래치. 같은 길이면 다시 잡지 않는다.</summary>
    private RectangleF[] Scratch(int slot, int count)
    {
        _scratch.TryGetValue((slot << 16) | count, out RectangleF[]? array);

        if (array is null)
        {
            array = new RectangleF[count];
            _scratch[(slot << 16) | count] = array;
        }

        return array;
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
