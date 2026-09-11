using Npc.Llm;
using Npc.Planning;

namespace Npc.Tests.Planning;

/// <summary>
/// C-03 — 프리픽스 SHA 별 플랜 스토어 배치.
///
/// <para>
/// <b>롤백이 "다시 $5 를 태우는 일" 이면 아무도 안 되돌린다.</b> 예전에는 디렉터리가 하나라
/// 새 회차가 옛 회차를 덮었고, 프롬프트를 되돌려도 플랜은 돌아오지 않았다.
/// </para>
///
/// <para>
/// 여기서 지키는 것 셋 — <b>SHA 별 선택</b> · <b>부재 시 폴백</b> · <b>핀 공유</b>.
/// </para>
/// </summary>
public sealed class PlanStorePathTests
{
    private const string ShaA = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string ShaB = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

    /// <summary>SHA 별 디렉터리가 있으면 그것을 고른다.</summary>
    [Fact]
    public void Resolve_PicksTheVersionedDirectory()
    {
        using var temp = new TempStore();

        temp.MakeVersioned(ShaA);

        PlanStoreLayout layout = PlanStoreLayout.Resolve(temp.Root, ShaA);

        Assert.Equal(PlanStoreShape.Versioned, layout.Shape);
        Assert.Equal("01234567", layout.ShortSha);
        Assert.Equal(Path.Combine(temp.Root, "01234567"), layout.Directory);

        // 핀은 항상 루트다 — 회차마다 복사하면 어느 쪽이 진짜인지 모르게 된다.
        Assert.Equal(temp.Root, layout.PinnedRoot);
    }

    /// <summary>
    /// <b>다른 프리픽스로 물으면 그 회차는 없다.</b> 이것이 롤백이 도는 근거다 —
    /// 프롬프트를 되돌리면 SHA 가 이전 값이 되고 그 디렉터리가 그대로 선택된다.
    /// </summary>
    [Fact]
    public void Resolve_DoesNotShareDirectoriesBetweenPrefixes()
    {
        using var temp = new TempStore();

        temp.MakeVersioned(ShaA);

        PlanStoreLayout a = PlanStoreLayout.Resolve(temp.Root, ShaA);
        PlanStoreLayout b = PlanStoreLayout.Resolve(temp.Root, ShaB);

        Assert.Equal(PlanStoreShape.Versioned, a.Shape);
        Assert.Equal(PlanStoreShape.Missing, b.Shape);
        Assert.NotEqual(a.Directory, b.Directory);

        // 없어도 쓸 자리는 알려 준다 — 프리베이크가 거기에 만든다.
        Assert.Equal(Path.Combine(temp.Root, "fedcba98"), b.Directory);
    }

    /// <summary>
    /// <b>옛 평면 배치를 버리지 않는다.</b> 있는 산출물을 못 쓰게 만드는 이주는 파괴다.
    /// </summary>
    [Fact]
    public void Resolve_FallsBackToTheFlatLayout()
    {
        using var temp = new TempStore();

        Directory.CreateDirectory(Path.Combine(temp.Root, "plans"));

        PlanStoreLayout layout = PlanStoreLayout.Resolve(temp.Root, ShaA);

        Assert.Equal(PlanStoreShape.Flat, layout.Shape);
        Assert.Equal(temp.Root, layout.Directory);
        Assert.Empty(layout.ShortSha);
    }

    /// <summary>SHA 별이 있으면 평면보다 그것이 이긴다 — 새 배치가 정답이다.</summary>
    [Fact]
    public void Resolve_PrefersVersionedOverFlat()
    {
        using var temp = new TempStore();

        Directory.CreateDirectory(Path.Combine(temp.Root, "plans"));
        temp.MakeVersioned(ShaA);

        Assert.Equal(PlanStoreShape.Versioned, PlanStoreLayout.Resolve(temp.Root, ShaA).Shape);

        // 다른 프리픽스로 물으면 평면으로 떨어진다 — 그것이 "있는 것을 쓴다" 다.
        Assert.Equal(PlanStoreShape.Flat, PlanStoreLayout.Resolve(temp.Root, ShaB).Shape);
    }

    /// <summary>
    /// <c>--planstore-sha</c> 는 프롬프트를 건드리지 않고 다른 회차를 가리킨다 (진단용).
    /// </summary>
    [Fact]
    public void Resolve_HonoursThePinnedSha()
    {
        using var temp = new TempStore();

        temp.MakeVersioned(ShaB);

        PlanStoreLayout layout = PlanStoreLayout.Resolve(temp.Root, ShaA, pinnedSha: "fedcba98");

        Assert.Equal(PlanStoreShape.Versioned, layout.Shape);
        Assert.Equal("fedcba98", layout.ShortSha);
    }

    // ---------------------------------------------------------------- 프리픽스 아티팩트

    /// <summary>
    /// 프리픽스 전문을 남긴다. <b>머리말에 버전과 SHA 가 있어야</b> 파일 하나만 열어도
    /// "이 플랜이 어떤 프롬프트로 만들어졌나" 에 답할 수 있다.
    /// </summary>
    [Fact]
    public void SavePrefix_WritesTheTextWithAHeader()
    {
        using var temp = new TempStore();

        Assert.True(PlanStoreLayout.SavePrefix(temp.Root, ShaA, "2026.09-r1", "# SYSTEM RULES\n본문"));

        string path = PlanStoreLayout.PrefixArtifactPath(temp.Root, ShaA);
        string text = File.ReadAllText(path);

        Assert.EndsWith("01234567.md", path, StringComparison.Ordinal);
        Assert.Contains("prompt_version: 2026.09-r1", text, StringComparison.Ordinal);
        Assert.Contains($"prefix_sha256: {ShaA}", text, StringComparison.Ordinal);
        Assert.Contains("# SYSTEM RULES", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>두 번째 회차는 덮지 않는다.</b> 같은 SHA 면 같은 내용이라 덮을 이유가 없고,
    /// 덮으면 파일 시각이 흔들려 "언제 만든 회차인가" 가 사라진다.
    /// </summary>
    [Fact]
    public void SavePrefix_DoesNotOverwrite()
    {
        using var temp = new TempStore();

        Assert.True(PlanStoreLayout.SavePrefix(temp.Root, ShaA, "r1", "처음"));
        Assert.False(PlanStoreLayout.SavePrefix(temp.Root, ShaA, "r2", "나중"));

        string text = File.ReadAllText(PlanStoreLayout.PrefixArtifactPath(temp.Root, ShaA));

        Assert.Contains("처음", text, StringComparison.Ordinal);
        Assert.DoesNotContain("나중", text, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- 프롬프트 라벨

    /// <summary>저장소의 <c>prompt_manifest.json</c> 을 읽는다.</summary>
    [Fact]
    public void PromptManifest_LoadsTheRepositoryLabel()
    {
        PromptManifest manifest = PromptManifest.Load(
            Path.Combine(TestPaths.MasterData, PromptPrefix.PromptFolderName));

        Assert.False(manifest.IsMissing, "masterdata/prompt/prompt_manifest.json 이 없다");
        Assert.NotEmpty(manifest.PromptVersion);
        Assert.NotEmpty(manifest.Changelog);

        // 최신 항목의 버전이 지금 버전과 같아야 한다 — 어긋나면 이력이 거짓이다.
        Assert.Equal(manifest.PromptVersion, manifest.Changelog[0].Version);
    }

    /// <summary>
    /// <b>파일이 없어도 기동을 막지 않는다.</b> 라벨이 없다고 서버가 못 뜰 이유는 없다 —
    /// 대신 "unversioned" 가 그대로 실려 보인다.
    /// </summary>
    [Fact]
    public void PromptManifest_MissingFileIsVisibleNotFatal()
    {
        using var temp = new TempStore();

        PromptManifest manifest = PromptManifest.Load(temp.Root);

        Assert.True(manifest.IsMissing);
        Assert.Equal(PromptManifest.UnknownVersion, manifest.PromptVersion);
        Assert.Empty(manifest.Changelog);
    }

    /// <summary>
    /// <b>라벨은 프리픽스 SHA 를 바꾸지 않는다.</b> 바꾸면 "설명을 고쳤더니 캐시가 전부
    /// 미적중" 이 되고, 그러면 아무도 설명을 안 고친다.
    /// </summary>
    [Fact]
    public void PromptManifest_IsNotPartOfThePrefix()
    {
        string prompt = Path.Combine(TestPaths.MasterData, PromptPrefix.PromptFolderName);
        string text = File.ReadAllText(Path.Combine(prompt, PromptManifest.FileName));

        // 프리픽스 전문에 라벨 파일의 내용이 들어가지 않는다.
        PromptPrefix prefix = PromptPrefix.Build(
            Npc.MasterData.MasterDataLoader.Load(TestPaths.MasterData), TestPaths.MasterData);

        Assert.Contains("prompt_version", text, StringComparison.Ordinal);
        Assert.DoesNotContain("prompt_version", prefix.Text, StringComparison.Ordinal);
    }

    /// <summary>짧은 SHA 는 8자다. 폴더 이름으로 읽을 수 있어야 한다.</summary>
    [Fact]
    public void ShortSha_IsEightCharacters()
    {
        Assert.Equal("01234567", PromptManifest.ShortSha(ShaA));
        Assert.Equal("abc", PromptManifest.ShortSha("abc"));
    }

    // ---------------------------------------------------------------- 도우미

    /// <summary>임시 스토어. 회차마다 지운다.</summary>
    private sealed class TempStore : IDisposable
    {
        public TempStore()
        {
            Root = Path.Combine(
                Path.GetTempPath(), "npc-planstore-" + Guid.NewGuid().ToString("N")[..12]);

            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        /// <summary>그 SHA 의 회차 디렉터리를 만든다.</summary>
        public void MakeVersioned(string sha) =>
            Directory.CreateDirectory(Path.Combine(PlanStoreLayout.DirectoryFor(Root, sha), "plans"));

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // 임시 폴더다. 못 지워도 회차 결과에 영향이 없다.
            }
        }
    }
}
