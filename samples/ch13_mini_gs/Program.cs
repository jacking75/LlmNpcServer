// 13장 — 최소 게임서버.
//
// NPC 서버가 --link tcp 로 붙을 수 있는 <b>가장 작은 상대방</b>이다.
// 하는 일 넷:
//   ① TCP 7010 을 열고 NPC 서버의 접속을 받는다
//   ② Hello 를 보내고 HelloAck 를 받는다 (핸드셰이크)
//   ③ CommandBatch 를 읽어 MoveTo 를 N틱 뒤 NpcArrived 로, 나머지를 즉시 완료로 답한다
//   ④ 10Hz 로 TickSync 를 밀고, 정해진 틱에 시간대·기후를 바꾼다
//
// 게임서버가 NPC 서버에게 <b>먼저</b> Hello 를 보낸다는 것에 주의한다.
// 연결은 NPC 서버가 걸지만, 세계의 주인은 게임서버이므로 규격을 먼저 제시하는 쪽도 게임서버다.
//
// 사용법
//   dotnet run --project samples/ch13_mini_gs -- --npcs 300 --time-scale 60
//   dotnet run -c Release --project src/Npc.Host -- --link tcp --gs-port 7010 --npcs 300 --time-scale 60 --days 0

using System.Buffers;
using System.Collections.Immutable;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using MemoryPack;
using Npc.MasterData;
using Npc.Wire;

// ── 옵션 ─────────────────────────────────────────────────────────────────
int port = 7010;
int npcs = 300;
int timeScale = 60;
int travelTicks = 20;          // MoveTo 를 몇 틱 뒤에 도착으로 칠 것인가
int gapAtTick = 0;             // 이 틱에 시퀀스를 하나 건너뛴다 (N6 실험). 0 이면 안 한다
string masterData = "./masterdata";

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port" when i + 1 < args.Length: port = int.Parse(args[++i]); break;
        case "--npcs" when i + 1 < args.Length: npcs = int.Parse(args[++i]); break;
        case "--time-scale" when i + 1 < args.Length: timeScale = int.Parse(args[++i]); break;
        case "--travel-ticks" when i + 1 < args.Length: travelTicks = int.Parse(args[++i]); break;
        case "--gap-at" when i + 1 < args.Length: gapAtTick = int.Parse(args[++i]); break;
        case "--masterdata" when i + 1 < args.Length: masterData = args[++i]; break;
        default:
            Console.Error.WriteLine($"모르는 인자다: {args[i]}");
            return 2;
    }
}

// ── 핸드셰이크에 실을 두 해시 ────────────────────────────────────────────
//
// NPC 서버와 <b>같은 함수</b>로 계산해야 한다. 그래서 Npc.MasterData 를 참조한다.
// 이 둘 중 하나라도 다르면 NPC 서버가 연결을 거절한다 — 우회 옵션은 없다.
string mdDir = ResolveMasterData(masterData);
MasterDataSet data = MasterDataLoader.Load(mdDir);
NpcInstanceTable instances = NpcInstanceTable.Load(Path.Combine(mdDir, "npc_instances.json"), data);
NpcRoster roster = NpcRoster.Select(instances, npcs);

Console.WriteLine($"mini game server  ·  npcs {roster.Count}  ·  time-scale {timeScale}");
Console.WriteLine($"masterdata  {data.ContentHash[..12]}   ({mdDir})");
Console.WriteLine($"roster      {roster.Hash[..12]}");
Console.WriteLine($"listening   :{port}");
Console.WriteLine();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var listener = new TcpListener(IPAddress.Loopback, port);
listener.Start();

try
{
    while (!cts.IsCancellationRequested)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(cts.Token);
        client.NoDelay = true;
        Console.WriteLine($"[link] 접속: {client.Client.RemoteEndPoint}");

        try
        {
            await ServeAsync(client, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[link] 끊김: {ex.GetType().Name} {ex.Message}");
        }

        Console.WriteLine("[link] 연결 종료. 다음 접속을 기다린다.");
    }
}
catch (OperationCanceledException) { }
finally
{
    listener.Stop();
}

return 0;

// ── 한 연결 처리 ─────────────────────────────────────────────────────────
async Task ServeAsync(TcpClient client, CancellationToken ct)
{
    NetworkStream stream = client.GetStream();

    // ① Hello — 게임서버가 먼저 규격을 제시한다.
    var hello = new WireHello
    {
        ProtocolVersion = FrameCodec.Version,
        TickRate = 10,
        TimeScale = timeScale,
        NpcCount = roster.Count,
        StartTick = 0,
        MasterData = WireHash.FromHex(data.ContentHash),
        Roster = WireHash.FromHex(roster.Hash),
    };

    await WriteFrameAsync(stream, LinkMessageKind.Hello, MemoryPackSerializer.Serialize(hello), ct);

    // ② HelloAck — 받아서 수락인지 본다.
    PipeReader reader = PipeReader.Create(stream);
    (LinkMessageKind kind, byte[] payload) = await ReadFrameAsync(reader, ct);

    if (kind != LinkMessageKind.HelloAck)
    {
        Console.WriteLine($"[handshake] HelloAck 가 아니라 {kind} 가 왔다.");
        return;
    }

    WireHelloAck ack = MemoryPackSerializer.Deserialize<WireHelloAck>(payload);

    if (ack.Accepted == 0)
    {
        Console.WriteLine($"[handshake] 거절됐다: {(LinkRejectCode)ack.RejectCode}");
        Console.WriteLine($"           내 masterdata {data.ContentHash[..12]} / 상대 {ack.MasterData.ToHex()[..12]}");
        Console.WriteLine($"           내 roster     {roster.Hash[..12]} / 상대 {ack.Roster.ToHex()[..12]}");
        Console.WriteLine($"           내 time-scale {timeScale} / 상대 {ack.TimeScale}");
        return;
    }

    Console.WriteLine($"[handshake] 수락. npc {ack.NpcCount} · time-scale {ack.TimeScale}");
    Console.WriteLine();

    // ③ 틱 루프와 수신 루프를 나란히 돌린다.
    var pending = new SortedDictionary<long, List<WireEvent>>();   // 도착 예정 (틱 -> 이벤트)
    long sequence = 0;
    long tick = 0;
    long commands = 0;
    var gate = new SemaphoreSlim(1, 1);

    var send = Task.Run(async () =>
    {
        var beat = new PeriodicTimer(TimeSpan.FromMilliseconds(100));

        while (await beat.WaitForNextTickAsync(ct))
        {
            tick++;
            var batch = new List<WireEvent>(8);

            await gate.WaitAsync(ct);
            try
            {
                // 도착 예정 이벤트를 꺼낸다.
                foreach (long due in pending.Keys.Where(k => k <= tick).ToArray())
                {
                    batch.AddRange(pending[due]);
                    pending.Remove(due);
                }
            }
            finally { gate.Release(); }

            // TickSync 는 매 틱 하나. 이게 없으면 NPC 서버의 게임 시계가 안 간다.
            batch.Add(new WireEvent
            {
                Kind = (byte)Npc.Contracts.GameEventKind.TickSync,
                OccurredAt = tick,
            });

            // N6 — 시퀀스는 순증이어야 한다. 여기서 한 번 건너뛰면 NPC 서버가 갭으로 읽는다.
            for (int i = 0; i < batch.Count; i++)
            {
                sequence++;
                if (gapAtTick > 0 && tick == gapAtTick && i == 0)
                {
                    sequence++;   // 일부러 하나 건너뛴다
                    Console.WriteLine($"[N6] tick {tick}: 시퀀스를 하나 건너뛴다 → 갭 검출을 유도한다");
                }

                WireEvent e = batch[i];
                e.Sequence = sequence;
                batch[i] = e;
            }

            await WriteFrameAsync(stream, LinkMessageKind.EventBatch,
                MemoryPackSerializer.Serialize(batch.ToArray()), ct);

            if (tick % 100 == 0)
            {
                Console.WriteLine($"tick {tick,6} | cmd {commands,8} | ev {sequence,8} | 대기 {pending.Count}");
            }
        }
    }, ct);

    // ④ 수신 — CommandBatch 를 읽어 응답을 예약한다.
    while (!ct.IsCancellationRequested)
    {
        (LinkMessageKind k, byte[] p) = await ReadFrameAsync(reader, ct);

        if (k == LinkMessageKind.Bye)
        {
            Console.WriteLine($"[link] Bye: {(LinkByeCode)MemoryPackSerializer.Deserialize<WireBye>(p).Code}");
            break;
        }

        if (k == LinkMessageKind.Heartbeat) { continue; }
        if (k != LinkMessageKind.CommandBatch) { continue; }

        WireCommand[] batch = MemoryPackSerializer.Deserialize<WireCommand[]>(p) ?? [];
        commands += batch.Length;

        await gate.WaitAsync(ct);
        try
        {
            foreach (WireCommand c in batch)
            {
                // MoveTo 만 시간이 걸린다. 나머지는 다음 틱에 완료로 답한다.
                bool isMove = c.Kind == (byte)Npc.Contracts.NpcCommandKind.MoveTo;
                long due = tick + (isMove ? travelTicks : 1);

                var reply = new WireEvent
                {
                    Kind = (byte)(isMove
                        ? Npc.Contracts.GameEventKind.NpcArrived
                        : Npc.Contracts.GameEventKind.NpcActionCompleted),
                    OccurredAt = due,
                    Npc = c.Npc,
                    Correlation = c.Correlation,
                    Poi = c.TargetPoi,
                };

                if (!pending.TryGetValue(due, out List<WireEvent>? list))
                {
                    list = new List<WireEvent>(4);
                    pending[due] = list;
                }

                list.Add(reply);
            }
        }
        finally { gate.Release(); }
    }

    await send.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None).ContinueWith(_ => { }, CancellationToken.None);
}

// ── 프레임 입출력 ────────────────────────────────────────────────────────
static async Task WriteFrameAsync(Stream stream, LinkMessageKind kind, byte[] payload, CancellationToken ct)
{
    var buffer = new ArrayBufferWriter<byte>(FrameCodec.HeaderSize + payload.Length);

    FrameCodec.WriteHeader(buffer, kind, payload.Length);
    buffer.Write(payload);

    await stream.WriteAsync(buffer.WrittenMemory, ct);
    await stream.FlushAsync(ct);
}

static async Task<(LinkMessageKind Kind, byte[] Payload)> ReadFrameAsync(PipeReader reader, CancellationToken ct)
{
    while (true)
    {
        ReadResult result = await reader.ReadAsync(ct);
        ReadOnlySequence<byte> buffer = result.Buffer;

        if (FrameCodec.TryReadFrame(ref buffer, out LinkMessageKind kind, out ReadOnlySequence<byte> payload))
        {
            byte[] bytes = payload.ToArray();
            reader.AdvanceTo(buffer.Start, buffer.End);
            return (kind, bytes);
        }

        reader.AdvanceTo(buffer.Start, buffer.End);

        if (result.IsCompleted)
        {
            throw new IOException("상대가 연결을 닫았다.");
        }
    }
}

// ── masterdata 경로 ──────────────────────────────────────────────────────
static string ResolveMasterData(string path)
{
    if (Directory.Exists(path)) { return Path.GetFullPath(path); }

    foreach (string from in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        for (var dir = new DirectoryInfo(from); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "masterdata");

            if (File.Exists(Path.Combine(candidate, "archetypes.json")))
            {
                return candidate;
            }
        }
    }

    throw new DirectoryNotFoundException($"마스터데이터 폴더를 찾지 못했다: {path}");
}
