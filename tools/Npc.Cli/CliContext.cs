using System.Collections.Immutable;
using Npc.MasterData;

namespace Npc.Cli;

/// <summary>
/// 한 번 실행에 공통인 것들 (F-01).
///
/// <b>마스터데이터는 늦게 읽는다.</b> <c>next-code</c> 나 <c>hints</c> 처럼 로더가 필요 없는
/// 명령도 있고, 3MB 를 매번 읽으면 터미널에서 쓰기 불편하다.
/// </summary>
public sealed class CliContext
{
    private MasterDataSet? _data;

    /// <summary><c>masterdata/</c> 경로.</summary>
    public required string MasterData { get; init; }

    /// <summary><c>planstore/</c> 경로.</summary>
    public required string PlanStore { get; init; }

    /// <summary>기계가 읽는 출력인가.</summary>
    public bool Json { get; init; }

    /// <summary>파일을 실제로 고칠 것인가. 기본은 dry-run 이다.</summary>
    public bool Apply { get; init; }

    /// <summary>표준출력.</summary>
    public required TextWriter Out { get; init; }

    /// <summary>인구표의 기준 NPC 수.</summary>
    public int Population { get; init; } = 5_000;

    /// <summary>필요할 때 한 번만 읽는다.</summary>
    public MasterDataSet Data => _data ??= MasterDataLoader.Load(MasterData);

    /// <summary>남은 인자 (플래그를 뺀 위치 인자).</summary>
    public required ImmutableArray<string> Args { get; init; }

    /// <summary>위치 인자 하나. 없으면 null.</summary>
    public string? Arg(int index) => index < Args.Length ? Args[index] : null;
}
