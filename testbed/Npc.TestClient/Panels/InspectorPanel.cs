using System.Globalization;
using Npc.TestClient.Net;

namespace Npc.TestClient.Panels;

/// <summary>
/// 선택한 NPC 가 <b>왜 그 행동을 하는지</b> 답하는 화면. docs/20 §9.5.
///
/// <para>
/// <b>이 패널이 데모의 핵심이다.</b> 지도는 "무엇을 하는가" 까지만 보여 준다 —
/// 아키타입·존·POI·LOD·플랜 출처·goal·스텝 목록·현재 스텝·플래그·인벤토리·최근 사건은
/// NPC 서버 안쪽 상태이고, <b>게임서버는 그 답을 모른다</b> (docs/20 §2).
/// </para>
///
/// <para>
/// <b>위젯을 쌓지 않고 직접 그린다.</b> 한 줄짜리 라벨 스무 개를 두면 값이 바뀔 때마다
/// 레이아웃이 흔들리고, 스텝 수가 달라질 때 컨트롤을 만들고 지워야 한다.
/// </para>
/// </summary>
public sealed class InspectorPanel : Panel
{
    /// <summary>줄 높이(px).</summary>
    private const int LineHeight = 17;

    /// <summary>왼쪽 여백(px).</summary>
    private const int PadX = 10;

    private readonly Font _font = new("Consolas", 9f);
    private readonly Font _boldFont = new("Consolas", 9f, FontStyle.Bold);
    private readonly SolidBrush _text = new(Color.FromArgb(225, 225, 232));
    private readonly SolidBrush _dim = new(Color.FromArgb(140, 142, 152));
    private readonly SolidBrush _accent = new(Color.FromArgb(235, 200, 90));
    private readonly SolidBrush _good = new(Color.FromArgb(120, 210, 140));
    private readonly SolidBrush _bad = new(Color.FromArgb(235, 110, 100));

    /// <summary>NPC 서버 주소. 안 닿을 때 어디를 보고 있었는지 말한다.</summary>
    private readonly string _endpoint;

    private NpcTraceDto? _trace;
    private bool _reachable;
    private int _selected = -1;
    private int _y;

    /// <summary>
    /// 패널을 만든다.
    ///
    /// <b>상태를 public 속성으로 두지 않는다.</b> <c>Control</c> 파생의 공개 속성은 디자이너
    /// 직렬화 대상이라 분석기(WFO1000)가 특성을 요구한다 — 디자이너를 안 쓰는 프로젝트에서
    /// 그 특성을 네 개 붙이느니 갱신 통로를 하나로 좁히는 편이 낫다.
    /// </summary>
    public InspectorPanel(string endpoint)
    {
        _endpoint = endpoint ?? string.Empty;

        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.FromArgb(32, 32, 38);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }

    /// <summary>
    /// 보여 줄 것을 갈아 끼운다. <b>UI 스레드에서 부른다.</b>
    ///
    /// <b>바뀐 것이 없으면 다시 그리지 않는다.</b> 폴링은 1초이고 렌더 타이머는 60fps 라,
    /// 매 프레임 무효화하면 같은 글자를 초당 60번 그리게 된다 — 이 패널의 글자 재기(줄 바꿈)가
    /// 그만큼 비싸다.
    /// </summary>
    /// <param name="selected">선택된 NPC 첨자. -1 이면 선택 없음.</param>
    /// <param name="reachable">NPC 서버에 닿는가.</param>
    /// <param name="trace">가장 최근 추적. 없으면 null.</param>
    public void Update(int selected, bool reachable, NpcTraceDto? trace)
    {
        if (_selected == selected && _reachable == reachable && ReferenceEquals(_trace, trace))
        {
            return;
        }

        _selected = selected;
        _reachable = reachable;
        _trace = trace;

        Invalidate();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _font.Dispose();
            _boldFont.Dispose();
            _text.Dispose();
            _dim.Dispose();
            _accent.Dispose();
            _good.Dispose();
            _bad.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    protected override void OnPaint(PaintEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        base.OnPaint(e);

        Graphics g = e.Graphics;

        _y = 8;

        if (_selected < 0)
        {
            Line(g, "NPC 를 고른다 — 좌클릭 또는 Tab", _dim);
            return;
        }

        if (!_reachable)
        {
            Line(g, $"NPC 서버 미연결 ({_endpoint})", _bad);
            Line(g, "게임서버만으로는 플랜을 알 수 없다 — 그 답은 NPC 서버 안쪽에 있다.", _dim);
            return;
        }

        if (_trace is not { } trace)
        {
            Line(g, $"npc #{_selected} — 응답을 기다리는 중…", _dim);
            return;
        }

        if (!trace.Found)
        {
            Line(g, $"npc #{trace.Npc} — NPC 서버의 로스터에 없다", _bad);
            Line(g, "게임서버와 --npcs·--zone 이 다를 때 이렇게 된다.", _dim);
            return;
        }

        DrawIdentity(g, trace);
        DrawPlan(g, trace);
        DrawSteps(g, trace);
        DrawState(g, trace);
        DrawRecent(g, trace);
    }

    // ---------------------------------------------------------------- 구획

    private void DrawIdentity(Graphics g, NpcTraceDto trace)
    {
        Line(g, $"npc #{trace.Npc}  {trace.Archetype}", _text, bold: true);
        Line(g, $"zone  {trace.Zone}", _text);
        Line(g, $"poi   {(trace.Poi.Length == 0 ? "(이동 중)" : trace.Poi)}", _text);
        Line(g, $"home  {trace.HomePoi}   work {(trace.WorkPoi.Length == 0 ? "-" : trace.WorkPoi)}", _dim);

        // LOD 0 이 관측 중이다 (docs/11 §4). 플레이어가 다가가면 여기가 내려간다.
        Line(
            g,
            $"lod   {trace.Lod}   hp {trace.Hp}   sta {trace.Stamina}   tick {trace.Tick}",
            trace.Lod == 0 ? _good : _text);

        Gap();
    }

    private void DrawPlan(Graphics g, NpcTraceDto trace)
    {
        // 출처가 fallback 이면 LLM 이 안 닿은 것이다. demo_blackout 이 이것을 보게 한다
        // (docs/20 §11.3) — 그 회차의 성공은 "여기가 fallback 으로 내려가고 아무 일도 안 난다" 다.
        SolidBrush kind = trace.PlanKind switch
        {
            "bucket" => _good,
            "individual" => _accent,
            _ => _dim,
        };

        Line(g, $"plan  #{trace.PlanId}  [{trace.PlanKind}]", kind, bold: true);
        Line(g, $"  goal   {trace.PlanGoal}", _text);
        Line(g, $"  bucket {trace.PlanBucket}", _dim);
        Line(
            g,
            $"  age {trace.PlanAgeTicks} ticks{(trace.PlanLoop ? "  loop" : string.Empty)}"
            + (trace.PendingPlanId != 0 ? $"   pending #{trace.PendingPlanId}" : string.Empty),
            _dim);

        Gap();
    }

    private void DrawSteps(Graphics g, NpcTraceDto trace)
    {
        Line(g, $"steps  ({trace.StepStatus})", _text, bold: true);

        if (trace.Steps.Length == 0)
        {
            Line(g, "  (없음)", _dim);
            Gap();

            return;
        }

        foreach (TraceStepDto step in trace.Steps)
        {
            string marker = step.Current ? "▶" : " ";
            string poi = step.Poi.Length == 0 ? string.Empty : $" {step.Poi}";
            string count = step.Count > 0 ? $" ×{step.Count}" : string.Empty;

            Line(
                g,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{marker} {step.Index} {step.Action,-14}{poi}{count}  ({step.TimeoutSeconds}s)"),
                step.Current ? _accent : _dim);
        }

        Gap();
    }

    private void DrawState(Graphics g, NpcTraceDto trace)
    {
        Line(g, "flags", _text, bold: true);
        Line(g, trace.Flags.Length == 0 ? "  (없음)" : "  " + string.Join(" | ", trace.Flags), _dim);

        Line(g, "inventory", _text, bold: true);
        Line(g, trace.Inventory.Length == 0 ? "  (비었다)" : "  " + string.Join(", ", trace.Inventory), _dim);

        Gap();
    }

    private void DrawRecent(Graphics g, NpcTraceDto trace)
    {
        Line(g, "recent", _text, bold: true);

        if (trace.Recent.Length == 0)
        {
            Line(g, "  (없음)", _dim);
            return;
        }

        foreach (TraceEventDto recent in trace.Recent)
        {
            Line(
                g,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"  -{recent.AgoTicks,5}t  {recent.Kind,-20} {recent.Subject}"),
                _dim);
        }
    }

    // ---------------------------------------------------------------- 그리기

    /// <summary>
    /// 한 줄. <b>폭을 넘으면 접는다</b> — 안 접으면 flags·inventory 처럼 긴 줄이
    /// 오른쪽에서 잘려 나가고, 잘린 자리에 무엇이 있었는지 알 수 없다.
    /// </summary>
    private void Line(Graphics g, string text, Brush brush, bool bold = false)
    {
        if (_y > Height)
        {
            return;   // 아래로 넘쳤다. 잘라 낸다 — 스크롤을 붙일 만큼의 내용이 아니다
        }

        Font font = bold ? _boldFont : _font;
        var box = new RectangleF(PadX, _y, Math.Max(1, Width - (PadX * 2)), Height - _y);

        g.DrawString(text, font, brush, box);

        SizeF size = g.MeasureString(text, font, box.Size.ToSize());

        _y += Math.Max(LineHeight, (int)Math.Ceiling(size.Height));
    }

    private void Gap() => _y += LineHeight / 2;
}
