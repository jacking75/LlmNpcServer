using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Npc.Narrative;

public static partial class Lexicon
{
    /// <summary>
    /// 화면 용어표 (H21). <b>화면 낱말 → 코드 이름</b>이다.
    ///
    /// <para>
    /// <b>같은 것을 두 이름으로 부르면 초보자는 그것이 두 가지라고 믿는다.</b>
    /// 화면은 "직업" 이라 하고 토스트는 "아키타입 'x' 이 이미 있다" 라고 하면,
    /// 사람은 직업과 아키타입의 관계를 먼저 배워야 한다 — 그것이 이 도구의 목적은 아니다.
    /// </para>
    ///
    /// <para>
    /// 코드 이름은 사라지지 않는다. <b>부제·ⓘ·고급 화면에서만</b> 쓴다 —
    /// 사람이 파일을 열었을 때 이어 붙일 수 있어야 하기 때문이다.
    /// </para>
    /// </summary>
    public static class Ui
    {
        /// <summary>화면 낱말 → 코드 이름.</summary>
        public static readonly FrozenDictionary<string, string> Words =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["직업"] = "archetype",
                ["NPC"] = "npc_instances",
                ["개별 설정"] = "npc_overrides",
                ["장소"] = "pois",
                ["지역"] = "zones",
                ["하루 일과"] = "fallback_plans",
                ["돌발 반응"] = "interrupts",
                ["미리 구운 계획"] = "planstore",
                ["상황(버킷)"] = "BucketKey",
                ["연습장"] = "lab/",
                ["초안 취소"] = "-",
                ["백업에서 복원"] = "backup/",
                ["검증하고 저장"] = "ValidateAndWrite",
                ["다시 만들기"] = "gen_*.cs",
            }.ToFrozenDictionary(StringComparer.Ordinal);

        /// <summary>
        /// 화면에 나오면 안 되는 개발자 낱말 (H21).
        ///
        /// <b>여기 있는 말이 토스트·버튼·배너에 나오면 드리프트 테스트가 잡는다.</b>
        /// 파일 이름(<c>archetypes.json</c>)과 코드 조각은 그대로 쓴다 — 그것은 주소다.
        /// </summary>
        public static readonly ImmutableArray<string> Forbidden =
        [
            "아키타입", "오버라이드", "폴백", "인스턴스", "프리베이크", "샌드박스", "리젠",
        ];

        /// <summary>이 문장이 개발자 낱말을 쓰는가. 쓰면 그 낱말을 돌려준다.</summary>
        /// <param name="text">화면 문장.</param>
        public static string? OffendingWord(string text)
        {
            ArgumentNullException.ThrowIfNull(text);

            foreach (string word in Forbidden)
            {
                if (text.Contains(word, StringComparison.Ordinal))
                {
                    return word;
                }
            }

            return null;
        }
    }
}
