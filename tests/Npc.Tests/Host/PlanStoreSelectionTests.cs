using System.Globalization;
using Npc.Core;
using Npc.Core.Plan;
using Npc.Host;
using Npc.Llm;
using Npc.MasterData;
using Npc.Planning;

namespace Npc.Tests.Host;

/// <summary>
/// C-03 완료 조건 — <b>프롬프트를 되돌리면 그 회차의 플랜이 그대로 로드된다.</b>
///
/// <para>
/// 여기서는 LLM 을 부르지 않고 두 회차의 디렉터리를 손으로 만든다. 보는 것은
/// <b>"어느 디렉터리를 고르는가"</b> 이고, 그것이 롤백의 전부다 — 프롬프트 파일을 되돌리면
/// SHA 가 이전 값이 되고 그 폴더가 선택된다.
/// </para>
///
/// <para>
/// <b><c>--planstore-sha</c> 로 그 선택을 흉내낸다.</b> 테스트가 프롬프트 파일을 고칠 수는
/// 없다(저장소의 마스터데이터다) — 고정 옵션이 같은 선택 경로를 탄다.
/// </para>
/// </summary>
public sealed class PlanStoreSelectionTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    /// <summary>되돌린 회차를 흉내내는 가짜 SHA. 지금 프리픽스와 절대 겹치지 않는다.</summary>
    private const string OldSha = "deadbeef";

    /// <summary>
    /// 지금 프리픽스의 회차가 있으면 그것을 고르고, <c>--planstore-sha</c> 로 고정하면
    /// <b>옛 회차의 플랜이 로드된다</b>.
    /// </summary>
    [Fact]
    public async Task Host_PicksThePrefixDirectoryAndHonoursTheOverride()
    {
        string root = Path.Combine(Path.GetTempPath(), $"npc-c03-{Guid.NewGuid():N}");

        try
        {
            string currentSha = PromptManifest.ShortSha(
                PromptPrefix.Build(s_data, TestPaths.MasterData).Sha256);

            // 지금 회차는 버킷 2개, 옛 회차는 4개. 몇 개가 올라오는지로 어느 폴더를 골랐는지 안다.
            Seed(Path.Combine(root, currentSha), buckets: 2);
            Seed(Path.Combine(root, OldSha), buckets: 4);

            await using (NpcHost now = Host(root, sha: null))
            {
                await now.RunAsync(CancellationToken.None);

                Assert.Equal(2, now.Plans.FilledBuckets);
            }

            // 되돌린 회차. 프롬프트를 되돌렸을 때와 같은 선택 경로다.
            await using (NpcHost rolledBack = Host(root, OldSha))
            {
                await rolledBack.RunAsync(CancellationToken.None);

                Assert.Equal(4, rolledBack.Plans.FilledBuckets);
            }
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// <b>핀은 공유다.</b> 회차 폴더가 아니라 루트의 <c>pinned/</c> 에서 온다 —
    /// 사람이 검수한 것이라 프리픽스가 바뀌었다고 무효가 되지 않는다.
    /// </summary>
    [Fact]
    public async Task Host_LoadsPinnedFromTheSharedRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), $"npc-c03-pin-{Guid.NewGuid():N}");

        try
        {
            string currentSha = PromptManifest.ShortSha(
                PromptPrefix.Build(s_data, TestPaths.MasterData).Sha256);

            Seed(Path.Combine(root, currentSha), buckets: 1);

            // 핀은 루트에 둔다. 회차 폴더에 두지 않는다.
            Seed(root, buckets: 3, layer: PlanLayer.Pinned, skip: 1);

            await using NpcHost host = Host(root, sha: null);

            await host.RunAsync(CancellationToken.None);

            // 회차 1건 + 핀 3건. 핀이 안 올라왔으면 1 이다.
            Assert.Equal(4, host.Plans.FilledBuckets);
            Assert.Equal(3, host.Plans.PinnedBuckets);
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// <b>프리픽스 전문이 남는다.</b> SHA 만으로는 되돌릴 좌표는 되어도 읽을 수는 없다.
    /// </summary>
    [Fact]
    public async Task Host_SavesThePrefixArtifact()
    {
        string root = Path.Combine(Path.GetTempPath(), $"npc-c03-art-{Guid.NewGuid():N}");

        try
        {
            string sha = PromptPrefix.Build(s_data, TestPaths.MasterData).Sha256;

            Seed(Path.Combine(root, PromptManifest.ShortSha(sha)), buckets: 1);

            await using NpcHost host = Host(root, sha: null);

            await host.RunAsync(CancellationToken.None);

            string path = PlanStoreLayout.PrefixArtifactPath(root, sha);

            Assert.True(File.Exists(path), $"{path} 이 없다");

            string text = File.ReadAllText(path);

            Assert.Contains($"prefix_sha256: {sha}", text, StringComparison.Ordinal);
            Assert.Contains("ACTION CATALOG", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Delete(root);
        }
    }

    // ---------------------------------------------------------------- 도우미

    /// <summary>폴백 플랜을 <paramref name="buckets"/> 개 버킷에 심는다.</summary>
    private static void Seed(
        string directory, int buckets, PlanLayer layer = PlanLayer.Plans, int skip = 0)
    {
        Directory.CreateDirectory(directory);

        int written = 0;

        for (int index = skip; index < TestPaths.TotalKeys && written < buckets; index++)
        {
            BucketKey bucket = BucketKey.FromIndex(index);

            if (s_data.Fallbacks!.For(bucket.A) is not { } fallback)
            {
                continue;
            }

            PlanStoreIo.SavePlan(
                directory,
                layer,
                bucket,
                fallback with { Bucket = bucket, Origin = PlanOrigin.Prebaked },
                s_data);

            written++;
        }

        Assert.Equal(buckets, written);
    }

    private static NpcHost Host(string root, string? sha)
    {
        string[] args =
        [
            "--loopback",
            "--npcs", "8",
            "--time-scale", "600",
            "--days", "1",   // 0 은 무제한이다 — 회차가 안 끝난다
            "--max-speed",
            "--no-dashboard",
            "--no-llm",
            "--planstore", root,
            .. sha is null ? Array.Empty<string>() : ["--planstore-sha", sha],
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
