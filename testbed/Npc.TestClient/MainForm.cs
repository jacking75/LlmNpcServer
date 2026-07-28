using System.Globalization;
using Npc.MasterData;
using Npc.TestBed.Protocol;
using Npc.TestClient.Net;
using Npc.TestClient.Render;
using Npc.Wire;

// System.Windows.Forms.Control 과 Npc.TestBed.Protocol.Control 이 이름이 겹친다.
// 창 코드에서 Control 은 위젯이다 — 프로토콜 쪽은 쓰는 자리에서 이름을 붙인다.
using Control = System.Windows.Forms.Control;

namespace Npc.TestClient;

/// <summary>
/// 창 하나. docs/20 §9.2.
///
/// <code>
/// ┌───────────────────────────────┬──────────────────────┐
/// │                               │ [인스펙터][로그][제어] │
/// │           맵 패널              │                      │
/// │      (GDI+, DoubleBuffered)   │                      │
/// ├───────────────────────────────┴──────────────────────┤
/// │ day 2  09:14 Morning │ link ● 12ms  gs 5,412  npc … │
/// └──────────────────────────────────────────────────────┘
/// </code>
///
/// <para>
/// <b>수신은 배경에서, 그리기는 타이머에서.</b> <see cref="GameConnection"/> 이 받은 것을
/// 원자 참조로 놓고, 여기 16ms 타이머가 그것을 읽어 <c>Invalidate</c> 한다 —
/// <c>BeginInvoke</c> 로 프레임마다 UI 큐를 두드리지 않는다 (docs/20 §9.3).
/// </para>
///
/// <para>
/// <b>디자이너를 쓰지 않는다.</b> 레이아웃은 전부 여기 코드다.
/// </para>
/// </summary>
public sealed class MainForm : Form
{
    /// <summary>렌더 주기(ms). 약 60fps 다 (docs/20 §9.3).</summary>
    public const int RenderIntervalMillis = 16;

    /// <summary>입력 전송 주기(ms). 10Hz 다 (docs/20 §9.4).</summary>
    public const int NetworkIntervalMillis = 100;

    /// <summary><c>Ping</c> 주기(틱). 100ms × 10 = 1초 (docs/20 §8.2).</summary>
    public const int PingEveryNetworkTicks = 10;

    private readonly ClientOptions _options;
    private readonly MasterDataSet _data;
    private readonly GameConnection _connection;
    private readonly CancellationTokenSource _stopping = new();
    private readonly System.Windows.Forms.Timer _render = new() { Interval = RenderIntervalMillis };
    private readonly System.Windows.Forms.Timer _network = new() { Interval = NetworkIntervalMillis };

    private readonly SplitContainer _split = new();
    private readonly MapPanel _map = new();
    private readonly MapRenderer _renderer;
    private readonly ToolTip _tip = new() { InitialDelay = 250, ReshowDelay = 100 };
    private readonly TabControl _tabs = new();
    private readonly ToolStripStatusLabel _clock = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _link = new() { Spring = true, TextAlign = ContentAlignment.MiddleRight };

    private Task? _connectionTask;
    private long _networkTicks;
    private bool _fitted;
    private bool _dragging;
    private Point _dragFrom;
    private string _tipText = string.Empty;

    /// <summary>창을 만든다. 마스터데이터는 여기서 읽는다 — 없으면 기동 실패다.</summary>
    public MainForm(ClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _data = MasterDataLoader.Load(options.ResolveMasterData());
        _connection = new GameConnection(options.Host, options.Port);
        _renderer = new MapRenderer(_data);

        Text = $"Npc.TestClient — {options.Host}:{options.Port}";
        ClientSize = new Size(1_440, 900);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        BackColor = Color.FromArgb(24, 24, 28);

        Controls.Add(BuildBody());
        Controls.Add(BuildStatusBar());

        _render.Tick += OnRender;
        _network.Tick += OnNetwork;
    }

    /// <summary>마스터데이터. 렌더러·로그 패널이 id 를 사람 말로 푸는 원천이다 (docs/20 §3.2).</summary>
    public MasterDataSet Data => _data;

    /// <summary>게임서버 연결.</summary>
    public GameConnection Connection => _connection;

    /// <summary>맵이 그려지는 패널.</summary>
    public Control Map => _map;

    /// <summary>지도 렌더러. 카메라가 여기 있다.</summary>
    public MapRenderer Renderer => _renderer;

    /// <inheritdoc />
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        // <b>분할 위치는 여기서 정한다.</b> 생성자에서 주면 아직 기본 크기(150px)라
        // Panel2MinSize 가 예외를 던지고, SplitterDistance 는 조용히 잘려 우측 패널이 사라진다.
        // 둘 다 실제로 겪은 증상이다 — 앞의 것은 크래시, 뒤의 것은 폭 10px 짜리 탭이었다.
        _split.Panel2MinSize = Math.Min(260, Math.Max(1, _split.Width / 4));
        _split.SplitterDistance = Math.Clamp(
            _split.Width - SidePanelWidth,
            _split.Panel1MinSize,
            Math.Max(_split.Panel1MinSize, _split.Width - _split.Panel2MinSize));

        _connectionTask = Task.Run(() => _connection.RunAsync(_stopping.Token), CancellationToken.None);

        _render.Start();
        _network.Start();
    }

    /// <inheritdoc />
    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        _render.Stop();
        _network.Stop();

        await _stopping.CancelAsync().ConfigureAwait(true);

        if (_connectionTask is { } task)
        {
            try
            {
                await task.ConfigureAwait(true);
            }
            catch (Exception)
            {
                // 취소로 끝난다.
            }
        }

        await _connection.DisposeAsync().ConfigureAwait(true);

        base.OnFormClosing(e);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _render.Dispose();
            _network.Dispose();
            _stopping.Dispose();
            _renderer.Dispose();
            _tip.Dispose();
        }

        base.Dispose(disposing);
    }

    // ---------------------------------------------------------------- 레이아웃

    /// <summary>우측 패널 너비(px). 인스펙터의 스텝 목록이 접히지 않는 최소치다.</summary>
    public const int SidePanelWidth = 420;

    private Control BuildBody()
    {
        _split.Dock = DockStyle.Fill;
        _split.Orientation = Orientation.Vertical;
        _split.FixedPanel = FixedPanel.Panel2;
        _split.BackColor = Color.FromArgb(40, 40, 46);

        _map.Dock = DockStyle.Fill;
        _map.Paint += OnPaintMap;
        _map.MouseWheel += OnMapMouseWheel;
        _map.MouseDown += OnMapMouseDown;
        _map.MouseMove += OnMapMouseMove;
        _map.MouseUp += OnMapMouseUp;

        _tabs.Dock = DockStyle.Fill;
        _tabs.TabPages.Add(NewTab("인스펙터"));
        _tabs.TabPages.Add(NewTab("로그"));
        _tabs.TabPages.Add(NewTab("제어"));
        _tabs.TabPages.Add(NewTab("링크"));

        _split.Panel1.Controls.Add(_map);
        _split.Panel2.Controls.Add(_tabs);

        return _split;
    }

    /// <summary>탭 한 장. 내용은 T6-30~T6-32 가 채운다.</summary>
    private static TabPage NewTab(string title) => new(title)
    {
        BackColor = Color.FromArgb(32, 32, 38),
        ForeColor = Color.Gainsboro,
        Padding = new Padding(6),
    };

    private Control BuildStatusBar()
    {
        var status = new StatusStrip
        {
            BackColor = Color.FromArgb(40, 40, 46),
            ForeColor = Color.Gainsboro,
            SizingGrip = false,
        };

        status.Items.Add(_clock);
        status.Items.Add(_link);

        return status;
    }

    // ---------------------------------------------------------------- 타이머

    private void OnRender(object? sender, EventArgs e)
    {
        UpdateStatus();

        _map.Invalidate();
    }

    private void OnNetwork(object? sender, EventArgs e)
    {
        _networkTicks++;

        if (_networkTicks % PingEveryNetworkTicks == 0)
        {
            _connection.SendPing();
        }
    }

    private void UpdateStatus()
    {
        SnapshotFrame? frame = _connection.Latest;

        _clock.Text = frame is { Snapshot: var shot }
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"day {shot.GameDay}  {shot.GameHour:00}:00 {(TimeOfDayLabel)shot.TimeOfDay}   tick {shot.Tick}   entities {shot.EntityCount}")
            : _connection.State switch
            {
                ConnectionState.Connecting => $"게임서버를 찾는 중… ({_connection.Attempts}회)",
                ConnectionState.Connected => "접속됨 — 첫 스냅샷을 기다리는 중",
                _ => "게임서버 미연결",
            };

        LinkStatus link = _connection.Link;
        long rtt = _connection.RoundTripMillis;
        string dot = _connection.State == ConnectionState.Connected ? "●" : "○";
        long lag = link.GsTick - link.NpcServerTick;

        _link.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"client {dot} {(rtt < 0 ? "--" : rtt.ToString(CultureInfo.InvariantCulture))}ms   " +
            $"npc-link {(link.Connected == 1 ? "●" : "○")}   " +
            $"gs {link.GsTick}  npc {link.NpcServerTick} ({-lag})   drop {link.Dropped}   gap {link.Gaps}");
    }

    /// <summary>
    /// 맵. T6-27 이 여기에 존·POI 를, T6-28 이 엔티티를 그린다.
    /// 지금은 무엇을 기다리는 중인지만 말한다 — 빈 화면은 "죽었다" 로 읽힌다.
    /// </summary>
    private void OnPaintMap(object? sender, PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Rectangle viewport = _map.ClientRectangle;

        // <b>첫 맞춤은 여기서 한다.</b> OnLoad 에서 하면 분할 위치가 아직 반영되기 전의 넓은
        // 폭으로 맞춰져서 지도가 화면 밖으로 삐져 나온다 — 실제로 그렇게 됐다.
        if (!_fitted && viewport.Width > 1 && viewport.Height > 1)
        {
            _renderer.Camera.FitWorld(_renderer.WorldBounds, viewport);
            _fitted = true;
        }

        g.Clear(Color.FromArgb(18, 18, 22));

        // 존·POI 는 마스터데이터에서 나온다. 접속 전에도 지도는 보인다 —
        // 게임서버가 없다고 빈 화면을 띄우면 사람이 클라이언트가 죽은 줄 안다.
        _renderer.DrawWorld(g, viewport, _connection.Zones);

        DrawOverlay(g, viewport);
    }

    /// <summary>화면 위에 얹는 안내. 무엇을 기다리는 중인지 말한다.</summary>
    private void DrawOverlay(Graphics g, Rectangle viewport)
    {
        string message = _connection.State switch
        {
            ConnectionState.Connected when _connection.PlayerId == 0 =>
                "정원이 찼다 — --max-clients 를 늘리거나 다른 창을 닫는다",
            ConnectionState.Connected when _connection.Latest is null => "첫 스냅샷을 기다리는 중…",
            ConnectionState.Connected when !MasterDataMatchesServer() =>
                "경고: 마스터데이터 해시가 게임서버와 다르다 — 화면이 거짓말을 한다",
            ConnectionState.Connected => string.Empty,
            _ => $"게임서버 {_options.Host}:{_options.Port} 에 붙는 중… ({_connection.Attempts}회 시도)",
        };

        if (message.Length == 0)
        {
            return;
        }

        using var brush = new SolidBrush(Color.Gainsboro);
        using var shade = new SolidBrush(Color.FromArgb(150, 12, 12, 16));
        var box = new Rectangle(viewport.X, viewport.Bottom - 40, viewport.Width, 28);

        g.FillRectangle(shade, box);
        g.DrawString(
            message,
            Font,
            brush,
            box,
            new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
    }

    // ---------------------------------------------------------------- 카메라 조작

    private void OnMapMouseWheel(object? sender, MouseEventArgs e) =>
        _renderer.Camera.ZoomAt(e.Location, e.Delta, _map.ClientRectangle);

    private void OnMapMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right || e.Button == MouseButtons.Middle)
        {
            _dragging = true;
            _dragFrom = e.Location;
        }

        _map.Focus();
    }

    private void OnMapMouseMove(object? sender, MouseEventArgs e)
    {
        if (_dragging)
        {
            _renderer.Camera.Pan(e.X - _dragFrom.X, e.Y - _dragFrom.Y);
            _dragFrom = e.Location;

            return;
        }

        // 마우스를 올리면 POI id 를 말한다 (docs/20 §9.3). 이름은 마스터데이터에서 온다.
        string label = _renderer.TryPickPoi(e.Location, _map.ClientRectangle, out PoiDef poi)
            ? $"{poi.Id}  ({poi.Type})"
            : string.Empty;

        if (_tipText != label)
        {
            _tipText = label;
            _tip.SetToolTip(_map, label);
        }
    }

    private void OnMapMouseUp(object? sender, MouseEventArgs e) => _dragging = false;

    /// <inheritdoc />
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Home:
                _renderer.Camera.FitWorld(_renderer.WorldBounds, _map.ClientRectangle);
                return true;

            case Keys.Space:
                // 플레이어 추적 복귀 (docs/20 §9.4). 따라갈 위치는 T6-28 이 채운다.
                _renderer.Camera.Follow = true;
                return true;

            default:
                return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    /// <summary>
    /// 이 창이 보는 마스터데이터가 서버의 것과 같은가.
    ///
    /// <b>다르면 그럴듯한 거짓 화면이 나온다</b> — POI 이름과 위치가 어긋난 채로 그려지고,
    /// 그것이 가장 찾기 어려운 종류의 사고다 (docs/20 §8.1).
    /// </summary>
    public bool MasterDataMatchesServer() =>
        _connection.State == ConnectionState.Connected
        && _connection.Hello.MasterData == WireHash.FromHex(_data.ContentHash);

    /// <summary>
    /// <c>Npc.Contracts.TimeOfDay</c> 를 화면에 쓰려고 다시 적은 이름표.
    ///
    /// <b>클라이언트는 <c>Npc.Contracts</c> 를 참조하지 않는다</b> (docs/20 §4) — 프로토콜은
    /// 이 값을 <c>byte</c> 로 싣고, 이름은 화면 문제라 여기 있는 것이 맞다.
    /// </summary>
    private enum TimeOfDayLabel : byte
    {
        Dawn = 0,
        Morning,
        Day,
        Evening,
        Night,
        LateNight,
    }

    /// <summary>
    /// 깜빡이지 않는 그리기 판. <c>DoubleBuffered</c> 를 켜는 것이 전부다 —
    /// 안 켜면 60fps 에서 화면이 찢어진다.
    /// </summary>
    private sealed class MapPanel : Panel
    {
        public MapPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        }
    }
}
