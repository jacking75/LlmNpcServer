using System.Globalization;
using Npc.Core;
using Npc.MasterData;
using Npc.TestBed.Protocol;
using Npc.TestClient.Net;

// System.Windows.Forms.Control 과 Npc.TestBed.Protocol.Control 이 이름이 겹친다.
// 이 파일에서 Control 은 언제나 위젯이다 — 프로토콜 쪽은 ControlKind 만 쓴다.
using Control = System.Windows.Forms.Control;

namespace Npc.TestClient.Panels;

/// <summary>
/// 세계를 흔들어 보는 자리. docs/20 §8.3 · §9.2 · §11.4.
///
/// <para>
/// <b>여기서 보내는 것은 전부 게임서버로 간다.</b> 존 상태·날씨·시간·고장 주입·despawn 은
/// <c>Control</c> 메시지 하나로 나가고, NPC 서버는 <b>이 패널의 존재를 모른다</b> —
/// 링크로 들어오는 것은 평소와 같은 <c>GameEvent</c> 뿐이다 (docs/20 §8.3).
/// 그것이 "게임서버가 바뀌어도 NPC 서버는 안 바뀐다" 는 주장의 실물이다.
/// </para>
///
/// <para>
/// <b>킬스위치만 다른 길로 간다.</b> 그것은 NPC 서버 안쪽 상태라 링크로 보낼 수단이 없고,
/// 만들면 N 규칙을 어긴다 (docs/20 §11.4). <c>POST /control/killswitch</c> 로 직접 간다 —
/// <c>--dev-control</c> 없이 기동한 NPC 서버에는 그 라우트가 <b>아예 없어서</b> 404 다.
/// </para>
///
/// <para>
/// <b>이 패널만 진짜 위젯을 쓴다.</b> 인스펙터·로그는 값을 보여 주기만 해서 직접 그리는 편이
/// 쌌지만, 드롭다운·슬라이더·스핀 박스를 GDI+ 로 다시 만들 이유는 없다 —
/// 그 절반이 공짜라서 WinForms 를 골랐다 (docs/20 §9.1).
/// </para>
/// </summary>
public sealed class ControlPanel : Panel
{
    /// <summary>고장 주입 슬라이더의 상한(%). 프로토콜은 만분율(‱)이라 100배 해서 보낸다.</summary>
    public const int FaultMaxPercent = 100;

    /// <summary>시간 건너뛰기 기본값(게임 분). 한 시간이면 시간대가 한 칸 움직인다.</summary>
    public const int DefaultSkipMinutes = 60;

    /// <summary>시간 건너뛰기 상한(게임 분). 하루를 넘기면 무엇을 봤는지 알 수 없다.</summary>
    public const int MaxSkipMinutes = 24 * 60;

    private const int Pad = 10;
    private const int Row = 30;
    private const int LabelWidth = 76;
    private const int FieldX = Pad + LabelWidth;

    private readonly GameConnection _connection;
    private readonly NpcServerHttp _npcHttp;
    private readonly CancellationToken _stopping;

    private readonly Font _font = new("Segoe UI", 9f);
    private readonly Font _headFont = new("Segoe UI", 9f, FontStyle.Bold);
    private readonly Font _monoFont = new("Consolas", 8.5f);

    private readonly ComboBox _zone = new();
    private readonly ComboBox _region = new();
    private readonly ComboBox _climate = new();
    private readonly NumericUpDown _minutes = new();
    private readonly TrackBar _fail = new();
    private readonly TrackBar _drop = new();
    private readonly Label _failValue = new();
    private readonly Label _dropValue = new();
    private readonly Button _despawn = new();
    private readonly Label _result = new();
    private readonly Label _llm = new();

    private int _selected = -1;
    private string _llmText = string.Empty;
    private int _y = Pad;

    /// <summary>
    /// 패널을 만든다.
    ///
    /// <b>상태를 public 속성으로 두지 않는다</b> — <c>Control</c> 파생의 공개 속성은 디자이너
    /// 직렬화 대상이라 분석기(WFO1000)가 특성을 요구한다. 갱신 통로는 <see cref="Update"/> 하나다.
    /// </summary>
    /// <param name="data">마스터데이터. 존 목록이 여기서 나온다.</param>
    /// <param name="connection">게임서버 연결. 제어는 전부 이리로 나간다.</param>
    /// <param name="npcHttp">NPC 서버 HTTP. 킬스위치만 이리로 간다 (docs/20 §11.4).</param>
    /// <param name="stopping">창이 닫힐 때 취소되는 토큰. 킬스위치 요청이 이것을 본다.</param>
    public ControlPanel(
        MasterDataSet data, GameConnection connection, NpcServerHttp npcHttp, CancellationToken stopping)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(npcHttp);

        _connection = connection;
        _npcHttp = npcHttp;
        _stopping = stopping;

        AutoScroll = true;
        BackColor = Color.FromArgb(32, 32, 38);
        ForeColor = Color.Gainsboro;
        Font = _font;

        Build(data);
    }

    /// <summary>
    /// 화면을 갱신한다. <b>UI 스레드에서 부른다.</b>
    ///
    /// <b>바뀐 것이 없으면 아무것도 안 한다.</b> 렌더 타이머가 60fps 인데 매 프레임
    /// <c>Label.Text</c> 를 쓰면 그때마다 무효화가 걸린다.
    /// </summary>
    /// <param name="selectedNpc">선택된 NPC 첨자. -1 이면 despawn 버튼을 잠근다.</param>
    /// <param name="reachable">NPC 서버에 닿는가.</param>
    /// <param name="metrics">가장 최근 <c>GET /metrics</c>. 없으면 null.</param>
    public void Update(int selectedNpc, bool reachable, MetricsDto? metrics)
    {
        if (_selected != selectedNpc)
        {
            _selected = selectedNpc;
            _despawn.Enabled = selectedNpc >= 0;
            _despawn.Text = selectedNpc < 0
                ? "despawn — NPC 를 먼저 고른다"
                : string.Create(CultureInfo.InvariantCulture, $"npc #{selectedNpc} despawn");
        }

        // 킬스위치가 들었는지는 이 숫자 하나로 본다 — 끊긴 뒤로는 늘지 않는다 (docs/20 §11.3).
        string llm = !reachable
            ? "NPC 서버 미연결 — 킬스위치를 누를 곳이 없다"
            : metrics is null
                ? "llm 호출 —"
                : string.Create(CultureInfo.InvariantCulture, $"llm 호출 {metrics.LlmCalls:N0}");

        if (_llmText != llm)
        {
            _llmText = llm;
            _llm.Text = llm;
            _llm.ForeColor = reachable ? Color.Gainsboro : Color.FromArgb(200, 130, 120);
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _font.Dispose();
            _headFont.Dispose();
            _monoFont.Dispose();
        }

        base.Dispose(disposing);
    }

    // ---------------------------------------------------------------- 조립

    private void Build(MasterDataSet data)
    {
        Head("세계 — 게임서버로 나간다");

        foreach (ZoneDef zone in data.Zones.Zones)
        {
            _zone.Items.Add(new ZoneItem(zone.Code.Value, zone.Id));
        }

        Combo(_zone, 250);

        if (_zone.Items.Count > 0)
        {
            _zone.SelectedIndex = 0;
        }

        Field("존", _zone);

        foreach (RegionState state in Enum.GetValues<RegionState>())
        {
            _region.Items.Add(state);
        }

        Combo(_region, 130);
        _region.SelectedIndex = 0;
        FieldWithButton("지역 상태", _region, "적용", OnSetZoneState);

        foreach (Climate climate in Enum.GetValues<Climate>())
        {
            _climate.Items.Add(climate);
        }

        Combo(_climate, 130);
        _climate.SelectedIndex = 0;
        FieldWithButton("날씨", _climate, "적용", OnSetWeather);

        _minutes.Minimum = 1;
        _minutes.Maximum = MaxSkipMinutes;
        _minutes.Value = DefaultSkipMinutes;
        _minutes.Increment = 10;
        _minutes.Width = 130;
        _minutes.BackColor = Color.FromArgb(44, 44, 52);
        _minutes.ForeColor = Color.Gainsboro;
        _minutes.BorderStyle = BorderStyle.FixedSingle;
        FieldWithButton("시간(분)", _minutes, "건너뛰기", OnSkipTime);

        // SkipTime 은 결정론 대상이 아니다 (docs/20 §8.3). 눌러 놓고 잊으면
        // "시나리오가 왜 다르게 흘렀나" 를 한참 찾게 되므로 화면에 적어 둔다.
        Note("건너뛰면 진행 중인 이동·작업이 한꺼번에 끝난다. 시나리오 회차에서는 쓰지 않는다.");

        Slider(_fail, _failValue, "실패 주입", OnFailRate);
        Slider(_drop, _dropValue, "드롭 주입", OnDropRate);
        Note("드롭은 응답을 아예 주지 않는다 — NPC 서버가 타임아웃을 합성해야만 진행된다.");

        _despawn.Text = "despawn — NPC 를 먼저 고른다";
        _despawn.Enabled = false;
        _despawn.Click += OnDespawn;
        Wide(_despawn);

        Head("NPC 서버 — 킬스위치 (--dev-control 필요)");

        // 대상 이름은 KillSwitchState.TargetNames 와 같은 문자열이다. 여기서 다시 적는 이유는
        // 클라이언트가 Npc.Core 의 열거형을 참조하긴 하지만 <b>보내는 것은 질의 문자열</b>이라,
        // 서버가 받아 파싱하는 값과 눈으로 맞춰 두는 편이 낫기 때문이다.
        Buttons(["T2", "T1", "PlanStore"], OnKillSwitch);

        _result.Text = "(아직 누르지 않았다)";
        _result.ForeColor = Color.FromArgb(150, 152, 162);
        Wide(_result, height: 22);

        _llm.Text = "llm 호출 —";
        _llm.Font = _monoFont;
        Wide(_llm, height: 22);
    }

    // ---------------------------------------------------------------- 동작

    private void OnSetZoneState(object? sender, EventArgs e) =>
        _connection.SendControl(ControlKind.SetZoneState, SelectedZone(), (byte)(RegionState)_region.SelectedItem!, 0);

    private void OnSetWeather(object? sender, EventArgs e) =>
        _connection.SendControl(ControlKind.SetWeather, SelectedZone(), (byte)(Climate)_climate.SelectedItem!, 0);

    private void OnSkipTime(object? sender, EventArgs e) =>
        _connection.SendControl(ControlKind.SkipTime, 0, 0, (int)_minutes.Value);

    private void OnFailRate(object? sender, EventArgs e)
    {
        _failValue.Text = Percent(_fail.Value);

        // 슬라이더는 %, 프로토콜은 ‱ 다 (docs/20 §8.3).
        _connection.SendControl(ControlKind.SetFaultRate, 0, 0, _fail.Value * 100);
    }

    private void OnDropRate(object? sender, EventArgs e)
    {
        _dropValue.Text = Percent(_drop.Value);
        _connection.SendControl(ControlKind.SetFaultRate, 0, 1, _drop.Value * 100);
    }

    private void OnDespawn(object? sender, EventArgs e)
    {
        if (_selected >= 0)
        {
            _connection.SendControl(ControlKind.Despawn, 0, 0, _selected);
        }
    }

    /// <summary>
    /// 킬스위치를 누른다.
    ///
    /// <b><c>async void</c> 인 것이 맞다</b> — 이벤트 핸들러이고, 예외는 안쪽에서 전부 잡는다.
    /// 창이 닫히는 중에 응답이 오면 처분된 라벨에 쓰게 되므로 그것도 막는다.
    /// </summary>
    private async void OnKillSwitch(object? sender, EventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        string target = button.Text;

        _result.Text = $"{target} …";
        _result.ForeColor = Color.FromArgb(150, 152, 162);

        string message;

        try
        {
            message = await _npcHttp.KillSwitchAsync(target, _stopping);
        }
        catch (OperationCanceledException)
        {
            return;   // 창이 닫혔다
        }

        if (IsDisposed || Disposing)
        {
            return;
        }

        _result.Text = message;

        // 끊겼으면 초록이 아니라 노랑이다 — 이것은 성공이지만 축하할 일은 아니다.
        _result.ForeColor = message.StartsWith("미연결", StringComparison.Ordinal)
            ? Color.FromArgb(200, 130, 120)
            : Color.FromArgb(235, 200, 90);
    }

    private ushort SelectedZone() => _zone.SelectedItem is ZoneItem zone ? zone.Code : (ushort)0;

    private static string Percent(int value) =>
        string.Create(CultureInfo.InvariantCulture, $"{value}%");

    // ---------------------------------------------------------------- 레이아웃

    private void Head(string text)
    {
        _y += Pad;

        var label = new Label
        {
            Text = text,
            Font = _headFont,
            ForeColor = Color.FromArgb(200, 202, 212),
            AutoSize = false,
            Bounds = new Rectangle(Pad, _y, Width - (Pad * 2), 20),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        Controls.Add(label);
        _y += 24;
    }

    private void Note(string text)
    {
        var label = new Label
        {
            Text = text,
            Font = _monoFont,
            ForeColor = Color.FromArgb(135, 137, 147),
            AutoSize = false,
            Bounds = new Rectangle(Pad, _y, Width - (Pad * 2), 30),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        Controls.Add(label);
        _y += 32;
    }

    private void Field(string text, Control field)
    {
        Caption(text);

        field.Location = new Point(FieldX, _y - 2);
        Controls.Add(field);

        _y += Row;
    }

    private void FieldWithButton(string text, Control field, string action, EventHandler onClick)
    {
        Caption(text);

        field.Location = new Point(FieldX, _y - 2);
        Controls.Add(field);

        var button = new Button
        {
            Text = action,
            Bounds = new Rectangle(FieldX + field.Width + 8, _y - 3, 96, 25),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(52, 52, 62),
            ForeColor = Color.Gainsboro,
        };

        button.Click += onClick;
        Controls.Add(button);

        _y += Row;
    }

    private void Slider(TrackBar bar, Label value, string text, EventHandler onChanged)
    {
        Caption(text);

        bar.Minimum = 0;
        bar.Maximum = FaultMaxPercent;
        bar.TickFrequency = 10;
        bar.SmallChange = 1;
        bar.LargeChange = 10;
        bar.Bounds = new Rectangle(FieldX, _y - 6, 200, 34);
        bar.BackColor = BackColor;
        bar.ValueChanged += onChanged;

        value.Text = Percent(0);
        value.Font = _monoFont;
        value.ForeColor = Color.FromArgb(180, 182, 192);
        value.AutoSize = false;
        value.Bounds = new Rectangle(FieldX + 208, _y, 50, 20);

        Controls.Add(bar);
        Controls.Add(value);

        _y += Row + 6;
    }

    private void Buttons(string[] labels, EventHandler onClick)
    {
        int x = Pad;

        foreach (string label in labels)
        {
            var button = new Button
            {
                Text = label,
                Bounds = new Rectangle(x, _y, 108, 26),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(62, 46, 46),
                ForeColor = Color.Gainsboro,
            };

            button.Click += onClick;
            Controls.Add(button);

            x += 114;
        }

        _y += Row + 2;
    }

    private void Wide(Control control, int height = 26)
    {
        control.Bounds = new Rectangle(Pad, _y, Math.Max(120, Width - (Pad * 2)), height);
        control.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        if (control is Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = Color.FromArgb(52, 52, 62);
            button.ForeColor = Color.Gainsboro;
        }

        Controls.Add(control);

        _y += height + 6;
    }

    private void Caption(string text)
    {
        var label = new Label
        {
            Text = text,
            ForeColor = Color.FromArgb(170, 172, 182),
            AutoSize = false,
            Bounds = new Rectangle(Pad, _y + 2, LabelWidth, 20),
        };

        Controls.Add(label);
    }

    private static void Combo(ComboBox combo, int width)
    {
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.FlatStyle = FlatStyle.Flat;
        combo.BackColor = Color.FromArgb(44, 44, 52);
        combo.ForeColor = Color.Gainsboro;
        combo.Width = width;
    }

    /// <summary>드롭다운 한 줄. <c>ToString</c> 이 곧 화면 글자다.</summary>
    private sealed record ZoneItem(ushort Code, string Id)
    {
        /// <inheritdoc />
        public override string ToString() => Id;
    }
}

/// <summary>
/// 링크와 NPC 서버 계측. docs/20 §9.5.
///
/// <para>
/// <b>왜 <c>ControlPanel.cs</c> 에 있는가.</b> T6-32 의 파일 목록에 새 파일이 없다.
/// 이 패널은 §9.5 가 인스펙터 절에 곁들여 적어 둔 한 줄("링크 패널에는 <c>GET /metrics</c> 의
/// 요약을 같이 띄운다")인데 T6-30 에서 빠졌고, T6-32 의 완료 조건이
/// "킬스위치 버튼 후 <c>/metrics</c> 의 LLM 호출이 0 이 된다" 라 <b>여기서 필요해졌다.</b>
/// 파일을 새로 만드는 대신 제어 패널 옆에 둔다 — 둘 다 "흔들고 그 결과를 본다" 는 한 쌍이다.
/// </para>
///
/// <para>
/// <b>두 출처를 한 화면에 놓는다.</b> 위쪽 절반은 게임서버가 본 링크(<c>LinkStatus</c>),
/// 아래쪽 절반은 NPC 서버가 스스로 말한 계측(<c>GET /metrics</c>)이다.
/// 둘이 어긋나면 그 자체가 정보다 — 이를테면 링크는 붙어 있는데 틱이 안 도는 경우다.
/// </para>
/// </summary>
public sealed class LinkPanel : Panel
{
    private const int LineHeight = 17;
    private const int PadX = 10;

    private readonly Font _font = new("Consolas", 9f);
    private readonly Font _boldFont = new("Consolas", 9f, FontStyle.Bold);
    private readonly SolidBrush _text = new(Color.FromArgb(225, 225, 232));
    private readonly SolidBrush _dim = new(Color.FromArgb(140, 142, 152));
    private readonly SolidBrush _good = new(Color.FromArgb(120, 210, 140));
    private readonly SolidBrush _bad = new(Color.FromArgb(235, 110, 100));

    private GameConnection? _connection;
    private MetricsDto? _metrics;
    private bool _reachable;
    private long _seenTick = -1;
    private int _y;

    /// <summary>패널을 만든다.</summary>
    public LinkPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.FromArgb(32, 32, 38);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }

    /// <summary>보여 줄 것을 갈아 끼운다. <b>UI 스레드에서 부른다.</b></summary>
    /// <param name="connection">게임서버 연결.</param>
    /// <param name="reachable">NPC 서버에 닿는가.</param>
    /// <param name="metrics">가장 최근 <c>GET /metrics</c>.</param>
    public void Update(GameConnection connection, bool reachable, MetricsDto? metrics)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // 링크 상태는 1초, 계측은 1초 주기다. 그 둘이 그대로면 화면도 그대로다.
        bool same = ReferenceEquals(_connection, connection)
            && ReferenceEquals(_metrics, metrics)
            && _reachable == reachable
            && _seenTick == connection.Link.GsTick;

        if (same)
        {
            return;
        }

        _connection = connection;
        _metrics = metrics;
        _reachable = reachable;
        _seenTick = connection.Link.GsTick;

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

        if (_connection is not { } connection)
        {
            Line(g, "(연결 대기)", _dim);
            return;
        }

        DrawClient(g, connection);
        DrawLink(g, connection.Link);
        DrawMetrics(g);
    }

    private void DrawClient(Graphics g, GameConnection connection)
    {
        long rtt = connection.RoundTripMillis;

        Line(g, "client → 게임서버", _text, bold: true);
        Line(
            g,
            $"  state {connection.State}  rtt {(rtt < 0 ? "--" : rtt.ToString(CultureInfo.InvariantCulture))}ms",
            connection.State == ConnectionState.Connected ? _good : _bad);
        Line(
            g,
            string.Create(
                CultureInfo.InvariantCulture,
                $"  snapshots {connection.SnapshotsReceived:N0}  attempts {connection.Attempts:N0}"),
            _dim);

        Gap();
    }

    private void DrawLink(Graphics g, LinkStatus link)
    {
        Line(g, "게임서버 ↔ NPC 서버 (링크)", _text, bold: true);
        Line(g, $"  connected {(link.Connected == 1 ? "yes" : "no")}", link.Connected == 1 ? _good : _bad);
        Line(
            g,
            string.Create(
                CultureInfo.InvariantCulture,
                $"  gs tick {link.GsTick:N0}   npc tick {link.NpcServerTick:N0}   lag {link.GsTick - link.NpcServerTick:N0}"),
            _text);
        Line(
            g,
            string.Create(
                CultureInfo.InvariantCulture,
                $"  commands in {link.CommandsIn:N0}   events out {link.EventsOut:N0}"),
            _dim);

        // 갭은 0 이 아니면 경보다 (N6). 드롭은 역압이 걸렸다는 뜻이라 0 이 아니어도 정상일 수 있다.
        Line(
            g,
            string.Create(CultureInfo.InvariantCulture, $"  dropped {link.Dropped:N0}   gaps {link.Gaps:N0}"),
            link.Gaps == 0 ? _dim : _bad);

        Gap();
    }

    private void DrawMetrics(Graphics g)
    {
        Line(g, "NPC 서버 (GET /metrics)", _text, bold: true);

        if (!_reachable)
        {
            Line(g, "  미연결 — 게임서버만으로는 틱 예산도 LLM 호출도 알 수 없다", _bad);
            return;
        }

        if (_metrics is not { } metrics)
        {
            Line(g, "  응답을 기다리는 중…", _dim);
            return;
        }

        if (metrics.Tick is { } tick)
        {
            // p99 20ms 가 예산이다 (CLAUDE.md §2.1). 넘으면 빨갛게.
            Line(
                g,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"  tick p50 {tick.P50Ms:F2}ms  p99 {tick.P99Ms:F2}ms  over {tick.Overruns:N0}"),
                tick.P99Ms <= 20.0 ? _good : _bad);
            Line(
                g,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"  ticks {tick.Ticks:N0}  gen0 {tick.Gen0Collections:N0}"),
                _dim);
            Line(
                g,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"  day {tick.GameDay}  {tick.GameHour:00}:00 {tick.TimeOfDay}"),
                _dim);
        }

        if (metrics.Replan is { } replan)
        {
            Line(
                g,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"  replan queue {replan.QueueDepth:N0}  deviations {replan.Deviations:N0}  forced {replan.InterruptsForced:N0}"),
                _dim);
        }

        // demo_blackout 이 보는 값이다 (docs/20 §11.3) — 끊긴 뒤로 늘지 않으면 성공이다.
        Line(g, string.Create(CultureInfo.InvariantCulture, $"  llm calls {metrics.LlmCalls:N0}"), _text);
    }

    private void Line(Graphics g, string text, Brush brush, bool bold = false)
    {
        if (_y > Height)
        {
            return;
        }

        g.DrawString(text, bold ? _boldFont : _font, brush, PadX, _y);

        _y += LineHeight;
    }

    private void Gap() => _y += LineHeight / 2;
}
