using System.Globalization;
using Npc.TestBed.Protocol;
using Npc.TestClient.Format;
using Npc.TestClient.Net;

namespace Npc.TestClient.Panels;

/// <summary>로그 필터. docs/20 §9.6.</summary>
public enum LogFilter
{
    /// <summary>전부.</summary>
    All = 0,

    /// <summary>선택한 NPC 의 것만.</summary>
    Selected,

    /// <summary>실패만 — <c>NpcActionFailed</c>.</summary>
    Failures,

    /// <summary>인터럽트 계열만 — 플레이어·전투·존 상태.</summary>
    Interrupts,
}

/// <summary>
/// 명령·이벤트 로그. docs/20 §9.6.
///
/// <para>
/// <b>여기서 처음으로 id 가 사람 말이 된다.</b> 프로토콜에는 문자열이 0개이고(§3.5)
/// 게임서버도 이름을 모른다 — 클라이언트가 <c>Npc.MasterData</c> 를 읽어 직접 푼다.
/// <c>MoveTo poi=137</c> 이 아니라 <c>MoveTo smithy_001_05</c> 로 보이는 것이 이 패널의 값이다.
/// </para>
///
/// <para>
/// <b>시간 역순이다.</b> 새 줄이 위에 온다 — 데모에서 사람이 보는 것은 언제나 "지금" 이다.
/// </para>
/// </summary>
public sealed class LogPanel : Panel
{
    /// <summary>화면에 담는 줄 수. docs/20 §9.6.</summary>
    public const int MaxLines = 500;

    /// <summary>한 번에 훑는 원본 줄 수. 필터가 걸리면 이만큼에서 골라낸다.</summary>
    private const int ScanLines = GameConnection.LogCapacity;

    private const int LineHeight = 15;
    private const int PadX = 8;
    private const int HeaderHeight = 30;

    private readonly IdNames _names;
    private readonly Font _font = new("Consolas", 8.25f);
    private readonly SolidBrush _command = new(Color.FromArgb(150, 190, 235));
    private readonly SolidBrush _event = new(Color.FromArgb(195, 197, 205));
    private readonly SolidBrush _failure = new(Color.FromArgb(235, 120, 105));
    private readonly SolidBrush _interrupt = new(Color.FromArgb(235, 195, 95));
    private readonly SolidBrush _dim = new(Color.FromArgb(130, 132, 142));

    private readonly LogCommand[] _commands = new LogCommand[ScanLines];
    private readonly LogEvent[] _events = new LogEvent[ScanLines];
    private readonly List<Row> _rows = new(MaxLines);

    private GameConnection? _connection;
    private int _selected = -1;
    private long _seenCommands = -1;
    private long _seenEvents = -1;

    /// <summary>패널을 만든다.</summary>
    public LogPanel(IdNames names)
    {
        ArgumentNullException.ThrowIfNull(names);

        _names = names;

        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.FromArgb(32, 32, 38);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }

    /// <summary>지금 필터. 헤더의 버튼이 바꾼다.</summary>
    private LogFilter Filter { get; set; } = LogFilter.All;

    /// <summary>
    /// 원본을 다시 읽어 화면 줄을 만든다. <b>UI 스레드에서 부른다.</b>
    ///
    /// <b>새 줄이 없으면 아무것도 안 한다.</b> 링의 꼬리가 그대로면 결과도 그대로다 —
    /// 60fps 로 1,024줄을 매번 다시 거를 이유가 없다.
    /// </summary>
    public void Update(GameConnection connection, int selected)
    {
        ArgumentNullException.ThrowIfNull(connection);

        bool same = ReferenceEquals(_connection, connection)
            && _selected == selected
            && _seenCommands == connection.CommandsLogged
            && _seenEvents == connection.EventsLogged;

        if (same)
        {
            return;
        }

        _connection = connection;
        _selected = selected;
        _seenCommands = connection.CommandsLogged;
        _seenEvents = connection.EventsLogged;

        Rebuild(connection);
        Invalidate();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _font.Dispose();
            _command.Dispose();
            _event.Dispose();
            _failure.Dispose();
            _interrupt.Dispose();
            _dim.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    protected override void OnMouseDown(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.Y < HeaderHeight)
        {
            // 헤더를 넷으로 나눠 필터 버튼으로 쓴다. 위젯을 얹지 않는 이유는
            // 이 패널이 이미 직접 그리고 있기 때문이다 — 반만 위젯이면 색이 어긋난다.
            int slot = Math.Clamp(e.X * 4 / Math.Max(1, Width), 0, 3);

            Filter = (LogFilter)slot;

            if (_connection is { } connection)
            {
                Rebuild(connection);
            }

            Invalidate();
        }

        base.OnMouseDown(e);
    }

    /// <inheritdoc />
    protected override void OnPaint(PaintEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        base.OnPaint(e);

        Graphics g = e.Graphics;

        DrawHeader(g);

        int y = HeaderHeight;

        foreach (Row row in _rows)
        {
            if (y > Height)
            {
                break;
            }

            g.DrawString(row.Text, _font, Brush(row), PadX, y);

            y += LineHeight;
        }

        if (_rows.Count == 0)
        {
            g.DrawString(
                _connection is null ? "(연결 대기)" : "(해당하는 줄이 없다)", _font, _dim, PadX, y);
        }
    }

    // ---------------------------------------------------------------- 헤더

    private void DrawHeader(Graphics g)
    {
        string[] labels = ["전체", "선택 NPC", "실패", "인터럽트"];
        float slot = Width / 4f;

        for (int i = 0; i < labels.Length; i++)
        {
            bool active = (int)Filter == i;
            var box = new RectangleF(i * slot, 0, slot, HeaderHeight - 6);

            using var back = new SolidBrush(
                active ? Color.FromArgb(60, 62, 76) : Color.FromArgb(40, 40, 48));

            g.FillRectangle(back, box);
            g.DrawString(
                labels[i],
                _font,
                active ? _event : _dim,
                box,
                new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                });
        }
    }

    private Brush Brush(Row row) => row.Tone switch
    {
        Tone.Command => _command,
        Tone.Failure => _failure,
        Tone.Interrupt => _interrupt,
        _ => _event,
    };

    // ---------------------------------------------------------------- 줄 만들기

    /// <summary>
    /// 링에서 새 것부터 읽어 필터를 걸고 <see cref="MaxLines"/> 까지 채운다.
    ///
    /// <b>명령과 이벤트를 한 목록에 섞는다.</b> 상관 ID 로 이어진 한 쌍이 떨어져 있으면
    /// 사람이 "이 명령의 답이 무엇이었나" 를 눈으로 못 잇는다 (N5).
    /// </summary>
    private void Rebuild(GameConnection connection)
    {
        _rows.Clear();

        int commands = connection.ReadRecentCommands(_commands);
        int events = connection.ReadRecentEvents(_events);
        int c = 0;
        int v = 0;

        // 둘 다 새 것부터 담겨 있다. 틱이 큰 쪽을 먼저 꺼내면 합쳐도 시간 역순이 된다.
        while (_rows.Count < MaxLines && (c < commands || v < events))
        {
            bool takeCommand = v >= events
                || (c < commands && _commands[c].Tick >= _events[v].Tick);

            if (takeCommand)
            {
                Offer(_commands[c++]);
            }
            else
            {
                Offer(_events[v++]);
            }
        }
    }

    private void Offer(in LogCommand line)
    {
        if (Filter is LogFilter.Failures or LogFilter.Interrupts)
        {
            return;   // 둘 다 이벤트의 성질이다
        }

        if (Filter == LogFilter.Selected && line.Npc != _selected)
        {
            return;
        }

        string poi = line.TargetPoi == 0 ? string.Empty : $" {_names.Poi(line.TargetPoi)}";

        _rows.Add(new Row(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{line.Tick,7}  →  #{line.Npc,-4} {IdNames.CommandKind(line.Kind),-16}{poi}  [{line.Correlation}]"),
            Tone.Command));
    }

    private void Offer(in LogEvent line)
    {
        bool failure = IdNames.IsFailure(line.Kind);
        bool interrupt = IdNames.IsInterruptish(line.Kind);

        bool keep = Filter switch
        {
            LogFilter.Selected => line.Npc == _selected,
            LogFilter.Failures => failure,
            LogFilter.Interrupts => interrupt,
            _ => true,
        };

        if (!keep)
        {
            return;
        }

        string code = IdNames.EventCode(line.Kind, line.Code);
        string detail = code.Length == 0
            ? (line.Amount != 0 ? $" {line.Amount}" : string.Empty)
            : $"({code})";

        _rows.Add(new Row(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{line.Tick,7}  ←  #{line.Npc,-4} {IdNames.EventKind(line.Kind)}{detail}"),
            failure ? Tone.Failure : interrupt ? Tone.Interrupt : Tone.Event));
    }

    /// <summary>줄의 색조.</summary>
    private enum Tone
    {
        Command,
        Event,
        Failure,
        Interrupt,
    }

    /// <summary>화면 한 줄.</summary>
    private readonly record struct Row(string Text, Tone Tone);
}
