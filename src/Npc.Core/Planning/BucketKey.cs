using Npc.Contracts;

namespace Npc.Core;

// docs/01 §6. 플랜 캐시의 키 공간.
//
// 이 파일이 Npc.Planning 이 아니라 Npc.Core 에 있는 이유:
//   docs/03 §5 의 CompiledPlan.Bucket 이 BucketKey 를 들고 있고 CompiledPlan 은 Npc.Core 에 있다.
//   BucketKey 를 Npc.Planning 에 두면 Core → Planning → Core 순환이 생긴다.
//   TimeOfDay 는 GameClock(Npc.Runtime)도 쓰므로 Core 가 자연스러운 자리다.
//
// 아래 세 열거형의 ordinal 은 context_buckets.json 의 values 순서이자
// GameEvent.Code 에 실리는 값이다 (docs/02 §3.3). 재배치하면 프리베이크 플랜이 전부 깨진다.

/// <summary>시간대. context_buckets.json 의 time_of_day.values 순서.</summary>
public enum TimeOfDay : byte
{
    Dawn = 0,
    Morning = 1,
    Noon = 2,
    Afternoon = 3,
    Evening = 4,
    Night = 5,
}

/// <summary>지역 상태. context_buckets.json 의 region_state.values 순서.</summary>
public enum RegionState : byte
{
    Peace = 0,
    Alert = 1,
    War = 2,
    Disaster = 3,
}

/// <summary>기후. context_buckets.json 의 climate.values 순서.</summary>
public enum Climate : byte
{
    Fair = 0,
    Cold = 1,
    Storm = 2,
}

/// <summary>
/// 플랜 캐시 키. (아키타입 × 시간대 × 지역상태 × 기후). docs/01 §6.
/// 플랜 스토어는 아키타입 수 × 72 짜리 고정 배열이면 충분하다 —
/// 조회는 <see cref="ToIndex"/> 한 번이고 해시맵이 필요 없다.
///
/// <para>
/// <b>아키타입 수는 이 타입이 모른다</b> (F-05). masterdata 가 정하고
/// <c>MasterDataSet.Buckets</c>(<c>BucketSpace</c>)가 들고 있다 — 코드 상수로 두면
/// 아키타입 하나 추가에 C# 수정과 빌드가 따라오고, 파일이 41 인데 바이너리가 40 이면
/// 가장 찾기 어려운 종류의 오류가 난다.
/// </para>
/// </summary>
public readonly record struct BucketKey(ArchetypeId A, TimeOfDay T, RegionState R, Climate C)
{
    /// <summary>시간대 수.</summary>
    public const int TimeOfDayCount = 6;

    /// <summary>지역 상태 수.</summary>
    public const int RegionStateCount = 4;

    /// <summary>기후 수.</summary>
    public const int ClimateCount = 3;

    /// <summary>
    /// 한 아키타입이 갖는 버킷 수. 6 × 4 × 3 = 72.
    /// <b>아키타입 수와 무관하다</b> — 열거형 세 개가 정한다.
    /// </summary>
    public const int PerArchetype = TimeOfDayCount * RegionStateCount * ClimateCount;

    /// <summary>
    /// 첨자. 전단사다.
    ///
    /// <b>이 식에 아키타입 수가 들어가지 않는다</b> — 아키타입은 가장 바깥 차원이라
    /// 뒤에 추가해도 기존 인덱스가 그대로다. 프리베이크 플랜 2,880개가 아키타입 추가로
    /// 깨지지 않는 이유가 이것이다 (CLAUDE.md §2.4).
    /// </summary>
    public int ToIndex() => ((((A.Value * TimeOfDayCount) + (int)T) * RegionStateCount) + (int)R) * ClimateCount + (int)C;

    /// <summary>
    /// <see cref="ToIndex"/> 의 역함수.
    ///
    /// <b>아키타입 수 상한은 보지 않는다</b> — 그것을 아는 것은 <c>BucketSpace</c> 다.
    /// 로스터 안인지까지 확인해야 하면 <c>BucketSpace.FromIndex</c> 를 쓴다.
    /// </summary>
    public static BucketKey FromIndex(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        // ushort 로 좁히므로 넘치는 첨자는 조용히 다른 아키타입이 된다. 그것만 막는다.
        if (index >= (ushort.MaxValue + 1) * PerArchetype)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index), $"버킷 인덱스가 ArchetypeId 범위를 넘는다: {index}");
        }

        int c = index % ClimateCount;
        int rest = index / ClimateCount;
        int r = rest % RegionStateCount;
        rest /= RegionStateCount;
        int t = rest % TimeOfDayCount;
        int a = rest / TimeOfDayCount;

        return new BucketKey(new ArchetypeId((ushort)a), (TimeOfDay)t, (RegionState)r, (Climate)c);
    }

    /// <summary>이 아키타입 차원을 제외한 나머지가 같은 키인가.</summary>
    public bool SameContext(BucketKey other) => T == other.T && R == other.R && C == other.C;

    /// <summary>
    /// 플랜 스토어 파일명. docs/03 §7 의 <c>blacksmith@Dawn.Peace.Fair.json</c> 형식.
    /// 아키타입의 문자열 id 는 이 타입이 모르므로 호출자가 준다.
    /// </summary>
    public string Format(string archetypeId) => $"{archetypeId}@{T}.{R}.{C}";

    /// <summary>아키타입 문자열 id 를 모를 때의 표기. 로그·디버깅용.</summary>
    public override string ToString() => $"{A.Value}@{T}.{R}.{C}";
}
