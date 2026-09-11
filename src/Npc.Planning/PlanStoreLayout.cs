namespace Npc.Planning;

/// <summary>어느 배치를 고랐나 (C-03).</summary>
public enum PlanStoreShape
{
    /// <summary>프리픽스 SHA 별 디렉터리 — <c>planstore/&lt;sha8&gt;/</c>.</summary>
    Versioned = 0,

    /// <summary>
    /// 옛 평면 배치 — <c>planstore/plans/</c>. C-03 이전에 만든 스토어다.
    /// <b>어느 프리픽스로 만든 것인지 경로에 안 적혀 있다</b> — manifest 를 열어야 안다.
    /// </summary>
    Flat = 1,

    /// <summary>이 프리픽스의 스토어가 없다. 프리베이크가 필요하다.</summary>
    Missing = 2,
}

/// <summary>
/// 프리픽스 SHA 별 플랜 스토어 배치 (C-03).
///
/// <code>
/// planstore/
///   pinned/                  ← 공유. 사람이 검수한 것이라 프리픽스와 무관하다
///   prefix/&lt;sha8&gt;.md         ← 그 회차의 프리픽스 전문 (사람이 읽는다)
///   &lt;sha8&gt;/plans/            ← 프리베이크 산출물
///   &lt;sha8&gt;/rejected/
///   &lt;sha8&gt;/manifest.json
/// </code>
///
/// <para>
/// <b>왜 갈랐나.</b> <c>system_rules.md</c> 한 줄을 고치면 2,880 버킷이 전부 미적중되는데,
/// 예전에는 디렉터리가 하나라 <b>새 회차가 옛 회차를 덮었다</b> — 되돌리려면 다시 만들어야
/// 했고 그것은 $5 와 5분이다. 이제 롤백은 <b>프롬프트 파일을 되돌리는 것</b>이고,
/// SHA 가 이전 값이 되면 그 디렉터리가 그대로 선택된다.
/// </para>
///
/// <para>
/// <b><c>pinned/</c> 는 공유한다.</b> 핀은 사람이 검수한 플랜이고 프리픽스가 바뀌었다고
/// 그 검수가 무효가 되지는 않는다 — 무효 판정은 <see cref="PlanStoreValidator"/> 가
/// 마스터데이터 기준으로 따로 한다. 회차마다 핀을 복사하면 <b>어느 쪽이 진짜인지</b> 모르게 된다.
/// </para>
///
/// <para>
/// <b>옛 평면 배치를 버리지 않는다.</b> 이미 있는 <c>planstore/plans/</c> 는 그대로 읽힌다 —
/// 있는 산출물을 못 쓰게 만드는 이주는 이주가 아니라 파괴다.
/// </para>
/// </summary>
/// <param name="Root">스토어 루트. <c>planstore/</c>.</param>
/// <param name="Directory">플랜·manifest 를 읽고 쓸 곳.</param>
/// <param name="PinnedRoot"><c>pinned/</c> 가 있는 곳. 공유라 항상 <paramref name="Root"/> 다.</param>
/// <param name="Shape">어느 배치인가.</param>
/// <param name="ShortSha">고른 프리픽스의 짧은 SHA. 평면 배치면 빈 문자열.</param>
public readonly record struct PlanStoreLayout(
    string Root,
    string Directory,
    string PinnedRoot,
    PlanStoreShape Shape,
    string ShortSha)
{
    /// <summary>프리픽스 전문을 보관하는 폴더 이름.</summary>
    public const string PrefixFolderName = "prefix";

    /// <summary>공유 핀 폴더 이름.</summary>
    public const string PinnedFolderName = "pinned";

    /// <summary>
    /// 이 프리픽스에 맞는 배치를 고른다.
    /// </summary>
    /// <param name="root">스토어 루트.</param>
    /// <param name="prefixSha">프리픽스 SHA-256 전문. 빈 문자열이면 평면 배치를 쓴다.</param>
    /// <param name="pinnedSha">
    /// <c>--planstore-sha</c> 로 고정한 짧은 SHA. 비어 있으면 <paramref name="prefixSha"/> 를 쓴다.
    ///
    /// <b>고정은 진단용이다.</b> "이 회차의 플랜으로 돌려 보자" 를 프롬프트 파일을 건드리지 않고
    /// 해 보는 길이며, 운영에서는 프롬프트를 되돌리는 쪽이 정직하다 — 그래야 만들어진 플랜과
    /// 지금 쓰는 프롬프트가 같아진다.
    /// </param>
    public static PlanStoreLayout Resolve(string root, string prefixSha, string? pinnedSha = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);

        string sha = !string.IsNullOrWhiteSpace(pinnedSha)
            ? pinnedSha.Trim()
            : string.IsNullOrWhiteSpace(prefixSha) ? string.Empty : Short(prefixSha);

        if (sha.Length > 0)
        {
            string versioned = Path.Combine(root, sha);

            if (HasStore(versioned))
            {
                return new PlanStoreLayout(root, versioned, root, PlanStoreShape.Versioned, sha);
            }
        }

        // 옛 평면 배치. 있으면 그대로 읽는다 — 있는 산출물을 버리지 않는다.
        if (HasStore(root))
        {
            return new PlanStoreLayout(root, root, root, PlanStoreShape.Flat, string.Empty);
        }

        // 아무것도 없다. 쓸 자리는 알려 주되 "없다" 고 말한다.
        return new PlanStoreLayout(
            root,
            sha.Length > 0 ? Path.Combine(root, sha) : root,
            root,
            PlanStoreShape.Missing,
            sha);
    }

    /// <summary>이 프리픽스가 쓸 디렉터리. 없어도 경로는 준다 (프리베이크가 만든다).</summary>
    /// <param name="root">스토어 루트.</param>
    /// <param name="prefixSha">프리픽스 SHA-256 전문.</param>
    public static string DirectoryFor(string root, string prefixSha)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentException.ThrowIfNullOrEmpty(prefixSha);

        return Path.Combine(root, Short(prefixSha));
    }

    /// <summary>프리픽스 전문을 쓸 경로. <c>planstore/prefix/&lt;sha8&gt;.md</c>.</summary>
    /// <param name="root">스토어 루트.</param>
    /// <param name="prefixSha">프리픽스 SHA-256 전문.</param>
    public static string PrefixArtifactPath(string root, string prefixSha)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentException.ThrowIfNullOrEmpty(prefixSha);

        return Path.Combine(root, PrefixFolderName, Short(prefixSha) + ".md");
    }

    /// <summary>
    /// 프리픽스 전문을 보관한다. <b>이미 있으면 덮지 않는다</b> (C-03).
    ///
    /// <para>
    /// 같은 SHA 면 같은 내용이므로 덮을 이유가 없고, 덮으면 파일 시각이 흔들려
    /// "언제 만든 회차인가" 가 사라진다.
    /// </para>
    /// </summary>
    /// <param name="root">스토어 루트.</param>
    /// <param name="prefixSha">프리픽스 SHA-256 전문.</param>
    /// <param name="promptVersion">사람이 읽는 버전 라벨.</param>
    /// <param name="text">프리픽스 전문.</param>
    /// <returns>새로 썼으면 true. 이미 있었으면 false.</returns>
    public static bool SavePrefix(string root, string prefixSha, string promptVersion, string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentException.ThrowIfNullOrEmpty(prefixSha);
        ArgumentNullException.ThrowIfNull(text);

        string path = PrefixArtifactPath(root, prefixSha);

        if (File.Exists(path))
        {
            return false;
        }

        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // 머리말은 사람을 위한 것이다. 이 파일을 여는 이유는 "이 플랜이 어떤 프롬프트로
        // 만들어졌나" 하나뿐이고, 그 답이 첫 세 줄에 있어야 한다.
        File.WriteAllText(
            path,
            $"<!-- prompt_version: {promptVersion} -->\n"
            + $"<!-- prefix_sha256: {prefixSha} -->\n"
            + "<!-- 생성물이다. 손으로 고치지 않는다 — 고치면 SHA 와 내용이 어긋난다. -->\n\n"
            + text);

        return true;
    }

    /// <summary>사람이 읽는 한 줄. 로그에 그대로 나간다.</summary>
    public override string ToString() => Shape switch
    {
        PlanStoreShape.Versioned => $"{Directory} (프리픽스 {ShortSha})",
        PlanStoreShape.Flat => $"{Directory} (평면 배치 — 어느 프리픽스인지 manifest 를 봐야 안다)",
        _ => $"{Directory} (없음 — 프리베이크가 필요하다)",
    };

    private static string Short(string sha) => sha.Length <= 8 ? sha : sha[..8];

    /// <summary>여기에 플랜 스토어가 있는가. 표식은 manifest 나 plans/ 다.</summary>
    private static bool HasStore(string directory) =>
        File.Exists(Path.Combine(directory, Manifest.FileName))
        || System.IO.Directory.Exists(Path.Combine(directory, PlanStoreIo.FolderOf(PlanLayer.Plans)));
}
