using System.Collections.Immutable;
using Npc.Contracts;

namespace Npc.Conformance;

/// <summary>
/// 게임서버가 보낸 것 하나. <b>도착 순서를 그대로 보존한다</b> —
/// 검사 대부분이 "무엇이 무엇보다 먼저 왔는가" 를 본다.
/// </summary>
/// <param name="Event">이벤트.</param>
/// <param name="Frame">몇 번째 프레임에 실려 왔는가. 프레임당 상한 검사에 쓴다.</param>
/// <param name="ElapsedMillis">관찰 시작으로부터의 벽시계 ms. 0 이면 구동 회차다.</param>
public readonly record struct Observed(GameEvent Event, int Frame, long ElapsedMillis);

/// <summary>
/// 명령 하나를 낸 기록. 응답이 왔는지를 <see cref="Checks.CommandResponseCheck"/> 가 본다.
/// </summary>
/// <param name="Correlation">상관 ID (N5).</param>
/// <param name="Kind">명령 종류.</param>
/// <param name="Npc">대상 NPC.</param>
/// <param name="IssuedTick">발행 틱.</param>
/// <param name="TimeoutTicks">이 틱 안에 응답이 와야 한다.</param>
public readonly record struct Issued(
    CorrelationId Correlation, NpcCommandKind Kind, NpcId Npc, long IssuedTick, long TimeoutTicks);

/// <summary>
/// 한 회차의 관찰 기록 (B-07).
///
/// <para>
/// <b>검사는 전부 이 자료구조 위의 순수 함수다.</b> 소켓을 붙여야만 돌릴 수 있는 검사는
/// 고의 위반을 만들어 시험할 수 없고, 시험하지 않은 검사는 <b>있다고 믿기만 하는 검사</b>다.
/// 그래서 관찰(소켓)과 판정(순수)을 갈랐다.
/// </para>
/// </summary>
public sealed record Observation
{
    /// <summary>협상된 프로토콜 버전. 0 이면 핸드셰이크가 안 끝났다.</summary>
    public int ProtocolVersion { get; init; }

    /// <summary>협상된 계약 부 버전.</summary>
    public ushort ContractMinor { get; init; }

    /// <summary>협상된 기능 비트.</summary>
    public ulong Features { get; init; }

    /// <summary>사람이 읽는 협상 결과.</summary>
    public string NegotiationDetail { get; init; } = string.Empty;

    /// <summary>링크가 붙었는가.</summary>
    public bool Connected { get; init; }

    /// <summary>기대한 NPC 수. 로스터 크기다.</summary>
    public int NpcCount { get; init; }

    /// <summary>기대한 존 수.</summary>
    public int ZoneCount { get; init; }

    /// <summary>받은 것 전부. 도착 순서다.</summary>
    public ImmutableArray<Observed> Events { get; init; } = [];

    /// <summary>우리가 낸 명령. 응답 검사가 본다.</summary>
    public ImmutableArray<Issued> Commands { get; init; } = [];

    /// <summary>관찰한 벽시계 시간(ms). <b>0 이면 구동 회차</b>라 속도 검사를 하지 않는다.</summary>
    public long WallClockMillis { get; init; }

    /// <summary>링크가 검출한 시퀀스 갭. 0 이어야 한다.</summary>
    public long EventGaps { get; init; }

    /// <summary>역압으로 버린 명령 수.</summary>
    public long CommandsDropped { get; init; }

    /// <summary>관찰이 끝난 마지막 틱.</summary>
    public long LastTick { get; init; }

    /// <summary>한 종류의 이벤트만.</summary>
    public IEnumerable<Observed> Of(GameEventKind kind)
    {
        foreach (Observed observed in Events)
        {
            if (observed.Event.Kind == kind)
            {
                yield return observed;
            }
        }
    }

    /// <summary>그 종류가 몇 건인가.</summary>
    public int Count(GameEventKind kind)
    {
        int count = 0;

        foreach (Observed observed in Events)
        {
            if (observed.Event.Kind == kind)
            {
                count++;
            }
        }

        return count;
    }
}
