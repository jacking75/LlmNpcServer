using System.Buffers.Binary;
using Npc.Contracts;
using Npc.Core;
using Npc.Runtime;

namespace Npc.Host.Persistence;

/// <summary>스냅샷 파일 머리 (A-01). 복원 조건 판정이 이 값들로 이뤄진다.</summary>
/// <param name="FormatVersion">형식 버전. 다르면 복원하지 않는다.</param>
/// <param name="Tick">스냅샷을 뜬 게임 틱.</param>
/// <param name="SyncedTick">게임서버가 알려준 마지막 틱.</param>
/// <param name="NpcCount">NPC 수.</param>
/// <param name="InventoryStride">인벤토리 칸 수.</param>
/// <param name="ZoneCapacity">존 상태 표 크기.</param>
/// <param name="NextCorrelation">스냅샷 시점의 다음 상관 ID.</param>
/// <param name="MasterDataHash">마스터데이터 content_hash (hex).</param>
/// <param name="RosterHash">로스터 해시 (hex).</param>
/// <param name="PrefixHash">프롬프트 프리픽스 SHA-256 (hex). 개별 플랜의 유효성 기준이다.</param>
public readonly record struct SnapshotHeader(
    ushort FormatVersion,
    long Tick,
    long SyncedTick,
    int NpcCount,
    int InventoryStride,
    int ZoneCapacity,
    uint NextCorrelation,
    string MasterDataHash,
    string RosterHash,
    string PrefixHash);

/// <summary>개별 플랜 한 건. 버킷 플랜은 스토어 첨자로 복원되므로 여기 담지 않는다.</summary>
/// <param name="Npc">주인 NPC 첨자.</param>
/// <param name="Bucket">어느 버킷으로 만들어졌나.</param>
/// <param name="SourceJson">원본 JSON. 복원 시 다시 컴파일한다.</param>
public readonly record struct IndividualPlanRecord(int Npc, int Bucket, string SourceJson);

/// <summary>
/// 스냅샷 파일 형식 (A-01).
///
/// <b><c>Npc.Wire</c>(MemoryPack)를 쓰지 않는다.</b> <c>Npc.Runtime</c> 은 와이어를 참조할 수 없고
/// (CLAUDE.md §3), 반대로 이 형식은 링크와 아무 관계가 없다. 자체 이진 형식이 정직하다.
///
/// 배치: 매직 <c>NPCS</c> · 형식 버전 · 머리(고정) · 해시 셋(길이 접두 UTF-8) ·
/// 섹션(SoA 원시 블롭) · 개별 플랜 · 꼬리 CRC32.
///
/// <b>파일에 벽시계를 넣지 않는다</b> (CLAUDE.md §2.3). 파일명의 숫자도 게임 틱이다.
/// </summary>
public static class SnapshotFile
{
    /// <summary>매직. 앞 4바이트.</summary>
    public static ReadOnlySpan<byte> Magic => "NPCS"u8;

    /// <summary>
    /// 형식 버전. 배치가 바뀌면 올린다. 다른 버전은 복원하지 않는다.
    ///
    /// <b>5 — <c>HostileFaction</c>·<c>PatrolCursor</c>(D-04)·<c>RecentPlayer</c>(D-03) 배열이 들어갔다.</b>
    /// <b>4 — <c>HostilePlayer</c> 배열이 들어갔다</b> (B-06).
    /// <b>3 — <c>Occupant</c>(슬롯 거주자) 배열이 들어갔다</b> (B-05).
    /// <b>2 — <c>Instance</c> 배열이 <c>CurrentPoi</c> 뒤에 들어갔다</b> (B-02).
    /// 버전 1 스냅샷은 거절된다. 되돌릴 상태가 아니라 <b>다시 만드는 것</b>이 정답이다 —
    /// 스냅샷은 60초마다 새로 쓰인다.
    /// </summary>
    public const ushort FormatVersion = 5;

    /// <summary>파일 이름 접두. 뒤에 게임 틱이 붙는다.</summary>
    public const string NamePrefix = "snapshot-";

    /// <summary>파일 확장자.</summary>
    public const string Extension = ".bin";

    /// <summary>이 틱의 스냅샷 파일 이름.</summary>
    public static string NameOf(long tick) =>
        $"{NamePrefix}{tick:D12}{Extension}";

    /// <summary>
    /// 그림자 버퍼를 파일로 쓴다. <b>쓰기 스레드에서 부른다</b> — 틱 루프가 아니다.
    ///
    /// <c>.tmp</c> 에 다 쓴 뒤 원자 교체한다. 도중에 죽어도 반쯤 쓰인 파일이 최신으로 남지 않는다.
    /// </summary>
    /// <returns>쓴 바이트 수.</returns>
    public static long Write(
        string path,
        ShadowBuffer buffer,
        SnapshotHeader header,
        IReadOnlyList<IndividualPlanRecord> individuals)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(individuals);

        string temp = path + ".tmp";
        long bytes;

        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var crc = new Crc32Stream(stream))
        using (var writer = new BinaryWriter(crc, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(header.FormatVersion);
            writer.Write(header.Tick);
            writer.Write(header.SyncedTick);
            writer.Write(header.NpcCount);
            writer.Write(header.InventoryStride);
            writer.Write(header.ZoneCapacity);
            writer.Write(header.NextCorrelation);
            writer.Write(header.MasterDataHash);
            writer.Write(header.RosterHash);
            writer.Write(header.PrefixHash);

            int n = header.NpcCount;

            WriteU64(writer, buffer.Flags, n);
            WriteI32(writer, buffer.PlanId, n);
            writer.Write(buffer.StepIndex, 0, n);
            writer.Write(buffer.StepStatus, 0, n);
            WriteI64(writer, buffer.StepIssuedTick, n);
            writer.Write(buffer.Lod, 0, n);

            for (int i = 0; i < n; i++)
            {
                writer.Write(buffer.Pos[i].X);
                writer.Write(buffer.Pos[i].Y);
                writer.Write(buffer.Pos[i].Z);
            }

            for (int i = 0; i < n; i++)
            {
                writer.Write(buffer.Hp[i]);
            }

            for (int i = 0; i < n; i++)
            {
                writer.Write(buffer.Stamina[i]);
            }

            WriteU16(writer, buffer.ZoneCode, n);
            WriteU16(writer, buffer.ArchetypeCode, n);
            WriteU16(writer, buffer.CurrentPoi, n);
            WriteI32(writer, buffer.HostilePlayer, n);
            WriteU16(writer, buffer.HostileFaction, n);
            writer.Write(buffer.PatrolCursor, 0, n);
            WriteI32(writer, buffer.RecentPlayer, n);
            WriteI32(writer, buffer.Occupant, n);
            WriteU16(writer, buffer.Instance, n);
            WriteU16(writer, buffer.HomePoi, n);
            WriteU16(writer, buffer.WorkPoi, n);
            WriteI32(writer, buffer.Inventory, n * header.InventoryStride);
            WriteI64(writer, buffer.PlanAssignedTick, n);
            writer.Write(buffer.PendingUrgency, 0, n);
            WriteI64(writer, buffer.LastEventSequence, n);
            writer.Write(buffer.LastFailReason, 0, n);
            writer.Write(buffer.StepRetries, 0, n);

            // Recent 는 링 버퍼라 원시 복사가 안 된다. 담긴 것만 길이 접두로 쓴다.
            for (int i = 0; i < n; i++)
            {
                ReadOnlySpan<RecentEvent> items = buffer.Recent[i].AsSpan();

                writer.Write((byte)items.Length);

                for (int k = 0; k < items.Length; k++)
                {
                    writer.Write((byte)items[k].Kind);
                    writer.Write(items[k].At.Value);
                    writer.Write(items[k].Subject);
                    writer.Write(items[k].Salience);
                }
            }

            writer.Write(buffer.ZoneRegion, 0, header.ZoneCapacity);
            writer.Write(buffer.ZoneClimate, 0, header.ZoneCapacity);

            writer.Write(individuals.Count);

            foreach (IndividualPlanRecord plan in individuals)
            {
                writer.Write(plan.Npc);
                writer.Write(plan.Bucket);
                writer.Write(plan.SourceJson);
            }

            writer.Flush();

            uint checksum = crc.Value;

            // CRC 는 계산에서 뺀 상태로 마지막에 붙인다.
            Span<byte> tail = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(tail, checksum);
            stream.Write(tail);
            stream.Flush(flushToDisk: true);

            bytes = stream.Length;
        }

        File.Move(temp, path, overwrite: true);
        return bytes;
    }

    /// <summary>
    /// 파일을 읽는다. CRC 가 어긋나면 <paramref name="error"/> 를 채우고 false —
    /// <b>던지지 않는다</b>. 손상된 스냅샷은 예외 상황이 아니라 예상된 사건이고,
    /// 호출부는 이전 스냅샷으로 물러난다.
    /// </summary>
    public static bool TryRead(
        string path,
        int zoneCapacity,
        out SnapshotHeader header,
        out ShadowBuffer? buffer,
        out IReadOnlyList<IndividualPlanRecord> individuals,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(path);

        header = default;
        buffer = null;
        individuals = [];
        error = null;

        byte[] bytes;

        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException e)
        {
            error = $"스냅샷을 읽지 못했다 ({path}): {e.Message}";
            return false;
        }

        if (bytes.Length < Magic.Length + 4)
        {
            error = $"스냅샷이 너무 짧다: {path}";
            return false;
        }

        if (!bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            error = $"스냅샷 매직이 다르다: {path}";
            return false;
        }

        uint stored = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4));
        uint actual = Crc32.Compute(bytes.AsSpan(0, bytes.Length - 4));

        if (stored != actual)
        {
            error = $"스냅샷 CRC 불일치 ({path}): 저장 {stored:X8} 실제 {actual:X8}";
            return false;
        }

        try
        {
            using var stream = new MemoryStream(bytes, 0, bytes.Length - 4, writable: false);
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8);

            stream.Position = Magic.Length;

            ushort format = reader.ReadUInt16();

            if (format != FormatVersion)
            {
                error = $"스냅샷 형식 버전이 다르다 ({path}): 파일 {format} vs 기대 {FormatVersion}";
                return false;
            }

            long tick = reader.ReadInt64();
            long syncedTick = reader.ReadInt64();
            int n = reader.ReadInt32();
            int stride = reader.ReadInt32();
            int zones = reader.ReadInt32();
            uint nextCorrelation = reader.ReadUInt32();

            header = new SnapshotHeader(
                format, tick, syncedTick, n, stride, zones, nextCorrelation,
                reader.ReadString(), reader.ReadString(), reader.ReadString());

            var shadow = new ShadowBuffer(n, Math.Max(stride, 1), Math.Max(zoneCapacity, Math.Max(zones, 1)));

            ReadU64(reader, shadow.Flags, n);
            ReadI32(reader, shadow.PlanId, n);
            reader.Read(shadow.StepIndex, 0, n);
            reader.Read(shadow.StepStatus, 0, n);
            ReadI64(reader, shadow.StepIssuedTick, n);
            reader.Read(shadow.Lod, 0, n);

            for (int i = 0; i < n; i++)
            {
                shadow.Pos[i] = new WorldPos(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            }

            for (int i = 0; i < n; i++)
            {
                shadow.Hp[i] = reader.ReadInt16();
            }

            for (int i = 0; i < n; i++)
            {
                shadow.Stamina[i] = reader.ReadInt16();
            }

            ReadU16(reader, shadow.ZoneCode, n);
            ReadU16(reader, shadow.ArchetypeCode, n);
            ReadU16(reader, shadow.CurrentPoi, n);
            ReadI32(reader, shadow.HostilePlayer, n);
            ReadU16(reader, shadow.HostileFaction, n);
            reader.Read(shadow.PatrolCursor, 0, n);
            ReadI32(reader, shadow.RecentPlayer, n);
            ReadI32(reader, shadow.Occupant, n);
            ReadU16(reader, shadow.Instance, n);
            ReadU16(reader, shadow.HomePoi, n);
            ReadU16(reader, shadow.WorkPoi, n);
            ReadI32(reader, shadow.Inventory, n * stride);
            ReadI64(reader, shadow.PlanAssignedTick, n);
            reader.Read(shadow.PendingUrgency, 0, n);
            ReadI64(reader, shadow.LastEventSequence, n);
            reader.Read(shadow.LastFailReason, 0, n);
            reader.Read(shadow.StepRetries, 0, n);

            for (int i = 0; i < n; i++)
            {
                int count = reader.ReadByte();
                var ring = default(RingBuffer8<RecentEvent>);

                for (int k = 0; k < count; k++)
                {
                    var item = new RecentEvent(
                        (GameEventKind)reader.ReadByte(),
                        new Tick(reader.ReadInt64()),
                        reader.ReadInt32(),
                        reader.ReadByte());

                    ring.Add(in item);
                }

                shadow.Recent[i] = ring;
            }

            reader.Read(shadow.ZoneRegion, 0, Math.Min(zones, shadow.ZoneRegion.Length));
            reader.Read(shadow.ZoneClimate, 0, Math.Min(zones, shadow.ZoneClimate.Length));

            int planCount = reader.ReadInt32();
            var plans = new List<IndividualPlanRecord>(planCount);

            for (int i = 0; i < planCount; i++)
            {
                plans.Add(new IndividualPlanRecord(
                    reader.ReadInt32(), reader.ReadInt32(), reader.ReadString()));
            }

            shadow.Tick = tick;
            shadow.SyncedTick = syncedTick;
            shadow.NextCorrelation = nextCorrelation;

            buffer = shadow;
            individuals = plans;
            return true;
        }
        catch (EndOfStreamException e)
        {
            error = $"스냅샷이 잘렸다 ({path}): {e.Message}";
            return false;
        }
    }

    /// <summary>디렉터리의 스냅샷을 최신 순으로. 파일명의 틱이 기준이다.</summary>
    public static IReadOnlyList<string> ListNewestFirst(string dir)
    {
        ArgumentNullException.ThrowIfNull(dir);

        if (!Directory.Exists(dir))
        {
            return [];
        }

        return
        [
            .. Directory.EnumerateFiles(dir, NamePrefix + "*" + Extension)
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal),
        ];
    }

    private static void WriteU64(BinaryWriter w, WorldFlags[] a, int n)
    {
        for (int i = 0; i < n; i++)
        {
            w.Write((ulong)a[i]);
        }
    }

    private static void WriteI32(BinaryWriter w, int[] a, int n)
    {
        for (int i = 0; i < n; i++)
        {
            w.Write(a[i]);
        }
    }

    private static void WriteI64(BinaryWriter w, long[] a, int n)
    {
        for (int i = 0; i < n; i++)
        {
            w.Write(a[i]);
        }
    }

    private static void WriteU16(BinaryWriter w, ushort[] a, int n)
    {
        for (int i = 0; i < n; i++)
        {
            w.Write(a[i]);
        }
    }

    private static void ReadU64(BinaryReader r, WorldFlags[] a, int n)
    {
        for (int i = 0; i < n; i++)
        {
            a[i] = (WorldFlags)r.ReadUInt64();
        }
    }

    private static void ReadI32(BinaryReader r, int[] a, int n)
    {
        for (int i = 0; i < n; i++)
        {
            a[i] = r.ReadInt32();
        }
    }

    private static void ReadI64(BinaryReader r, long[] a, int n)
    {
        for (int i = 0; i < n; i++)
        {
            a[i] = r.ReadInt64();
        }
    }

    private static void ReadU16(BinaryReader r, ushort[] a, int n)
    {
        for (int i = 0; i < n; i++)
        {
            a[i] = r.ReadUInt16();
        }
    }
}
