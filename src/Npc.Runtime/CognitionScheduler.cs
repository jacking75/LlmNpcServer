using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.Planning;

namespace Npc.Runtime;

/// <summary>
/// 인지 LOD 스캐너. docs/11 §4 · docs/03 §5.
///
/// 밴드별 주기(1/10/100/이벤트)로 슬라이스를 훑으며 <b>이탈 판정을 비트 연산 한 번</b>으로 한다.
/// 스텝을 순회하지 않는다 — 플랜 단위로 사전계산해둔 RequiredFlags/ForbiddenFlags 만 본다.
///
/// 성능 목표 (docs/11 §4): 틱당 판정 대상 ≤ 150, 스캔 소요 ≤ 3ms, NPC 5,000 기준.
/// NPC 를 늘려도 틱당 스캔 수가 거의 변하지 않아야 한다 — 그게 이 구조의 존재 이유다.
///
/// <b>슬라이스만으로는 상한이 지켜지지 않는다.</b> LOD 0 은 주기가 1 이라 밴드 전원을 매 틱 본다.
/// 플레이어가 마을 광장에 몰리면 수백 마리가 한꺼번에 LOD 0 으로 올라오고 스캔이 그만큼 늘어난다.
/// 그래서 <see cref="MaxScansPerTick"/> 로 틱당 총량을 자르고, 밴드별 커서를 돌려
/// 잘린 뒤쪽을 다음 틱에 이어 본다. 군중이 생기면 판정 주기가 늘어날 뿐 굶는 NPC 는 없다.
/// </summary>
public sealed class CognitionScheduler
{
    /// <summary>틱당 판정 상한. docs/11 §4 · §9 의 수용 기준이 그대로 코드에 있다.</summary>
    public const int MaxScansPerTick = 150;

    private readonly NpcStore _store;
    private readonly LodBandSet _bands;
    private readonly PlanStore _plans;
    private readonly int[] _cursor = new int[LodBandSet.BandCount];

    /// <summary>스캐너를 만든다. 기동 시 1회.</summary>
    /// <param name="store">NPC 상태.</param>
    /// <param name="bands">LOD 밴드 멤버십.</param>
    /// <param name="plans">플랜 스토어.</param>
    /// <param name="weights">
    /// 재계획 점수 가중치. 기본은 <see cref="Weights.Default"/> 다 —
    /// T4-17 의 A/B 스크립트가 세트를 바꿔 끼우려고 옵션으로 뺐다 (docs/14 §2 튜닝 절차).
    /// </param>
    public CognitionScheduler(NpcStore store, LodBandSet bands, PlanStore plans, Weights? weights = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(bands);
        ArgumentNullException.ThrowIfNull(plans);

        _store = store;
        _bands = bands;
        _plans = plans;
        Weights = weights ?? Weights.Default;
    }

    /// <summary>이 스캐너가 쓰는 가중치. 대시보드·A/B 리포트가 무엇으로 돌았는지 적을 때 읽는다.</summary>
    public Weights Weights { get; }

    /// <summary>
    /// 큐 삽입 시점의 플래그 스냅샷 표. 없으면 낡음 판정을 하지 않는다 (docs/14 §10).
    /// 워커가 꺼낼 때 이 값과 지금 플래그를 비교해 낡은 요청을 폐기한다.
    /// </summary>
    public ReplanSnapshots? Snapshots { get; init; }

    /// <summary>마지막 틱에 판정한 NPC 수. 대시보드의 "인지 스캔 대상/틱".</summary>
    public int LastScanned { get; private set; }

    /// <summary>누적 판정 수.</summary>
    public long TotalScanned { get; private set; }

    /// <summary>이탈로 판정해 재계획 큐에 넣은 수.</summary>
    public long Deviations { get; private set; }

    /// <summary>
    /// 한 틱. 각 밴드의 이번 슬라이스만 훑는다. <b>할당 0.</b>
    /// </summary>
    public void Scan(Tick tick, ReplanQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);

        int scanned = 0;
        int budget = MaxScansPerTick;

        for (int band = 0; band < LodBandSet.BandCount && budget > 0; band++)
        {
            if (LodBandSet.Bands[band].Period == 0)
            {
                continue;   // 비활성 밴드는 이벤트 시에만 본다
            }

            (int start, int end) = _bands.SliceOf(band, tick.Value);
            ReadOnlySpan<int> members = _bands.MembersOf(band);

            int width = end - start;

            if (width <= 0)
            {
                continue;
            }

            int take = Math.Min(width, budget);
            int offset = _cursor[band] % width;   // 잘린 뒤쪽을 다음 틱에 이어 본다

            // 커서는 폭으로 접어 둔다. 그냥 더하면 오래 돌린 서버에서 int 가 넘친다.
            _cursor[band] = (offset + take) % width;
            budget -= take;

            for (int t = 0; t < take; t++)
            {
                int k = start + ((offset + t) % width);
                int npc = members[k];
                scanned++;

                if ((StepStatus)_store.StepStatus[npc] == StepStatus.Unspawned)
                {
                    continue;
                }

                // 개별 풀 슬롯이 회수됐으면 판정을 건너뛴다 — 같은 틱의 실행기가
                // 곧바로 아키타입 폴백으로 되돌린다 (PlanExecutor.PlanOf).
                if (!_plans.TryFor(npc, _store.PlanId[npc], tick.Value, out CompiledPlan plan))
                {
                    continue;
                }

                // 이탈 판정. 비트 연산 한 번 (docs/01 §1 · docs/03 §5).
                if (!plan.NeedsReplan(_store.Flags[npc]))
                {
                    continue;
                }

                Deviations++;

                float score = Score(npc, plan, tick);

                if (queue.TryEnqueue(npc, score))
                {
                    Snapshots?.Capture(npc, _store.Flags[npc], tick, score);
                }
            }
        }

        LastScanned = scanned;
        TotalScanned += scanned;
    }

    /// <summary>
    /// 재계획 우선순위 점수. 네 항의 가중합이다 — 근접 · 노후 · 이탈 · 긴급 (docs/14 §2).
    ///
    /// 계산은 <see cref="ReplanScorer"/>(Npc.Planning) 가 한다. 여기서는 SoA 배열에서
    /// 네 값을 뽑아 넘기기만 한다 — <c>Npc.Planning</c> 은 <c>NpcStore</c> 를 모른다 (CLAUDE.md §3).
    /// </summary>
    private float Score(int npc, CompiledPlan plan, Tick tick) => ReplanScorer.Score(
        new NpcReplanState(
            _store.Lod[npc],
            _store.Flags[npc],
            _store.PlanAssignedTick[npc],
            _store.PendingUrgency[npc]),
        plan,
        tick,
        Weights);
}
