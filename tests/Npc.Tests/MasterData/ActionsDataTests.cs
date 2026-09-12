using System.Text.Json;

namespace Npc.Tests.MasterData;

/// <summary>
/// masterdata/actions.json 중 <b>로더도 검증기도 보지 않는 것</b>만 남긴다.
/// code·id 중복, 플래그 참조, 명령·이벤트 이름, params 타입, enum values 는
/// <see cref="Npc.MasterData.ActionCatalog"/> 로더가 던지고 V1/V2 가 잡는다 — 여기서 다시 세지 않는다.
/// </summary>
public sealed class ActionsDataTests
{
    private static readonly JsonDocument s_actions = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(TestPaths.MasterData, "actions.json")));

    private static JsonElement[] Actions() =>
        s_actions.RootElement.GetProperty("actions").EnumerateArray().ToArray();

    /// <summary>
    /// 로더의 사각지대 둘. <c>default_timeout_s</c> 가 0 이면 모든 스텝이 즉시 타임아웃하고,
    /// int 파라미터에 min/max 가 없으면 4단 검증기가 LLM 이 준 임의의 정수를 통과시킨다.
    /// 둘 다 로더는 통과시키므로 여기서 본다.
    /// </summary>
    [Fact]
    public void ActionsJson_TimeoutsAndIntBoundsAreUsable()
    {
        foreach (JsonElement action in Actions())
        {
            string id = action.GetProperty("id").GetString()!;

            Assert.InRange(action.GetProperty("default_timeout_s").GetInt32(), 5, 7200);

            foreach (JsonProperty p in action.GetProperty("params").EnumerateObject())
            {
                if (p.Value.GetProperty("type").GetString() != "int")
                {
                    continue;
                }

                Assert.True(p.Value.TryGetProperty("min", out _), $"{id}.{p.Name}: int 인데 min 이 없다.");
                Assert.True(p.Value.TryGetProperty("max", out _), $"{id}.{p.Name}: int 인데 max 가 없다.");
            }
        }
    }

    /// <summary>W1 스파이크가 쓴 12개의 code 는 재배치되면 안 된다 (spike/data/actions.min.json).</summary>
    [Theory]
    [InlineData("MoveTo", 1)]
    [InlineData("Work", 6)]
    [InlineData("Gather", 7)]
    [InlineData("Craft", 11)]
    [InlineData("Talk", 14)]
    [InlineData("Trade", 15)]
    [InlineData("Eat", 20)]
    [InlineData("Sleep", 22)]
    [InlineData("Rest", 23)]
    [InlineData("Guard", 27)]
    [InlineData("Store", 32)]
    [InlineData("Wait", 35)]
    public void ActionsJson_SpikeCodesAreStable(string id, int code)
    {
        JsonElement action = Actions().Single(a => a.GetProperty("id").GetString() == id);

        Assert.Equal(code, action.GetProperty("code").GetInt32());
    }
}
