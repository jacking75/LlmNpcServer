using Npc.TestBed.Protocol;
using Npc.TestClient.Net;
using Npc.TestClient.Render;

namespace Npc.TestClient.Input;

/// <summary>
/// 키·마우스 → 서버. docs/20 §9.4.
///
/// <para>
/// <b>클라이언트는 위치를 보내지 않는다.</b> 방향만 보낸다 — 위치를 보내면 그것이 곧
/// 텔레포트 치트이고, 무엇보다 서버와 클라이언트가 서로 다른 위치를 진실이라고 믿기 시작한다
/// (docs/20 §8.2).
/// </para>
///
/// <para>
/// <b>사거리 판정을 여기서 하지 않는다.</b> <c>E</c>·<c>R</c> 은 그냥 보내고 30m 밖이면
/// 서버가 조용히 무시한다 (docs/20 §7.3). 클라이언트가 미리 걸러 주면 "왜 안 되는지" 를
/// 클라이언트가 알게 되고, 그 앎이 곧 화면 밖 NPC 의 존재를 알아내는 통로가 된다.
/// </para>
/// </summary>
public sealed class InputController
{
    /// <summary><c>R</c> 한 번의 피해량. 서버가 1~100 으로 자른다 (docs/20 §8.2).</summary>
    public const int AttackAmount = 10;

    private readonly GameConnection _connection;
    private readonly MapRenderer _renderer;

    private bool _north;
    private bool _south;
    private bool _west;
    private bool _east;
    private bool _run;

    private int _seq;
    private float _sentX;
    private float _sentZ;
    private bool _sentRun;
    private bool _everSent;

    /// <summary>조종기를 만든다.</summary>
    public InputController(GameConnection connection, MapRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(renderer);

        _connection = connection;
        _renderer = renderer;
    }

    /// <summary>선택된 NPC 첨자. 없으면 -1. 인스펙터·로그 필터의 대상이다 (docs/20 §9.5·§9.6).</summary>
    public int Selected { get; private set; } = -1;

    /// <summary>지금 눌려 있는 이동 방향의 X 성분(-1·0·1).</summary>
    public float DirX => (_east ? 1f : 0f) - (_west ? 1f : 0f);

    /// <summary>
    /// Z 성분. <b>화면 위가 −Z 다</b> — 카메라가 z 를 뒤집지 않으므로(<see cref="Camera"/>)
    /// <c>W</c> 는 z 를 줄인다. 뒤집으면 앞으로 가려다 뒤로 간다.
    /// </summary>
    public float DirZ => (_south ? 1f : 0f) - (_north ? 1f : 0f);

    /// <summary>달리는 중인가.</summary>
    public bool Running => _run;

    /// <summary>보낸 <c>Input</c> 프레임 수.</summary>
    public long InputsSent { get; private set; }

    /// <summary>
    /// 키가 눌렸다. 처리했으면 true — 폼이 그 키를 더 넘기지 않는다.
    /// </summary>
    /// <param name="key">눌린 키.</param>
    /// <param name="viewport">맵 영역. 카메라 조작이 쓴다.</param>
    /// <param name="entities">지금 화면의 엔티티. <c>Tab</c> 이 여기서 다음 NPC 를 고른다.</param>
    public bool KeyDown(Keys key, Rectangle viewport, ReadOnlySpan<EntityState> entities)
    {
        switch (key & Keys.KeyCode)
        {
            case Keys.W or Keys.Up:
                _north = true;
                break;

            case Keys.S or Keys.Down:
                _south = true;
                break;

            case Keys.A or Keys.Left:
                _west = true;
                break;

            case Keys.D or Keys.Right:
                _east = true;
                break;

            case Keys.ShiftKey:
                _run = true;
                break;

            case Keys.E:
                if (Selected >= 0)
                {
                    _connection.SendInteract(Selected);
                }

                break;

            case Keys.R:
                if (Selected >= 0)
                {
                    _connection.SendAttack(Selected, AttackAmount);
                }

                break;

            case Keys.Tab:
                Select(NextNpc(entities));
                break;

            case Keys.Home:
                _renderer.Camera.FitWorld(_renderer.WorldBounds, viewport);
                break;

            case Keys.Space:
                _renderer.Camera.Follow = true;
                break;

            default:
                return false;
        }

        return true;
    }

    /// <summary>키가 떨어졌다.</summary>
    public void KeyUp(Keys key)
    {
        switch (key & Keys.KeyCode)
        {
            case Keys.W or Keys.Up:
                _north = false;
                break;

            case Keys.S or Keys.Down:
                _south = false;
                break;

            case Keys.A or Keys.Left:
                _west = false;
                break;

            case Keys.D or Keys.Right:
                _east = false;
                break;

            case Keys.ShiftKey:
                _run = false;
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// 눌린 키를 전부 놓는다. <b>창이 포커스를 잃으면 부른다.</b>
    ///
    /// 안 하면 <c>Alt-Tab</c> 으로 나간 사이에 키를 놓아도 <c>KeyUp</c> 이 안 와서
    /// 플레이어가 영원히 걸어간다.
    /// </summary>
    public void ReleaseAll()
    {
        _north = false;
        _south = false;
        _west = false;
        _east = false;
        _run = false;
    }

    /// <summary>
    /// 10Hz 전송. docs/20 §9.4.
    ///
    /// <para>
    /// <b>멈출 때 0 을 한 번 보내야 한다.</b> 서버의 <c>PlayerRegistry</c> 는 마지막 입력을
    /// 그대로 들고 있으므로(<c>SetInput</c>), 키를 놓고 전송을 끊으면 플레이어가 계속 걸어간다.
    /// 그래서 <b>움직이는 동안은 매 틱, 값이 바뀐 순간은 무조건</b> 보낸다.
    /// </para>
    /// </summary>
    /// <returns>실제로 보냈으면 true.</returns>
    public bool Send()
    {
        float x = DirX;
        float z = DirZ;
        bool moving = x != 0f || z != 0f;
        bool changed = !_everSent || x != _sentX || z != _sentZ || _run != _sentRun;

        if (!moving && !changed)
        {
            return false;   // 서 있고 값도 그대로다. 보낼 것이 없다
        }

        _connection.SendInput(++_seq, x, z, _run);

        _sentX = x;
        _sentZ = z;
        _sentRun = _run;
        _everSent = true;
        InputsSent++;

        return true;
    }

    /// <summary>NPC 를 선택한다. 음수면 해제. 서버에도 알린다 — 로그 필터가 그 값을 본다.</summary>
    public void Select(int npc)
    {
        Selected = npc < 0 ? -1 : npc;

        _connection.SendSelect(Selected);
    }

    /// <summary>
    /// 좌클릭 선택. 15px 안에 NPC 가 없으면 해제다 (docs/20 §9.4).
    /// </summary>
    public void Click(Point screen, Rectangle viewport, ReadOnlySpan<EntityState> entities) =>
        Select(_renderer.TryPickNpc(screen, viewport, entities, out int npc) ? npc : -1);

    /// <summary>
    /// <c>Tab</c> — 다음 NPC. 첨자 오름차순으로 돌고, 끝나면 처음으로 감는다.
    ///
    /// <b>화면에 있는 것 중에서 고른다.</b> 전체 로스터에서 고르면 화면 밖으로 선택이 나가
    /// 인스펙터만 바뀌고 지도에서는 아무 일도 안 일어난다.
    /// </summary>
    private int NextNpc(ReadOnlySpan<EntityState> entities)
    {
        int first = -1;
        int next = -1;

        foreach (EntityState entity in entities)
        {
            if (entity.Kind != (byte)EntityKind.Npc)
            {
                continue;
            }

            if (first < 0 || entity.Id < first)
            {
                first = entity.Id;
            }

            if (entity.Id > Selected && (next < 0 || entity.Id < next))
            {
                next = entity.Id;
            }
        }

        return next >= 0 ? next : first;
    }
}
