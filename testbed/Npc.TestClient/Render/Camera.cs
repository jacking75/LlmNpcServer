namespace Npc.TestClient.Render;

/// <summary>
/// 월드 ↔ 화면 변환. docs/20 §9.3.
///
/// <para>
/// <b>좌표는 <c>pois.json</c> 의 것을 그대로 쓴다</b> (docs/20 §3.2). 존별 로컬이 아니라
/// 전역 좌표라 레이아웃 파일이 필요 없다 — 만들면 화면과 세계가 두 벌이 되고, 그 순간
/// 어느 쪽이 진짜인지 알 수 없게 된다.
/// </para>
///
/// <para>
/// <b>화면 y 는 월드 z 다.</b> 위에서 내려다본 2D 이고 <c>pois.json</c> 의 y 는 전부 0 이다.
/// z 를 뒤집지 않는다 — 뒤집으면 지도가 남북으로 거울이 되어, 데모에서 "성문이 반대쪽에 있다" 가 된다.
/// </para>
/// </summary>
public sealed class Camera
{
    /// <summary>줌 하한. 월드 3.8km 를 다 보여 주려면 이 정도까지 내려가야 한다.</summary>
    public const float MinZoom = 0.05f;

    /// <summary>줌 상한. 이보다 키우면 POI 사각이 화면을 덮는다.</summary>
    public const float MaxZoom = 4f;

    /// <summary>휠 한 칸의 배율.</summary>
    public const float WheelStep = 1.2f;

    /// <summary>화면 중앙이 보는 월드 좌표.</summary>
    public PointF Center { get; set; }

    /// <summary>화면 픽셀 / 월드 미터.</summary>
    public float Zoom { get; private set; } = 0.2f;

    /// <summary>
    /// 플레이어를 따라다니는가. <c>Space</c> 로 켜고, 드래그 팬을 하면 꺼진다 (docs/20 §9.3).
    ///
    /// <b>드래그가 자동으로 끄는 것이 요점이다.</b> 안 끄면 사람이 지도를 옮겨도 다음 프레임에
    /// 되돌아와서 "팬이 안 된다" 로 읽힌다.
    /// </summary>
    public bool Follow { get; set; } = true;

    /// <summary>월드 → 화면.</summary>
    public PointF ToScreen(float worldX, float worldZ, Rectangle viewport) => new(
        viewport.X + (viewport.Width / 2f) + ((worldX - Center.X) * Zoom),
        viewport.Y + (viewport.Height / 2f) + ((worldZ - Center.Y) * Zoom));

    /// <summary>화면 → 월드.</summary>
    public PointF ToWorld(float screenX, float screenY, Rectangle viewport) => new(
        Center.X + ((screenX - viewport.X - (viewport.Width / 2f)) / Zoom),
        Center.Y + ((screenY - viewport.Y - (viewport.Height / 2f)) / Zoom));

    /// <summary>월드 길이(m) → 화면 픽셀.</summary>
    public float Scale(float meters) => meters * Zoom;

    /// <summary>
    /// 커서 아래 지점을 고정한 채 확대·축소한다.
    ///
    /// <b>중앙 기준으로 줌하면 안 된다</b> — 사람이 보려던 곳이 화면 밖으로 밀려난다.
    /// </summary>
    public void ZoomAt(Point cursor, int wheelDelta, Rectangle viewport)
    {
        if (wheelDelta == 0)
        {
            return;
        }

        PointF before = ToWorld(cursor.X, cursor.Y, viewport);
        float factor = wheelDelta > 0 ? WheelStep : 1f / WheelStep;

        Zoom = Math.Clamp(Zoom * factor, MinZoom, MaxZoom);

        PointF after = ToWorld(cursor.X, cursor.Y, viewport);

        Center = new PointF(Center.X + (before.X - after.X), Center.Y + (before.Y - after.Y));
        Follow = false;
    }

    /// <summary>드래그 팬. 화면 픽셀 이동량을 월드로 되돌린다.</summary>
    public void Pan(int screenDx, int screenDy)
    {
        if (screenDx == 0 && screenDy == 0)
        {
            return;
        }

        Center = new PointF(Center.X - (screenDx / Zoom), Center.Y - (screenDy / Zoom));
        Follow = false;
    }

    /// <summary>이 지점을 화면 중앙에 둔다. 추적 모드가 매 프레임 부른다.</summary>
    public void LookAt(float worldX, float worldZ) => Center = new PointF(worldX, worldZ);

    /// <summary>
    /// 월드 전체를 화면에 맞춘다. <c>Home</c> 키다 (docs/20 §9.3).
    ///
    /// 여백 5% 를 둔다 — 딱 맞추면 가장자리 POI 가 화면 경계에 걸려 반쯤 잘린다.
    /// </summary>
    public void FitWorld(RectangleF world, Rectangle viewport)
    {
        if (world.Width <= 0 || world.Height <= 0 || viewport.Width <= 0 || viewport.Height <= 0)
        {
            return;
        }

        float fit = Math.Min(viewport.Width / world.Width, viewport.Height / world.Height);

        Zoom = Math.Clamp(fit * 0.95f, MinZoom, MaxZoom);
        Center = new PointF(world.X + (world.Width / 2f), world.Y + (world.Height / 2f));
        Follow = false;
    }
}
