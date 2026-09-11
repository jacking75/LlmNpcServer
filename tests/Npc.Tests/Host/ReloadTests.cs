using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.Host;
using Npc.Host.Metrics;
using Npc.Host.Reload;
using Npc.Llm;
using Npc.MasterData;
using Npc.Planning;

namespace Npc.Tests.Host;

/// <summary>
/// A-07 — 무중단 리로드.
///
/// <para>
/// <b>고친 플랜을 올리려고 프로세스를 내리는 것이 문제였다.</b> 재기동은 상태 전손이고,
/// 상태 전손은 검수 사이클을 하루에 한 번으로 만든다 — 그러면 아무도 고치지 않는다.
/// </para>
///
/// <para>
/// 여기서 지키는 것 — <b>리로드는 트랜잭션이다</b>(실패하면 아무것도 안 바뀐다) ·
/// <b>바뀐 것만 등록한다</b>(레지스트리가 65,536칸이고 회수가 없다) ·
/// <b>틱 루프에 할당을 만들지 않는다</b>.
/// </para>
/// </summary>
[Collection(Npc.Tests.Runtime.AllocationCollection.Name)]
public sealed class ReloadTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    /// <summary>
    /// <b>디스크에 새 플랜을 놓고 부르면 올라온다.</b> 프로세스를 내리지 않는다 —
    /// 그것이 이 태스크의 전부다.
    /// </summary>
    [Fact]
    public async Task Reload_PicksUpPlansWrittenAfterStartup()
    {
        string root = NewRoot("pickup");

        try
        {
            string dir = Path.Combine(root, CurrentSha());

            Seed(dir, buckets: 2);

            await using NpcHost host = Host(root);

            await host.RunAsync(CancellationToken.None);

            Assert.Equal(2, host.Plans.FilledBuckets);

            // 기동 뒤에 프리베이크가 두 건을 더 떨궜다.
            Seed(dir, buckets: 4);

            ReloadResult result = host.Reloader.Reload(ReloadScope.PlanStore);

            Assert.True(result.Ok, result.Detail);
            Assert.Equal(4, host.Plans.FilledBuckets);
            Assert.Equal(1, host.Reloader.Reloads);
            Assert.Equal(0, host.Reloader.Failures);
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// <b>같은 버킷의 내용이 바뀌면 새 <c>PlanId</c> 가 걸린다.</b> 런타임은 NPC 마다
    /// <c>PlanId</c> 를 들고 있으므로, id 가 안 바뀌면 고친 것이 반영되지 않는다.
    /// </summary>
    [Fact]
    public async Task Reload_SwapsTheBucketToANewPlanId()
    {
        string root = NewRoot("swap");

        try
        {
            string dir = Path.Combine(root, CurrentSha());

            BucketKey bucket = Seed(dir, buckets: 1)[0];

            await using NpcHost host = Host(root);

            await host.RunAsync(CancellationToken.None);

            PlanId before = host.Plans.Resolve(bucket).Id;

            Assert.Equal("reload-0", host.Plans.Resolve(bucket).Goal);

            // 같은 버킷을 다른 내용으로 덮는다.
            Write(dir, bucket, goal: "고쳤다");

            Assert.True(host.Reloader.Reload(ReloadScope.PlanStore).Ok);

            CompiledPlan after = host.Plans.Resolve(bucket);

            Assert.Equal("고쳤다", after.Goal);
            Assert.NotEqual(before, after.Id);

            // <b>옛 플랜은 지우지 않는다.</b> 그 id 를 들고 있는 NPC 가 스텝 중간일 수 있다.
            Assert.Equal("reload-0", host.Plans[before.Value].Goal);
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// <b>깨진 플랜이 하나라도 있으면 아무것도 안 바꾼다.</b> 반쯤 갈아 끼운 스토어는
    /// "어떤 NPC 는 새 플랜, 어떤 NPC 는 옛 플랜, 경계가 어디인지 모름" 을 만든다.
    /// </summary>
    [Fact]
    public async Task Reload_IsTransactional()
    {
        string root = NewRoot("tx");

        try
        {
            string dir = Path.Combine(root, CurrentSha());

            BucketKey[] buckets = Seed(dir, buckets: 2);

            await using NpcHost host = Host(root);

            await host.RunAsync(CancellationToken.None);

            PlanId before = host.Plans.Resolve(buckets[0]).Id;

            // 고친 것 하나 + 깨진 것 하나. 깨진 쪽 때문에 고친 쪽도 안 올라가야 한다.
            //
            // 깨는 것은 <b>내용</b>이다. 이름을 깨면 "낡은 파일" 로 세어 건너뛰므로
            // (마스터데이터가 바뀐 뒤에 남은 파일이 그렇다) 교체를 막지 못한다.
            Write(dir, buckets[0], goal: "올라가면 안 된다");
            File.WriteAllText(
                PlanStoreIo.PathOf(dir, PlanLayer.Plans, buckets[1], s_data),
                "{ 이건 JSON 이 아니다");

            ReloadResult result = host.Reloader.Reload(ReloadScope.PlanStore);

            Assert.False(result.Ok);
            Assert.Contains("교체하지 않는다", result.Detail, StringComparison.Ordinal);

            // 현 상태 그대로다.
            Assert.Equal(before, host.Plans.Resolve(buckets[0]).Id);
            Assert.Equal("reload-0", host.Plans.Resolve(buckets[0]).Goal);
            Assert.Equal(0, host.Reloader.Reloads);
            Assert.Equal(1, host.Reloader.Failures);
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// <b>안 바뀐 것은 등록하지 않는다.</b> 버킷은 2,880칸이고 레지스트리는 65,536칸인데
    /// 회수가 없다 — 매번 전량 등록하면 리로드 22회에 프로세스가 던진다.
    /// </summary>
    [Fact]
    public async Task Reload_DoesNotGrowTheRegistryWhenNothingChanged()
    {
        string root = NewRoot("noop");

        try
        {
            Seed(Path.Combine(root, CurrentSha()), buckets: 6);

            await using NpcHost host = Host(root);

            await host.RunAsync(CancellationToken.None);

            int registered = host.Plans.Count;

            for (int i = 0; i < 5; i++)
            {
                ReloadResult result = host.Reloader.Reload(ReloadScope.PlanStore);

                Assert.True(result.Ok, result.Detail);
                Assert.Equal(0, result.Buckets);
            }

            Assert.Equal(registered, host.Plans.Count);
            Assert.Equal(6, host.Plans.FilledBuckets);
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// <b>온 등급은 인터럽트 규칙까지 올린다.</b> 마스터데이터를 다시 읽되
    /// <b>구조 해시가 같을 때만</b> — 바뀌었으면 게임서버도 같이 배포해야 한다 (B-04).
    /// </summary>
    [Fact]
    public async Task Reload_RaisesInterruptRulesOnTheContentScope()
    {
        string root = NewRoot("content");

        try
        {
            Seed(Path.Combine(root, CurrentSha()), buckets: 1);

            await using NpcHost host = Host(root);

            await host.RunAsync(CancellationToken.None);

            ReloadResult result = host.Reloader.Reload(ReloadScope.Content);

            Assert.True(result.Ok, result.Detail);
            Assert.Equal(s_data.Interrupts.Rules.Length, result.Interrupts);
            Assert.True(result.Fallbacks > 0, "폴백이 안 올라왔다");
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// <b>모르는 등급은 조용히 핫으로 떨어지지 않는다.</b> 떨어뜨리면 운영자가
    /// "content 로 불렀는데 인터럽트가 안 올라왔다" 를 한참 들여다본다.
    /// </summary>
    [Fact]
    public void Reload_RejectsAnUnknownScope()
    {
        Assert.Equal(ReloadScope.PlanStore, ReloadService.ParseScope(null));
        Assert.Equal(ReloadScope.PlanStore, ReloadService.ParseScope("planstore"));
        Assert.Equal(ReloadScope.Content, ReloadService.ParseScope("content"));
        Assert.Null(ReloadService.ParseScope("everything"));

        // 등급 설명이 응답·문서에 같이 나간다.
        Assert.Equal(3, ReloadService.Describe().Length);
    }

    /// <summary>
    /// <b>리로드 중에도 틱 예산은 그대로다.</b> 로드·검증은 호출자 스레드에서 돌고
    /// 틱 루프가 하는 일은 참조 읽기뿐이다 (CLAUDE.md §2.1).
    /// </summary>
    [Fact]
    public async Task Reload_DoesNotAllocateInTheTickLoop()
    {
        string root = NewRoot("alloc");

        try
        {
            string dir = Path.Combine(root, CurrentSha());

            Seed(dir, buckets: 3);

            await using NpcHost host = Host(root, npcs: 200);

            // 루프가 돌기 시작하면 리로드를 건다. 틱 루프와 다른 스레드다.
            Task<int> reloads = Task.Run(async () =>
            {
                int done = 0;

                while (host.Loop.TicksProcessed == 0)
                {
                    await Task.Yield();
                }

                for (int i = 0; i < 3 && host.Loop.TicksProcessed > 0; i++)
                {
                    Seed(dir, buckets: 3 + i);

                    if (host.Reloader.Reload(ReloadScope.PlanStore).Ok)
                    {
                        done++;
                    }
                }

                return done;
            });

            await host.RunAsync(CancellationToken.None);

            Assert.True(await reloads > 0, "리로드가 한 번도 안 돌았다");

            MetricsSnapshot m = host.Metrics.Snapshot();

            Assert.Equal(0, m.Tick.BytesPerTick);
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// <b>워처는 개발용이지만 조용해질 때까지 기다린다.</b> 프리베이크가 2,880개를 쏟는 동안
    /// 매번 도는 워처는 리로드가 아니라 부하다.
    /// </summary>
    [Fact]
    public async Task Watcher_DebouncesAndWatchesBothTrees()
    {
        string root = NewRoot("watch");

        try
        {
            Seed(Path.Combine(root, CurrentSha()), buckets: 1);

            await using NpcHost host = Host(root);

            await host.RunAsync(CancellationToken.None);

            using var watcher = new ReloadWatcher(
                host.Reloader, root, TestPaths.MasterData, TextWriter.Null);

            // planstore/ 와 masterdata/ 둘 다 본다.
            Assert.Equal(2, watcher.WatchedPaths);

            // 디바운스 창 안에서는 아직 안 돈다 — 창이 1.5초다.
            Assert.Equal(0, host.Reloader.Reloads + host.Reloader.Failures);
        }
        finally
        {
            Delete(root);
        }
    }

    // ---------------------------------------------------------------- 도우미

    private static string CurrentSha() =>
        PromptManifest.ShortSha(PromptPrefix.Build(s_data, TestPaths.MasterData).Sha256);

    private static string NewRoot(string tag) =>
        Path.Combine(Path.GetTempPath(), $"npc-a07-{tag}-{Guid.NewGuid():N}");

    /// <summary>폴백 플랜을 <paramref name="buckets"/> 개 버킷에 심는다. 심은 키를 돌려준다.</summary>
    private static BucketKey[] Seed(string directory, int buckets)
    {
        Directory.CreateDirectory(directory);

        var keys = new List<BucketKey>(buckets);

        for (int index = 0; index < TestPaths.TotalKeys && keys.Count < buckets; index++)
        {
            BucketKey bucket = BucketKey.FromIndex(index);

            if (s_data.Fallbacks!.For(bucket.A) is null)
            {
                continue;
            }

            Write(directory, bucket, $"reload-{keys.Count}");
            keys.Add(bucket);
        }

        Assert.Equal(buckets, keys.Count);

        return [.. keys];
    }

    private static void Write(string directory, BucketKey bucket, string goal)
    {
        CompiledPlan fallback = s_data.Fallbacks!.For(bucket.A)!;

        // <b><c>SourceJson</c> 을 비운다.</b> 있으면 Serialize 가 그것을 그대로 담아
        // 여기서 바꾼 Goal 이 파일에 안 실린다 — 원본 JSON 이 컴파일 결과보다 우선이다.
        PlanStoreIo.SavePlan(
            directory,
            PlanLayer.Plans,
            bucket,
            fallback with
            {
                Bucket = bucket,
                Goal = goal,
                Origin = PlanOrigin.Prebaked,
                SourceJson = string.Empty,
            },
            s_data);
    }

    private static NpcHost Host(string root, int npcs = 8)
    {
        string[] args =
        [
            "--loopback",
            "--npcs", npcs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--time-scale", "600",
            "--days", "1",   // 0 은 무제한이다 — 회차가 안 끝난다
            "--max-speed",
            "--no-dashboard",
            "--no-llm",
            "--planstore", root,
        ];

        Assert.True(HostOptions.TryParse(args, out HostOptions options, out string? error), error);

        return NpcHost.Create(
            options with { MasterData = TestPaths.MasterData }, TextWriter.Null);
    }

    private static void Delete(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // 임시 폴더다. 못 지워도 회차 결과에 영향이 없다.
        }
    }
}
