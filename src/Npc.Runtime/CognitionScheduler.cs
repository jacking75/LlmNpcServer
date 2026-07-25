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
/// </summary>
public sealed class CognitionScheduler
{
    private readonly NpcStore _store;
    private readonly LodBandSet _bands;
    private readonly PlanStore _plans;

    /// <summary>스캐너를 만든다. 기동 시 1회.</summary>
    public CognitionScheduler(NpcStore store, LodBandSet bands, PlanStore plans)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(bands);
        ArgumentNullException.ThrowIfNull(plans);

        _store = store;
        _bands = bands;
        _plans = plans;
    }

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

        for (int band = 0; band < LodBandSet.BandCount; band++)
        {
            if (LodBandSet.Bands[band].Period == 0)
            {
                continue;   // 비활성 밴드는 이벤트 시에만 본다
            }

            (int start, int end) = _bands.SliceOf(band, tick.Value);
            ReadOnlySpan<int> members = _bands.MembersOf(band);

            for (int k = start; k < end; k++)
            {
                int npc = members[k];
                scanned++;

                if ((StepStatus)_store.StepStatus[npc] == StepStatus.Unspawned)
                {
                    continue;
                }

                CompiledPlan plan = _plans[_store.PlanId[npc]];

                // 이탈 판정. 비트 연산 한 번 (docs/01 §1 · docs/03 §5).
                if (!plan.NeedsReplan(_store.Flags[npc]))
                {
                    continue;
                }

                Deviations++;
                queue.TryEnqueue(npc, Score(npc, band));
            }
        }

        LastScanned = scanned;
        TotalScanned += scanned;
    }

    /// <summary>
    /// 재계획 우선순위 점수. P1 은 LOD 만 본다 (가까울수록 급하다).
    /// 정식 가중치(플레이어 근접 · 플랜 노후 · 전제 이탈 · 긴급도)는 T4-01 이 넣는다.
    /// </summary>
    private static int Score(int npc, int band)
    {
        _ = npc;
        return (LodBandSet.BandCount - band) * 10;
    }
}
