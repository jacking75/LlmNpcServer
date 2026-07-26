using System.Text.Json;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.Core.Validation;
using Npc.Llm;
using Npc.MasterData;

namespace Npc.Tests.Llm;

/// <summary>
/// T2-05 — 프리픽스 불변성. docs/01 §10.2 · CLAUDE.md §2.5.
///
/// <b>리스크 R11(캐시 미적중)의 탐지 장치다.</b> 프리픽스가 1바이트라도 흔들리면
/// 프롬프트 캐시가 전면 미적중이 되고 비용이 그대로 튄다.
/// </summary>
public sealed class PromptPrefixTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);
    private static readonly PromptPrefix s_prefix = PromptPrefix.Build(s_data, TestPaths.MasterData);

    [Fact]
    public void PromptPrefix_IsByteIdentical_Across1000Builds()
    {
        string expected = s_prefix.Sha256;

        // 병렬로 돌린다 — 순차 1000회는 느리고, 동시 조립에서 SHA 가 갈라지는 것도 같이 잡힌다.
        Parallel.For(0, 1_000, _ =>
        {
            PromptPrefix again = PromptPrefix.Build(s_data, TestPaths.MasterData);

            Assert.Equal(expected, again.Sha256);
        });
    }

    [Fact]
    public void PromptPrefix_MeetsTokenFloor()
    {
        // 상한은 단언하지 않는다 — 정식 카탈로그의 실측은 11,967 tok 이고(W6_compile_stats.md §1),
        // 캐시에 필요한 것은 하한뿐이다. 상한을 지키려고 카탈로그를 줄이지 않는다.
        Assert.True(
            s_prefix.TokenCount >= PromptPrefix.TokenFloor,
            $"프리픽스가 {s_prefix.TokenCount} 토큰이다. {PromptPrefix.TokenFloor} 미만이면 캐시가 안 걸린다.");
    }

    [Fact]
    public void PromptPrefix_ExposesTextShaAndTokenCount()
    {
        Assert.False(string.IsNullOrEmpty(s_prefix.Text));
        Assert.Equal(64, s_prefix.Sha256.Length);
        Assert.Equal(PromptPrefix.CountTokens(s_prefix.Text), s_prefix.TokenCount);
        // 절 합계와 전체가 정확히 같지는 않다 — 절 사이의 개행이 토큰 경계를 바꾼다.
        int sum = s_prefix.Sections.Sum(s => s.Tokens);
        Assert.InRange(sum, s_prefix.TokenCount - 60, s_prefix.TokenCount + 60);
    }

    [Fact]
    public void PromptPrefix_HasNoCarriageReturn()
    {
        // CRLF 로 체크아웃된 기계에서 SHA 가 달라지면 캐시가 통째로 미적중이 된다.
        Assert.DoesNotContain('\r', s_prefix.Text);
    }

    [Fact]
    public void PromptPrefix_ProfilesDifferInSchemaOnly()
    {
        PromptPrefix bare = PromptPrefix.Build(s_data, TestPaths.MasterData, SchemaProfile.Bare);

        Assert.NotEqual(s_prefix.Sha256, bare.Sha256);

        foreach (string section in new[] { "system_rules", "action_catalog", "archetypes", "fewshot" })
        {
            Assert.Equal(
                s_prefix.Sections.Single(s => s.Name == section).Tokens,
                bare.Sections.Single(s => s.Name == section).Tokens);
        }
    }

    /// <summary>
    /// few-shot 은 프리픽스에 그대로 실려 모델이 그대로 흉내낸다.
    /// <b>검증기가 반려할 플랜을 예시로 실으면 통과율이 그만큼 내려간다.</b>
    /// </summary>
    [Fact]
    public void FewShot_ExamplesPassValidators()
    {
        foreach (string file in FewShotFiles())
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
            JsonElement request = document.RootElement.GetProperty("request");

            (ArchetypeDef archetype, BucketKey bucket) = BucketOf(request);
            string planJson = document.RootElement.GetProperty("plan").GetRawText();
            string name = Path.GetFileName(file);

            ValidationResult schema = SchemaValidator.Validate(planJson, out PlanDocument? plan);
            Assert.True(schema.IsValid, $"{name}: {schema.Code} {schema.Detail}");

            ValidationResult vocabulary = VocabularyValidator.Validate(plan!, archetype.Code, s_data);
            Assert.True(vocabulary.IsValid, $"{name}: {vocabulary.Code} {vocabulary.Detail}");

            ValidationResult coherence = CoherenceValidator.Validate(plan!, bucket, archetype.Code, s_data);
            Assert.True(coherence.IsValid, $"{name}: {coherence.Code} {coherence.Detail}");
        }
    }

    /// <summary>
    /// 예시의 <c>flags</c> 는 서픽스가 실제로 만들 값과 같아야 한다 —
    /// 예시가 요청 형식을 가르치는데 그 형식이 진짜와 다르면 모델을 잘못 가르친다.
    /// </summary>
    [Fact]
    public void FewShot_RequestFlagsMatchDerivedInitialFlags()
    {
        foreach (string file in FewShotFiles())
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
            JsonElement request = document.RootElement.GetProperty("request");

            (_, BucketKey bucket) = BucketOf(request);

            WorldFlags declared = WorldFlags.None;
            foreach (JsonElement flag in request.GetProperty("flags").EnumerateArray())
            {
                Assert.True(WorldFlagTable.TryParse(flag.GetString(), out WorldFlags parsed), flag.ToString());
                declared |= parsed;
            }

            Assert.Equal(
                WorldFlagTable.Format(s_data.InitialFlags(bucket)),
                WorldFlagTable.Format(declared));
        }
    }

    [Fact]
    public void FewShot_ExamplesAreEmbeddedInThePrefix()
    {
        var compact = new JsonSerializerOptions { WriteIndented = false };

        foreach (string file in FewShotFiles())
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));

            // _comment 같은 주석 필드는 프리픽스에 실리지 않는다.
            Assert.Contains(
                JsonSerializer.Serialize(document.RootElement.GetProperty("plan"), compact),
                s_prefix.Text,
                StringComparison.Ordinal);
        }

        Assert.DoesNotContain("_comment", s_prefix.Text, StringComparison.Ordinal);
    }

    private static string[] FewShotFiles() =>
        [.. Directory.GetFiles(Path.Combine(TestPaths.MasterData, "prompt", "fewshot"), "*.json")
            .Order(StringComparer.Ordinal)];

    private static (ArchetypeDef Archetype, BucketKey Bucket) BucketOf(JsonElement request)
    {
        Assert.True(
            s_data.Archetypes.TryGet(request.GetProperty("archetype").GetString()!, out ArchetypeDef archetype));

        var bucket = new BucketKey(
            archetype.Code,
            Enum.Parse<TimeOfDay>(request.GetProperty("time_of_day").GetString()!),
            Enum.Parse<RegionState>(request.GetProperty("region_state").GetString()!),
            Enum.Parse<Climate>(request.GetProperty("climate").GetString()!));

        return (archetype, bucket);
    }
}
