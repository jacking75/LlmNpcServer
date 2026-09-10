using System.Text.Json;
using System.Text.Json.Nodes;
using Npc.MasterData;

namespace Npc.Tests.MasterData;

/// <summary>
/// B-04 — 구조 해시와 내용 해시의 분할.
///
/// <b>여기서 잡으려는 것.</b> 마스터데이터가 한 글자만 달라도 링크가 안 붙던 것을 고치되,
/// <c>code</c>·<c>bit</c> 재배치는 여전히 거절되어야 한다. 두 성질을 같이 본다.
/// </summary>
public sealed class HashSplitTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "npc-hash-" + Guid.NewGuid().ToString("N"));

    public HashSplitTests() => CopyMasterData(TestPaths.MasterData, _dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void StructuralHash_IsDeterministic()
    {
        MasterDataSet a = MasterDataLoader.Load(TestPaths.MasterData);
        MasterDataSet b = MasterDataLoader.Load(TestPaths.MasterData);

        Assert.Equal(a.StructuralHash, b.StructuralHash);
        Assert.NotEqual(a.StructuralHash, a.ContentHash);
    }

    [Fact]
    public void DescriptionChange_LeavesStructuralHashAlone()
    {
        MasterDataSet before = MasterDataLoader.Load(_dir);

        // desc 는 프롬프트에 실리지만 두 프로세스가 같은 번호로 같은 것을 가리키는 데에는
        // 아무 상관이 없다. 이것이 링크를 끊을 이유가 되면 안 된다.
        Rewrite("archetypes.json", root =>
        {
            JsonNode archetypes = root["archetypes"]!;

            archetypes[0]!["desc"] = "설명을 바꿨다. 구조는 그대로다.";
        });

        MasterDataSet after = MasterDataLoader.Load(_dir);

        Assert.Equal(before.StructuralHash, after.StructuralHash);

        // 내용 해시는 바뀐다 — 경고로 수락되지만 "달라졌다" 는 사실은 남아야 한다.
        Assert.NotEqual(before.ContentHash, after.ContentHash);
    }

    [Fact]
    public void PoiPositionChange_MovesStructuralHash()
    {
        MasterDataSet before = MasterDataLoader.Load(_dir);

        // 게임서버가 POI 좌표를 다르게 알면 NPC 가 엉뚱한 곳으로 간다. 거절이어야 한다.
        Rewrite("pois.json", root =>
        {
            JsonNode poi = root["pois"]![0]!;

            poi["pos"]![0] = poi["pos"]![0]!.GetValue<double>() + 100;
        });

        MasterDataSet after = MasterDataLoader.Load(_dir);

        Assert.NotEqual(before.StructuralHash, after.StructuralHash);
    }

    [Fact]
    public void ItemCodeChange_MovesStructuralHash()
    {
        MasterDataSet before = MasterDataLoader.Load(_dir);
        int highest = before.Items.Items.Max(i => i.Code.Value);

        // code 재배치는 여전히 거절이다 (CLAUDE.md §2.4). 그것이 이 분할이 지키는 것이다.
        Rewrite("items.json", root =>
        {
            JsonNode item = root["items"]![0]!;

            item["code"] = highest + 1;
        });

        MasterDataSet after = MasterDataLoader.Load(_dir);

        Assert.NotEqual(before.StructuralHash, after.StructuralHash);
    }

    [Fact]
    public void FormattingChange_MovesNeitherHashMeaningfully()
    {
        MasterDataSet before = MasterDataLoader.Load(_dir);

        // 들여쓰기만 바꾼다. 구조는 물론이고 의미도 그대로다.
        string path = Path.Combine(_dir, "zones.json");
        JsonNode root = JsonNode.Parse(File.ReadAllText(path))!;

        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));

        MasterDataSet after = MasterDataLoader.Load(_dir);

        // 구조 해시는 로드된 표에서 뽑으므로 서식에 흔들리지 않는다.
        Assert.Equal(before.StructuralHash, after.StructuralHash);
    }

    private void Rewrite(string fileName, Action<JsonNode> edit)
    {
        string path = Path.Combine(_dir, fileName);
        JsonNode root = JsonNode.Parse(File.ReadAllText(path))!;

        edit(root);

        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void CopyMasterData(string from, string to)
    {
        Directory.CreateDirectory(to);

        foreach (string file in Directory.EnumerateFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        }

        foreach (string dir in Directory.EnumerateDirectories(from))
        {
            CopyMasterData(dir, Path.Combine(to, Path.GetFileName(dir)));
        }
    }
}
