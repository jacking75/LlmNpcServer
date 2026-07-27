using System.Globalization;
using System.Text;
using System.Text.Json;
using Npc.Contracts;
using Npc.Gateway;
using Npc.Host;

namespace Npc.Tests.Determinism;

/// <summary>
/// T5-07 — 결정론 리플레이. docs/15 §3.
///
/// <code>
/// 1. 시나리오 A 를 RecordingGameServerLink 로 실행  → run1/link.jsonl
/// 2. 같은 로그를 ReplayGameServerLink 로 재생      → run2 의 명령
/// 3. run1 의 명령 == run2 의 명령 (바이트 동일)
/// </code>
///
/// <b>재생 시 LLM 을 부르지 않는다.</b> P1~P3 범위의 런타임에는 LLM 호출 경로가 아예 없고
/// (<c>Npc.Runtime ↛ Npc.Llm</c>), 플랜은 로드된 폴백·프리베이크 플랜을 그대로 쓴다.
///
/// 이 테스트가 통과하면 버그 재현이 가능해진다 — 리스크 R5 의 해소 지점이다.
/// </summary>
[Trait("Category", "Determinism")]
public sealed class ReplayTests
{
    /// <summary>시나리오 A — 평시 하루. 600배속에서 1,440틱이다.</summary>
    private static readonly string[] s_scenarioA =
        ["--npcs", "50", "--time-scale", "600", "--days", "1", "--max-speed", "--no-dashboard"];

    [Fact]
    public async Task Determinism_ReplayMatchesByteForByte()
    {
        string dir = NewWorkspace();

        try
        {
            string trace = Path.Combine(dir, "run1", "link.jsonl");
            string run1 = Path.Combine(dir, "run1", "commands.jsonl");
            string run2 = Path.Combine(dir, "run2", "commands.jsonl");

            // ── 1. 기록 ────────────────────────────────────────────
            await using (NpcHost host = Host("--link", "record", "--trace", trace))
            {
                await host.RunAsync(CancellationToken.None);
            }

            // 기록기가 실제로 쓴 줄을 그대로 뽑는다. 역직렬화했다가 다시 쓰면
            // 왕복 손실이 섞여 들어와 무엇이 갈라진 것인지 알 수 없게 된다.
            string[] recorded = [.. CommandLinesIn(trace)];

            Assert.NotEmpty(recorded);
            await File.WriteAllTextAsync(run1, Join(recorded), Utf8);

            // ── 2. 재생 ────────────────────────────────────────────
            string[] replayed;

            await using (NpcHost host = Host("--link", "replay", "--trace", trace))
            {
                await host.RunAsync(CancellationToken.None);

                var link = Assert.IsType<ReplayGameServerLink>(host.Link);

                // 기록된 명령은 입력이 아니라 비교 대상이다 — 재생은 이벤트만 다시 흘린다.
                Assert.Equal(recorded.Length, link.RecordedCommands.Count);

                replayed = [.. link.Commands.Select(Render)];
            }

            await File.WriteAllTextAsync(run2, Join(replayed), Utf8);

            // ── 3. 바이트 비교 ─────────────────────────────────────
            byte[] a = await File.ReadAllBytesAsync(run1);
            byte[] b = await File.ReadAllBytesAsync(run2);

            Assert.True(a.AsSpan().SequenceEqual(b), Divergence(recorded, replayed));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// 기록 자체가 재현되는가. <b>재생이 일치해도 기록이 실행마다 다르면 아무 의미가 없다</b> —
    /// 같은 시나리오를 두 번 기록해 바이트를 맞춰 본다.
    /// </summary>
    [Fact]
    public async Task Determinism_TwoRecordingsAreIdentical()
    {
        string dir = NewWorkspace();

        try
        {
            string first = Path.Combine(dir, "run1", "link.jsonl");
            string second = Path.Combine(dir, "run2", "link.jsonl");

            foreach (string trace in new[] { first, second })
            {
                await using NpcHost host = Host("--link", "record", "--trace", trace);

                await host.RunAsync(CancellationToken.None);
            }

            byte[] a = await File.ReadAllBytesAsync(first);
            byte[] b = await File.ReadAllBytesAsync(second);

            Assert.True(a.AsSpan().SequenceEqual(b), FirstLineDifference(first, second));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ------------------------------------------------------------------ 헬퍼

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static string NewWorkspace()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"npc-replay-{Guid.NewGuid():N}");

        Directory.CreateDirectory(Path.Combine(dir, "run1"));
        Directory.CreateDirectory(Path.Combine(dir, "run2"));

        return dir;
    }

    private static NpcHost Host(params string[] extra)
    {
        string[] args = [.. extra, .. s_scenarioA];

        Assert.True(HostOptions.TryParse(args, out HostOptions options, out string? error), error);

        return NpcHost.Create(options with { MasterData = TestPaths.MasterData }, TextWriter.Null);
    }

    /// <summary>
    /// 기록에서 명령 줄만 뽑는다. docs/15 §3 의 <c>run1/commands.jsonl</c> 이다.
    /// <b>역직렬화하지 않는다</b> — 기록기가 쓴 바이트 그대로여야 비교가 성립한다.
    /// </summary>
    private static IEnumerable<string> CommandLinesIn(string trace)
    {
        foreach (string line in File.ReadLines(trace))
        {
            // 첫 필드가 Kind 다. Command 는 0 (RecordKind.Command).
            if (line.StartsWith("{\"Kind\":0,", StringComparison.Ordinal))
            {
                yield return line;
            }
        }
    }

    /// <summary>
    /// 기록 링크와 <b>같은 직렬화</b>로 적는다. 다른 옵션으로 적으면 내용이 같아도 바이트가 갈라진다.
    /// </summary>
    private static string Render(NpcCommand command) => JsonSerializer.Serialize(
        new LinkRecord(RecordKind.Command, command, null), LinkRecordJsonContext.Default.LinkRecord);

    private static string Join(IReadOnlyList<string> lines)
    {
        var sb = new StringBuilder(64 * 1024);

        foreach (string line in lines)
        {
            sb.Append(line).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>최초 divergence 를 틱과 함께 리포트한다 (T5-07 완료 조건).</summary>
    private static string Divergence(IReadOnlyList<string> recorded, IReadOnlyList<string> replayed)
    {
        int n = Math.Min(recorded.Count, replayed.Count);

        for (int i = 0; i < n; i++)
        {
            if (string.Equals(recorded[i], replayed[i], StringComparison.Ordinal))
            {
                continue;
            }

            return string.Create(
                CultureInfo.InvariantCulture,
                $"명령 {i}번에서 갈라진다 (최초 divergence tick {TickIn(recorded[i])} → {TickIn(replayed[i])})\n"
                + $"  기록: {recorded[i]}\n"
                + $"  재생: {replayed[i]}");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"명령 수가 다르다 — 기록 {recorded.Count} · 재생 {replayed.Count}. "
            + $"공통 {n}개는 일치한다. 이후 첫 줄: "
            + $"{(recorded.Count > n ? recorded[n] : replayed.Count > n ? replayed[n] : "(없음)")}");
    }

    /// <summary>리포트에 실을 틱. 파싱에 실패하면 -1 — 진단이 예외로 대체되면 안 된다.</summary>
    private static long TickIn(string line)
    {
        try
        {
            LinkRecord? record = JsonSerializer.Deserialize(line, LinkRecordJsonContext.Default.LinkRecord);

            return record?.Command?.IssuedAt.Value ?? -1;
        }
        catch (JsonException)
        {
            return -1;
        }
    }

    private static string FirstLineDifference(string a, string b)
    {
        string[] left = File.ReadAllLines(a);
        string[] right = File.ReadAllLines(b);

        for (int i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"{i + 1}번째 줄부터 갈라진다\n  A: {left[i]}\n  B: {right[i]}");
            }
        }

        return string.Create(CultureInfo.InvariantCulture, $"줄 수가 다르다 — {left.Length} vs {right.Length}");
    }
}
