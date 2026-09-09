using System.Text.Json;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.MasterData;
using Npc.Planning;
using Npc.Runtime;

namespace Npc.Host.Persistence;

/// <summary>복원 정책 (A-01).</summary>
public enum RestoreMode
{
    /// <summary>디렉터리의 최신 스냅샷을 조건이 맞으면 복원한다 (기본).</summary>
    Auto,

    /// <summary>복원하지 않는다. 시드로 시작한다.</summary>
    None,

    /// <summary>지정한 파일에서 복원한다. 조건이 안 맞으면 기동 실패다.</summary>
    File,
}

/// <summary>복원 결과. <c>/healthz/startup</c>·<c>/status</c> 가 읽는다.</summary>
/// <param name="Restored">복원했는가.</param>
/// <param name="Tick">복원한 스냅샷의 틱. 복원 안 했으면 0.</param>
/// <param name="Path">읽은 파일. 복원 안 했으면 null.</param>
/// <param name="ReissuedSteps">재발행 대기로 되돌린 스텝 수.</param>
/// <param name="IndividualPlans">되살린 개별 플랜 수.</param>
/// <param name="Detail">사람이 읽는 사유. 복원하지 않았으면 왜인지가 여기 있다.</param>
public readonly record struct RestoreResult(
    bool Restored,
    long Tick,
    string? Path,
    int ReissuedSteps,
    int IndividualPlans,
    string Detail);

/// <summary>
/// 스냅샷 복원 (A-01).
///
/// <b>조건이 하나라도 안 맞으면 복원하지 않는다.</b> 형식 버전 · 마스터데이터 해시 · 로스터 해시 ·
/// NPC 수 · 인벤토리 칸 수. 맞지 않는 상태를 억지로 얹으면 첨자가 어긋난 채로 도는데,
/// 그 사고는 "대장장이가 밭을 간다" 로 나타나 원인이 아주 멀어진다.
///
/// CRC 가 깨진 파일은 <b>이전 스냅샷으로 물러난다</b> — 크래시 도중에 쓰이던 파일이 최신인 것이
/// 정상적인 상황이기 때문이다.
///
/// <b>프리픽스 해시가 다르면 개별 플랜만 버린다.</b> 프롬프트가 바뀌었다는 뜻이고, 그때 개별 플랜은
/// 낡은 카탈로그로 만들어진 것이다. NPC 는 버킷 플랜으로 내려온다.
/// </summary>
public sealed class SnapshotRestorer
{
    private readonly NpcStore _store;
    private readonly GameClock _clock;
    private readonly ZoneStateTable _zones;
    private readonly CorrelationTable _correlations;
    private readonly IndividualPlanPool _pool;
    private readonly MasterDataSet _data;

    /// <summary>복원기를 만든다.</summary>
    public SnapshotRestorer(
        NpcStore store,
        GameClock clock,
        ZoneStateTable zones,
        CorrelationTable correlations,
        IndividualPlanPool pool,
        MasterDataSet data)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(zones);
        ArgumentNullException.ThrowIfNull(correlations);
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(data);

        _store = store;
        _clock = clock;
        _zones = zones;
        _correlations = correlations;
        _pool = pool;
        _data = data;
    }

    /// <summary>
    /// 복원을 시도한다. <b>기동 중에만 부른다</b> — 틱 루프가 돌기 전이다.
    /// </summary>
    /// <param name="mode">정책.</param>
    /// <param name="dirOrPath">디렉터리(auto) 또는 파일(file).</param>
    /// <param name="masterDataHash">지금 마스터데이터 해시.</param>
    /// <param name="rosterHash">지금 로스터 해시.</param>
    /// <param name="prefixHash">지금 프롬프트 프리픽스 해시.</param>
    /// <param name="log">진단 로그.</param>
    public RestoreResult TryRestore(
        RestoreMode mode,
        string dirOrPath,
        string masterDataHash,
        string rosterHash,
        string prefixHash,
        TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(dirOrPath);
        ArgumentNullException.ThrowIfNull(log);

        if (mode == RestoreMode.None)
        {
            return new RestoreResult(false, 0, null, 0, 0, "--restore none");
        }

        IReadOnlyList<string> candidates = mode == RestoreMode.File
            ? [dirOrPath]
            : SnapshotFile.ListNewestFirst(dirOrPath);

        if (candidates.Count == 0)
        {
            return new RestoreResult(false, 0, null, 0, 0, $"스냅샷이 없다 ({dirOrPath}) — 시드로 기동");
        }

        string? lastError = null;

        foreach (string path in candidates)
        {
            if (!SnapshotFile.TryRead(
                    path, _zones.Capacity,
                    out SnapshotHeader header, out ShadowBuffer? buffer,
                    out IReadOnlyList<IndividualPlanRecord> individuals, out string? readError))
            {
                lastError = readError;
                log.WriteLine($"warn: 스냅샷을 건너뛴다 — {readError}");
                continue;
            }

            if (Mismatch(header, masterDataHash, rosterHash) is { } reason)
            {
                lastError = reason;
                log.WriteLine($"warn: 스냅샷을 건너뛴다 ({Path.GetFileName(path)}) — {reason}");
                continue;
            }

            _store.LoadFrom(buffer!);
            _zones.LoadFrom(buffer!.ZoneRegion, buffer.ZoneClimate);
            _clock.RestoreTo(new Tick(header.Tick), header.SyncedTick);
            _correlations.JumpTo(header.NextCorrelation);

            int reissued = _store.ReissueWaitingSteps();
            int restoredPlans = 0;

            // 프리픽스가 바뀌었으면 개별 플랜은 낡은 카탈로그의 산물이다. 버킷 플랜으로 내린다.
            bool prefixMatches = string.Equals(header.PrefixHash, prefixHash, StringComparison.OrdinalIgnoreCase);

            if (prefixMatches)
            {
                restoredPlans = RestoreIndividuals(individuals, header.Tick, log);
            }
            else if (individuals.Count > 0)
            {
                log.WriteLine(
                    $"warn: 프리픽스가 바뀌어 개별 플랜 {individuals.Count}건을 버린다 "
                    + $"({Short(header.PrefixHash)} → {Short(prefixHash)}).");

                DropIndividuals();
            }

            return new RestoreResult(
                true,
                header.Tick,
                path,
                reissued,
                restoredPlans,
                $"{Path.GetFileName(path)} · tick {header.Tick} · 재발행 {reissued} · 개별 플랜 {restoredPlans}");
        }

        return new RestoreResult(
            false, 0, null, 0, 0,
            lastError is null ? "복원할 스냅샷이 없다" : $"복원 실패 — {lastError}");
    }

    private static string Short(string hash) =>
        hash.Length <= 8 ? hash : hash[..8];

    private string? Mismatch(SnapshotHeader header, string masterDataHash, string rosterHash)
    {
        if (header.NpcCount != _store.Count)
        {
            return $"NPC 수가 다르다: 파일 {header.NpcCount} vs 지금 {_store.Count}";
        }

        if (header.InventoryStride != _store.InventoryStride)
        {
            return $"인벤토리 칸 수가 다르다: 파일 {header.InventoryStride} vs 지금 {_store.InventoryStride}";
        }

        if (!string.Equals(header.MasterDataHash, masterDataHash, StringComparison.OrdinalIgnoreCase))
        {
            return $"마스터데이터 해시가 다르다: {Short(header.MasterDataHash)} vs {Short(masterDataHash)}";
        }

        if (!string.Equals(header.RosterHash, rosterHash, StringComparison.OrdinalIgnoreCase))
        {
            return $"로스터 해시가 다르다: {Short(header.RosterHash)} vs {Short(rosterHash)}";
        }

        return null;
    }

    private void DropIndividuals()
    {
        for (int npc = 0; npc < _store.Count; npc++)
        {
            if (IndividualPlanPool.IsIndividual(_store.PlanId[npc]))
            {
                // 0 은 유효한 버킷 첨자가 아니라 "쉬는 플랜" 이다. PlanStore.CreateIdleOnly 가
                // 0번에 그것을 등록한다 — 다음 스텝 경계에서 버킷 플랜으로 갈아탄다.
                _store.PlanId[npc] = PlanStore.IdlePlanId;
                _store.StepIndex[npc] = 0;
                _store.StepStatus[npc] = (byte)StepStatus.Ready;
            }
        }
    }

    private int RestoreIndividuals(
        IReadOnlyList<IndividualPlanRecord> records, long tick, TextWriter log)
    {
        int restored = 0;

        foreach (IndividualPlanRecord record in records)
        {
            if ((uint)record.Npc >= (uint)_store.Count)
            {
                continue;
            }

            try
            {
                PlanDocument? document = JsonSerializer.Deserialize(
                    record.SourceJson, PlanJsonContext.Default.PlanDocument);

                if (document is null)
                {
                    throw new PlanCompilationException("SourceJson 을 PlanDocument 로 읽지 못했다.", 0);
                }

                CompiledPlan plan = PlanCompiler.Compile(
                    document,
                    BucketKey.FromIndex(record.Bucket),
                    new PlanId(PlanStore.IdlePlanId),
                    _data,
                    PlanOrigin.Runtime,
                    version: 1,
                    sourceJson: record.SourceJson);

                _store.PlanId[record.Npc] = _pool.Assign(plan, record.Npc, tick);
                restored++;
            }
            catch (Exception e) when (e is PlanCompilationException or JsonException)
            {
                // 재컴파일 실패는 치명이 아니다 — 그 NPC 만 버킷 플랜으로 내린다.
                log.WriteLine($"warn: NPC {record.Npc} 의 개별 플랜을 되살리지 못했다: {e.Message}");

                _store.PlanId[record.Npc] = PlanStore.IdlePlanId;
                _store.StepIndex[record.Npc] = 0;
                _store.StepStatus[record.Npc] = (byte)StepStatus.Ready;
            }
        }

        return restored;
    }
}
