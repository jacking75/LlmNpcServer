using System.Collections.Immutable;
using System.Globalization;
using Npc.Contracts;
using Npc.MasterData;
using Npc.Planning;
using Npc.Runtime;

namespace Npc.Host.Reload;

/// <summary>리로드 등급 (A-07).</summary>
public enum ReloadScope
{
    /// <summary>
    /// <b>핫</b> — <c>planstore/</c> 의 플랜과 핀만. 게임서버에 아무 영향이 없다.
    /// </summary>
    PlanStore = 0,

    /// <summary>
    /// <b>온</b> — 위에 더해 <c>interrupts.json</c>·<c>fallback_plans.json</c>.
    /// <b>구조 해시가 바뀌지 않는 범위만</b> 허용한다 (B-04).
    /// </summary>
    Content = 1,
}

/// <summary>
/// 리로드 한 번의 결과.
/// </summary>
/// <param name="Ok">교체했는가. false 면 <b>아무것도 안 바꿨다</b>.</param>
/// <param name="Scope">요청한 등급.</param>
/// <param name="Buckets">버킷 표에서 바뀐 칸 수.</param>
/// <param name="Interrupts">올라온 인터럽트 규칙 수. 핫 등급이면 0.</param>
/// <param name="Fallbacks">올라온 폴백 플랜 수.</param>
/// <param name="Detail">사람이 읽는 사유. 실패면 <b>왜 안 바꿨는지</b>가 여기 있다.</param>
public readonly record struct ReloadResult(
    bool Ok, ReloadScope Scope, int Buckets, int Interrupts, int Fallbacks, string Detail);

/// <summary>
/// 무중단 리로드 (A-07).
///
/// <para>
/// <b>리로드는 트랜잭션이다.</b> 디스크에서 전부 읽고 전부 검증한 뒤에야 교체한다 —
/// 중간에 실패하면 <b>현 상태 그대로</b>다. 반쯤 갈아 끼운 스토어는 "어떤 NPC 는 새 플랜,
/// 어떤 NPC 는 옛 플랜, 그 경계가 어디인지 아무도 모르는" 상태를 만든다.
/// </para>
///
/// <para>
/// <b>틱 루프는 참조만 바꾼다.</b> 로드·검증은 이 호출자의 스레드(HTTP·워처)에서 돌고,
/// 틱 루프가 하는 일은 <c>Volatile.Write</c> 뿐이다 — 틱 예산에 영향이 없다 (§2.1).
/// </para>
///
/// <para>
/// <b>아키타입은 여기 없다.</b> 로드맵은 <c>archetypes.json</c> 의 <c>traits</c>·<c>desc</c> 를
/// 온 등급에 뒀지만, 그 둘은 <b>프롬프트 프리픽스에 실린다</b> — 고치면 프리픽스 SHA 가 바뀌고
/// 플랜 스토어는 다른 회차의 것이 된다 (C-03). 플랜을 살린 채 프리픽스만 바꾸는 것은
/// "이 플랜이 어떤 프롬프트로 만들어졌나" 를 거짓으로 만드는 일이라 <b>콜드</b>로 둔다.
/// </para>
/// </summary>
public sealed class ReloadService
{
    private readonly PlanStore _plans;
    private readonly MasterDataSet _data;
    private readonly string _masterDataDirectory;
    private readonly string _planStoreRoot;
    private readonly string _prefixSha;
    private readonly string? _planStoreSha;

    /// <summary>만든다. 기동 시 1회.</summary>
    /// <param name="plans">살아 있는 플랜 스토어.</param>
    /// <param name="data">기동 시 읽은 마스터데이터. 구조 비교의 기준이다.</param>
    /// <param name="masterDataDirectory"><c>masterdata/</c> 경로.</param>
    /// <param name="planStoreRoot"><c>planstore/</c> 경로.</param>
    /// <param name="prefixSha">지금 프롬프트 프리픽스 SHA.</param>
    /// <param name="planStoreSha"><c>--planstore-sha</c> 고정값. 없으면 null.</param>
    public ReloadService(
        PlanStore plans,
        MasterDataSet data,
        string masterDataDirectory,
        string planStoreRoot,
        string prefixSha,
        string? planStoreSha = null)
    {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrEmpty(masterDataDirectory);
        ArgumentException.ThrowIfNullOrEmpty(planStoreRoot);

        _plans = plans;
        _data = data;
        _masterDataDirectory = masterDataDirectory;
        _planStoreRoot = planStoreRoot;
        _prefixSha = prefixSha ?? string.Empty;
        _planStoreSha = planStoreSha;
    }

    /// <summary>인터럽트 규칙을 받을 곳. null 이면 온 등급이 인터럽트를 건너뛴다.</summary>
    public InterruptMatcher? Interrupts { get; set; }

    /// <summary>성공한 리로드 수.</summary>
    public long Reloads { get; private set; }

    /// <summary>실패한 리로드 수. <b>0 이 아니면 디스크에 못 올릴 것이 있다.</b></summary>
    public long Failures { get; private set; }

    /// <summary>마지막 결과. <c>/status</c> 가 보여 준다.</summary>
    public ReloadResult Last { get; private set; } =
        new(false, ReloadScope.PlanStore, 0, 0, 0, "아직 없음");

    /// <summary>
    /// 리로드한다. <b>부르는 쪽 스레드에서 로드·검증이 돈다</b> — 틱 루프가 아니다.
    /// </summary>
    /// <param name="scope">등급.</param>
    public ReloadResult Reload(ReloadScope scope)
    {
        try
        {
            ReloadResult result = Run(scope);

            if (result.Ok)
            {
                Reloads++;
            }
            else
            {
                Failures++;
            }

            Last = result;

            return result;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException)
        {
            Failures++;
            Last = new ReloadResult(false, scope, 0, 0, 0, $"실패: {e.Message}");

            return Last;
        }
    }

    private ReloadResult Run(ReloadScope scope)
    {
        // ── 1. 디스크에서 전부 읽는다 ──────────────────────────────
        //
        // 여기서 던지면 아무것도 안 바뀐 채로 끝난다. 그것이 트랜잭션의 전부다.
        MasterDataSet fresh = scope == ReloadScope.Content
            ? MasterDataLoader.Load(_masterDataDirectory)
            : _data;

        if (scope == ReloadScope.Content
            && !string.Equals(fresh.StructuralHash, _data.StructuralHash, StringComparison.Ordinal))
        {
            // 구조가 바뀌었다. 게임서버도 같이 바꿔야 하므로 리로드로 덮을 수 없다 (B-04).
            return new ReloadResult(
                false, scope, 0, 0, 0,
                "구조 해시가 달라졌다 — 재기동이 필요하다. code·bit·POI 좌표·버킷 차원이 바뀌면 "
                + "게임서버도 같이 배포해야 한다.");
        }

        PlanStoreLayout layout = PlanStoreLayout.Resolve(_planStoreRoot, _prefixSha, _planStoreSha);

        if (layout.Shape == PlanStoreShape.Missing)
        {
            return new ReloadResult(false, scope, 0, 0, 0, $"플랜 스토어가 없다: {layout}");
        }

        PlanStore staged = PlanStore.CreateIdleOnly(fresh);

        // 폴백을 먼저 올린다 — 버킷 미스가 기댈 곳이다.
        int fallbacks = 0;

        if (fresh.Fallbacks is { } table)
        {
            foreach (FallbackPlanEntry entry in table.Plans)
            {
                staged.SetFallback(entry.Archetype, staged.Register(entry.Plan));
                fallbacks++;
            }
        }

        PlanStoreLoadReport report = PlanStoreIo.LoadAll(
            layout.Directory, layout.PinnedRoot, staged, fresh);

        // ── 2. 검증 ────────────────────────────────────────────────
        //
        // 못 올린 플랜이 있으면 그것을 감추고 교체하지 않는다 — 감추면 다음 사람이
        // "리로드했는데 왜 그대로지" 를 몇 시간 들여다본다.
        if (report.Failed > 0)
        {
            return new ReloadResult(
                false, scope, 0, 0, 0,
                $"플랜 {report.Failed}건이 깨졌다. 교체하지 않는다: "
                + string.Join(" / ", report.Errors.Take(3)));
        }

        // ── 3. 교체 ────────────────────────────────────────────────
        //
        // 여기부터는 실패할 수 없는 연산만 있다. 참조 쓰기뿐이다.
        int buckets = _plans.Adopt(staged);
        int interrupts = 0;

        if (scope == ReloadScope.Content && Interrupts is { } matcher)
        {
            matcher.Rules = fresh.Interrupts;
            interrupts = fresh.Interrupts.Rules.Length;
        }

        string detail = string.Create(
            CultureInfo.InvariantCulture,
            $"{scope}: 버킷 {buckets}칸 · 폴백 {fallbacks} · 인터럽트 {interrupts} · "
            + $"올린 플랜 {report.Total}(pinned {report.Pinned}) · 건너뛴 파일 {report.Skipped}");

        return new ReloadResult(true, scope, buckets, interrupts, fallbacks, detail);
    }

    /// <summary>
    /// <c>scope</c> 문자열을 등급으로. 모르는 값은 null 이다 — 조용히 핫으로 떨어뜨리지 않는다.
    /// </summary>
    /// <param name="text">질의 문자열.</param>
    public static ReloadScope? ParseScope(string? text) => text?.ToLowerInvariant() switch
    {
        null or "" or "planstore" or "plans" or "hot" => ReloadScope.PlanStore,
        "content" or "warm" => ReloadScope.Content,
        _ => null,
    };

    /// <summary>등급별로 무엇이 바뀌는가. 문서·응답에 같이 싣는다.</summary>
    public static ImmutableArray<string> Describe() =>
    [
        "planstore — 플랜·핀만. 게임서버 영향 없음",
        "content — 위 + interrupts.json · fallback_plans.json. 구조 해시가 같을 때만",
        "재기동 — code·bit 추가 · allowed_actions · pois/zones/items 구조 · context_buckets · "
        + "archetypes 의 desc·traits(프리픽스에 실린다)",
    ];
}
