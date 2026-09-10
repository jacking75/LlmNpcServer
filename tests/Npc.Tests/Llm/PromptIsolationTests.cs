using System.Reflection;
using System.Text.RegularExpressions;
using Npc.Contracts;
using Npc.Core;
using Npc.Llm;
using Npc.MasterData;

namespace Npc.Tests.Llm;

/// <summary>
/// C-06 — 플레이어 문자열이 프롬프트에 새지 않는다.
///
/// <b>지금까지 이 규칙은 타입 설계로만 지켜졌다.</b> <c>PlanRequest</c> 에 <c>string</c> 필드를
/// 하나 넣는 변경을 막는 장치가 없었고, 그런 변경은 리뷰에서 놓치기 쉽다 —
/// "NPC 이름을 프롬프트에 넣으면 더 좋은 플랜이 나오지 않을까" 는 자연스러운 생각이다.
///
/// <para>
/// 그 순간 프롬프트 인젝션이 열린다. 캐릭터명·채팅·길드명은 전부 플레이어가 쓴 문자열이다.
/// </para>
/// </summary>
public sealed partial class PromptIsolationTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    /// <summary>서픽스에 실리는 타입 전부. 하나라도 문자열을 들면 안 된다.</summary>
    public static IReadOnlyList<Type> SuffixTypeList { get; } =
    [
        typeof(PlanRequest),
        typeof(NpcSnapshot),
        typeof(RecentEvent),
        typeof(InventorySlot),
    ];

    /// <summary>위 목록의 xunit 표현.</summary>
    public static TheoryData<Type> SuffixTypes
    {
        get
        {
            var data = new TheoryData<Type>();

            foreach (Type type in SuffixTypeList)
            {
                data.Add(type);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(SuffixTypes))]
    public void SuffixTypes_HaveNoStringFields(Type type)
    {
        foreach (MemberInfo member in Members(type))
        {
            Type memberType = member switch
            {
                PropertyInfo property => property.PropertyType,
                FieldInfo field => field.FieldType,
                _ => typeof(void),
            };

            Assert.False(
                IsTextual(memberType),
                $"{type.Name}.{member.Name} 이 문자열이다. "
                + "플레이어가 쓴 문자열이 프롬프트에 새는 길이다 (CLAUDE.md §2.5). "
                + "강타입 enum 이나 내부 id 로 바꾼다.");
        }
    }

    [Fact]
    public void SuffixTypes_AreValueTypes()
    {
        // 값 타입이면 참조 필드를 들 수 없고, 참조 필드가 없으면 문자열이 숨어들 자리가 없다.
        // ImmutableArray<T> 도 값 타입이라 여기 걸리지 않는다.
        foreach (Type type in SuffixTypeList)
        {
            Assert.True(type.IsValueType, $"{type.Name} 이 값 타입이 아니다");
        }
    }

    [Fact]
    public void RenderedSuffix_ContainsOnlySafeCharacters()
    {
        // 서픽스 전체를 훑어 허용 문자 밖이 있는지 본다. 플레이어 문자열이 들어오면
        // 거의 반드시 여기에 걸린다 — 한글·이모지·따옴표는 전부 허용 밖이다.
        int checkedBuckets = 0;

        foreach (BucketKey bucket in SampleBuckets())
        {
            var request = new PlanRequest(bucket, s_data.InitialFlags(bucket));
            string suffix = PlanRequestSuffix.Build(in request, s_data);

            Match unsafeMatch = UnsafePattern().Match(suffix);

            Assert.False(
                unsafeMatch.Success,
                $"버킷 {bucket} 의 서픽스에 허용 밖 문자가 있다: '{unsafeMatch.Value}'");

            checkedBuckets++;
        }

        Assert.True(checkedBuckets > 0, "표본이 비었다");
    }

    [Fact]
    public void PlanRequest_HasNoObjectOrCollectionOfStrings()
    {
        foreach (MemberInfo member in Members(typeof(PlanRequest)))
        {
            Type memberType = member switch
            {
                PropertyInfo property => property.PropertyType,
                FieldInfo field => field.FieldType,
                _ => typeof(void),
            };

            Assert.NotEqual(typeof(object), memberType);

            if (memberType.IsGenericType)
            {
                foreach (Type argument in memberType.GetGenericArguments())
                {
                    Assert.False(
                        IsTextual(argument),
                        $"PlanRequest.{member.Name} 이 문자열 컬렉션이다");
                }
            }
        }
    }

    private static IEnumerable<BucketKey> SampleBuckets()
    {
        // 아키타입 전체 × 시간대 전체를 훑는다. 지역·기후는 대표 하나씩이면 충분하다 —
        // 서픽스에 실리는 것은 플래그이고 그 조합은 시간대가 가장 넓게 흔든다.
        for (int archetype = 0; archetype < s_data.Archetypes.Count; archetype++)
        {
            foreach (TimeOfDay time in Enum.GetValues<TimeOfDay>())
            {
                yield return new BucketKey(
                    new ArchetypeId((ushort)archetype), time, RegionState.Peace, Climate.Fair);
            }
        }
    }

    private static IEnumerable<MemberInfo> Members(Type type)
    {
        foreach (PropertyInfo property in type.GetProperties(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            // record 의 EqualityContract 는 Type 이지 데이터가 아니다.
            if (property.Name != "EqualityContract")
            {
                yield return property;
            }
        }

        foreach (FieldInfo field in type.GetFields(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            // record 의 백킹 필드는 위 속성과 같은 것이다.
            if (!field.Name.Contains("k__BackingField", StringComparison.Ordinal))
            {
                yield return field;
            }
        }
    }

    private static bool IsTextual(Type type) =>
        type == typeof(string) || type == typeof(char[]) || type == typeof(System.Text.StringBuilder);

    /// <summary>허용 문자. id·enum·숫자에 쓰이는 것만이다.</summary>
    [GeneratedRegex(@"[^A-Za-z0-9_$:.@,\-()\[\]{}#=/*""'|<>+ \r\n\t]")]
    private static partial Regex UnsafePattern();
}
