using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.MasterData;

namespace Npc.Planning;

/// <summary>플랜 스토어의 3계층. docs/03 §7.</summary>
public enum PlanLayer
{
    /// <summary>프리베이크 산출물. 생성물이라 <c>.gitignore</c> 다 (CLAUDE.md §6).</summary>
    Plans = 0,

    /// <summary>
    /// 사람이 검수·수정한 플랜. <b>반드시 커밋한다</b> — 잃으면 검수 작업이 통째로 날아간다.
    /// 로드 시 <see cref="Plans"/> 를 덮어쓴다.
    /// </summary>
    Pinned = 1,

    /// <summary>검증 실패 산출물. 품질 분석용이고 로드하지 않는다.</summary>
    Rejected = 2,
}

/// <summary>로드 결과. 기동 로그가 이 숫자를 찍는다.</summary>
/// <param name="Loaded"><c>plans/</c> 에서 올린 수.</param>
/// <param name="Pinned"><c>pinned/</c> 에서 올린 수 (plans 를 덮어쓴 것 포함).</param>
/// <param name="Skipped">버킷 이름을 못 읽어 건너뛴 파일 수.</param>
/// <param name="Failed">읽었지만 컴파일·검증에서 걸린 수. 그 버킷은 폴백으로 해소된다.</param>
/// <param name="Errors">실패 사유 (파일명 오름차순). 조용히 넘기지 않는다.</param>
public readonly record struct PlanStoreLoadReport(
    int Loaded,
    int Pinned,
    int Skipped,
    int Failed,
    ImmutableArray<string> Errors)
{
    /// <summary>스토어에 올라간 총 버킷 수.</summary>
    public int Total => Loaded + Pinned;
}

/// <summary>
/// 플랜 스토어 파일 입출력. docs/03 §7.
///
/// <code>
/// planstore/
///   manifest.json
///   plans/    blacksmith@Dawn.Peace.Fair.json     ← 프리베이크 산출물 (2,880개)
///   pinned/   blacksmith@Evening.War.Cold.json    ← 사람이 수정한 것. prebake 가 덮어쓰지 않는다
///   rejected/ &lt;bucket&gt;.&lt;attempt&gt;.json          ← 검증 실패분 + 실패 코드
/// </code>
///
/// <b>로드는 <c>plans/</c> 먼저, <c>pinned/</c> 나중이다.</b> 순서가 곧 우선순위다 —
/// 사람이 고친 것이 항상 이긴다 (docs/13 §5).
///
/// 파일에는 <see cref="PlanDocument"/> 를 그대로 담는다. 검수자가 읽는 것이 그 JSON 이고,
/// 컴파일 왕복으로는 <c>route</c> 경유지 같은 것을 잃는다 (<c>BucketNeighbors.Revalidate</c> 와 같은 이유).
/// </summary>
public static class PlanStoreIo
{
    /// <summary>파일 포맷 버전.</summary>
    public const int CurrentSchema = 1;

    /// <summary>계층별 폴더 이름.</summary>
    public static string FolderOf(PlanLayer layer) => layer switch
    {
        PlanLayer.Plans => "plans",
        PlanLayer.Pinned => "pinned",
        PlanLayer.Rejected => "rejected",
        _ => throw new ArgumentOutOfRangeException(nameof(layer)),
    };

    /// <summary>이 버킷의 파일 경로. 파일명은 <c>{bucket}.json</c> 이다 (docs/03 §7).</summary>
    public static string PathOf(string planStoreDirectory, PlanLayer layer, BucketKey bucket, MasterDataSet data)
    {
        ArgumentException.ThrowIfNullOrEmpty(planStoreDirectory);
        ArgumentNullException.ThrowIfNull(data);

        return Path.Combine(
            planStoreDirectory,
            FolderOf(layer),
            bucket.Format(data.Archetypes[bucket.A].Id) + ".json");
    }

    /// <summary>
    /// 버킷 이름을 파싱한다. <c>blacksmith@Dawn.Peace.Fair</c> 형식이다.
    /// 아키타입이 없거나 열거값이 틀리면 거짓 — 마스터데이터가 바뀐 뒤의 낡은 파일이 그렇다.
    /// </summary>
    public static bool TryParseBucket(string text, MasterDataSet data, out BucketKey bucket)
    {
        ArgumentNullException.ThrowIfNull(data);

        bucket = default;

        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        int at = text.IndexOf('@', StringComparison.Ordinal);

        if (at <= 0 || at == text.Length - 1)
        {
            return false;
        }

        if (!data.Archetypes.TryGet(text[..at], out ArchetypeDef archetype))
        {
            return false;
        }

        string[] parts = text[(at + 1)..].Split('.');

        if (parts.Length != 3
            || !Enum.TryParse(parts[0], out TimeOfDay time)
            || !Enum.TryParse(parts[1], out RegionState region)
            || !Enum.TryParse(parts[2], out Climate climate))
        {
            return false;
        }

        // Enum.TryParse 는 "7" 같은 숫자 문자열도 통과시킨다. 범위를 직접 본다.
        if (!Enum.IsDefined(time) || !Enum.IsDefined(region) || !Enum.IsDefined(climate))
        {
            return false;
        }

        bucket = new BucketKey(archetype.Code, time, region, climate);
        return true;
    }

    /// <summary>플랜 하나를 쓴다. 폴더가 없으면 만든다.</summary>
    public static void SavePlan(
        string planStoreDirectory,
        PlanLayer layer,
        BucketKey bucket,
        CompiledPlan plan,
        MasterDataSet data)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(data);

        string path = PathOf(planStoreDirectory, layer, bucket, data);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Serialize(bucket, plan, data), Encoding.UTF8);
    }

    /// <summary>
    /// 스토어의 버킷 플랜을 전부 쓴다. <b>pinned 버킷은 건너뛴다</b> —
    /// <c>pinned/</c> 는 사람이 관리하는 계층이고 버전 관리에 올라간다.
    /// </summary>
    /// <returns>쓴 파일 수.</returns>
    public static int SaveAll(string planStoreDirectory, PlanStore store, MasterDataSet data)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(data);

        int written = 0;

        for (int index = 0; index < BucketKey.TotalKeys; index++)
        {
            var bucket = BucketKey.FromIndex(index);

            // Resolve 가 아니라 PeekBucket 이다 — 저장이 히트율 카운터에 실리면 측정이 오염된다.
            if (store.IsPinned(bucket) || store.PeekBucket(bucket) is not { } plan)
            {
                continue;
            }

            SavePlan(planStoreDirectory, PlanLayer.Plans, bucket, plan, data);
            written++;
        }

        return written;
    }

    /// <summary>
    /// <c>plans/</c> → <c>pinned/</c> 순으로 스토어에 올린다. pinned 가 이긴다.
    /// 폴더가 없으면 아무것도 하지 않는다 — 첫 기동에는 스토어가 없다.
    /// </summary>
    public static PlanStoreLoadReport LoadAll(string planStoreDirectory, PlanStore store, MasterDataSet data)
    {
        ArgumentException.ThrowIfNullOrEmpty(planStoreDirectory);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(data);

        var errors = ImmutableArray.CreateBuilder<string>();
        int loaded = 0;
        int pinned = 0;
        int skipped = 0;
        int failed = 0;

        // plans 먼저, pinned 나중. 순서가 곧 우선순위다.
        foreach (PlanLayer layer in new[] { PlanLayer.Plans, PlanLayer.Pinned })
        {
            string folder = Path.Combine(planStoreDirectory, FolderOf(layer));

            if (!Directory.Exists(folder))
            {
                continue;
            }

            // 파일 순서를 고정한다 — 로드 순서가 흔들리면 PlanId 배정이 회차마다 달라진다.
            string[] files = Directory.GetFiles(folder, "*.json");
            Array.Sort(files, StringComparer.Ordinal);

            foreach (string file in files)
            {
                string name = Path.GetFileNameWithoutExtension(file);

                if (!TryParseBucket(name, data, out BucketKey bucket))
                {
                    skipped++;
                    errors.Add($"{name}: 버킷 이름을 읽을 수 없다 (마스터데이터가 바뀐 뒤의 낡은 파일인가).");
                    continue;
                }

                if (!TryDeserialize(File.ReadAllText(file), bucket, data, out CompiledPlan? plan, out string error))
                {
                    failed++;
                    errors.Add($"{name}: {error}");
                    continue;
                }

                if (layer == PlanLayer.Pinned)
                {
                    // 이미 plans 에서 올라온 것을 세었다면 그 몫을 pinned 로 옮긴다.
                    if (store.HasBucket(bucket) && !store.IsPinned(bucket))
                    {
                        loaded--;
                    }

                    store.Pin(bucket, plan!);
                    pinned++;
                }
                else
                {
                    store.SetBucket(bucket, plan!);
                    loaded++;
                }
            }
        }

        errors.Sort(StringComparer.Ordinal);

        return new PlanStoreLoadReport(loaded, pinned, skipped, failed, errors.ToImmutable());
    }

    /// <summary>플랜 하나를 파일 내용으로. 사람이 읽는 파일이라 들여쓴다.</summary>
    public static string Serialize(BucketKey bucket, CompiledPlan plan, MasterDataSet data)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(data);

        // 원본 JSON 이 있으면 그것을 그대로 담는다. 컴파일 왕복은 route 경유지 같은 것을 잃는다.
        string document = string.IsNullOrEmpty(plan.SourceJson)
            ? JsonSerializer.Serialize(
                Npc.Core.Plan.PlanCompiler.ToDocument(plan, data), PlanJsonContext.Default.PlanDocument)
            : plan.SourceJson;

        var buffer = new MemoryStream(4 * 1024);

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();

            writer.WriteNumber("schema", CurrentSchema);
            writer.WriteString("bucket", bucket.Format(data.Archetypes[bucket.A].Id));
            writer.WriteString("archetype", data.Archetypes[bucket.A].Id);
            writer.WriteNumber("version", plan.Version);
            writer.WriteString("origin", plan.Origin.ToString());

            writer.WritePropertyName("plan");
            writer.WriteRawValue(document, skipInputValidation: false);

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>파일 내용을 플랜으로. 실패하면 이유를 준다 — 조용히 버리지 않는다.</summary>
    public static bool TryDeserialize(
        string json,
        BucketKey bucket,
        MasterDataSet data,
        out CompiledPlan? plan,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(data);

        plan = null;
        error = string.Empty;

        try
        {
            using JsonDocument file = JsonDocument.Parse(json);
            JsonElement root = file.RootElement;

            if (!root.TryGetProperty("plan", out JsonElement planElement))
            {
                error = "'plan' 필드가 없다.";
                return false;
            }

            string document = planElement.GetRawText();

            PlanDocument? parsed = JsonSerializer.Deserialize(document, PlanJsonContext.Default.PlanDocument);

            if (parsed is null)
            {
                error = "'plan' 을 PlanDocument 로 읽지 못했다.";
                return false;
            }

            int version = root.TryGetProperty("version", out JsonElement v) && v.TryGetInt32(out int parsedVersion)
                ? parsedVersion
                : 1;

            PlanOrigin origin = root.TryGetProperty("origin", out JsonElement o)
                && o.ValueKind == JsonValueKind.String
                && Enum.TryParse(o.GetString(), out PlanOrigin parsedOrigin)
                && Enum.IsDefined(parsedOrigin)
                    ? parsedOrigin
                    : PlanOrigin.Prebaked;

            plan = Npc.Core.Plan.PlanCompiler.Compile(
                parsed,
                bucket,
                new PlanId(PlanStore.IdlePlanId),
                data,
                origin,
                version,
                document);

            return true;
        }
        catch (JsonException ex)
        {
            error = "JSON 이 깨졌다: " + Flatten(ex.Message);
            return false;
        }
        catch (PlanCompilationException ex)
        {
            error = string.Create(
                CultureInfo.InvariantCulture,
                $"컴파일 실패 (스텝 {ex.StepIndex}): {Flatten(ex.Message)}");
            return false;
        }
    }

    private static string Flatten(string message)
    {
        string flat = message.Replace('\r', ' ').Replace('\n', ' ');

        return flat.Length > 200 ? flat[..200] : flat;
    }
}
