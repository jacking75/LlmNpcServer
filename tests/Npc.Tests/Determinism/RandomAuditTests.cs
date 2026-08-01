using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using Npc.Core;
using Npc.Planning;
using Npc.Runtime;

namespace Npc.Tests.Determinism;

/// <summary>어떤 어셈블리의 어디에서 무엇을 부르는가.</summary>
/// <param name="Assembly">어셈블리 단순 이름.</param>
/// <param name="Location"><c>Namespace.Type.Method</c>. 메타데이터에만 있고 IL 에서 못 찾으면 <c>(참조만)</c>.</param>
/// <param name="Target"><c>System.Random::.ctor</c> 형식.</param>
public readonly record struct MemberUse(string Assembly, string Location, string Target)
{
    /// <summary>리포트 한 줄.</summary>
    public override string ToString() => $"{Assembly}: {Location} → {Target}";
}

/// <summary>
/// 컴파일된 어셈블리의 IL 을 훑어 금지된 API 호출을 찾는다. docs/15 §3 · CLAUDE.md §2.3.
///
/// <b>소스 문자열 검색으로는 안 된다.</b> 이 저장소는 주석이 한국어로 길고
/// <c>"new Random"</c> 같은 문자열이 주석·문서 주석·테스트 단언에 그대로 나온다 —
/// 실제로 <c>BucketTransitionTests</c> 와 <c>DryRunValidatorTests</c> 가 그 문자열을 들고 있다.
/// <c>ArchitectureTests</c> 의 csproj XML 읽기도 여기엔 못 쓴다 (docs/15 T5-05).
///
/// 판정은 <b>메타데이터</b>가 한다 — 어셈블리가 그 멤버를 참조조차 하지 않으면 부를 수 없다.
/// IL 순회는 <b>어디에서</b> 부르는지를 붙이기 위한 것이고, 못 찾아도 참조 자체를 위반으로 남긴다.
/// </summary>
public static class IlScanner
{
    /// <summary>
    /// 이 어셈블리가 <paramref name="isWatched"/> 가 참이라고 답하는 멤버를 쓰는 곳을 전부 찾는다.
    /// </summary>
    /// <param name="assemblyPath">스캔할 어셈블리 파일.</param>
    /// <param name="isWatched">(타입 전체이름, 멤버이름) → 감시 대상인가.</param>
    public static ImmutableArray<MemberUse> FindUses(string assemblyPath, Func<string, string, bool> isWatched)
    {
        ArgumentNullException.ThrowIfNull(isWatched);

        string assembly = Path.GetFileNameWithoutExtension(assemblyPath);

        using FileStream stream = File.OpenRead(assemblyPath);
        using var reader = new PEReader(stream);

        MetadataReader metadata = reader.GetMetadataReader();

        // 1) 감시 대상 멤버의 토큰을 모은다. 여기가 판정의 근거다.
        var watched = new Dictionary<int, string>();

        foreach (MemberReferenceHandle handle in metadata.MemberReferences)
        {
            MemberReference member = metadata.GetMemberReference(handle);

            if (member.Parent.Kind != HandleKind.TypeReference)
            {
                continue;
            }

            string type = FullNameOf(metadata, (TypeReferenceHandle)member.Parent);
            string name = metadata.GetString(member.Name);

            if (isWatched(type, name))
            {
                watched[MetadataTokens.GetToken(handle)] = $"{type}::{name}";
            }
        }

        if (watched.Count == 0)
        {
            return [];
        }

        // 2) IL 을 훑어 어느 메서드가 부르는지 붙인다.
        var uses = ImmutableArray.CreateBuilder<MemberUse>();
        var located = new HashSet<int>();

        foreach (MethodDefinitionHandle handle in metadata.MethodDefinitions)
        {
            MethodDefinition method = metadata.GetMethodDefinition(handle);

            if (method.RelativeVirtualAddress == 0)
            {
                continue;
            }

            byte[] il = reader.GetMethodBody(method.RelativeVirtualAddress).GetILBytes() ?? [];

            foreach (int raw in Tokens(il))
            {
                int token = Normalize(metadata, raw);

                if (!watched.TryGetValue(token, out string? target))
                {
                    continue;
                }

                located.Add(token);
                uses.Add(new MemberUse(assembly, LocationOf(metadata, method), target));
            }
        }

        // 3) IL 에서 못 찾은 참조도 남긴다. 디코더에 구멍이 있어도 위반을 놓치지 않는다.
        foreach ((int token, string target) in watched.OrderBy(kv => kv.Key))
        {
            if (!located.Contains(token))
            {
                uses.Add(new MemberUse(assembly, "(참조만)", target));
            }
        }

        return uses.ToImmutable();
    }

    /// <summary>
    /// 제네릭 메서드 인스턴스화(<c>MethodSpec</c>)는 원본 <c>MemberRef</c> 토큰으로 되돌린다.
    /// <c>RandomNumberGenerator.GetItems&lt;T&gt;</c> 처럼 제네릭인 것들이 이 경로로 불린다.
    ///
    /// <para>
    /// <b>행 번호를 먼저 본다.</b> <see cref="Tokens"/> 는 IL 의 4바이트 피연산자를 <b>전부</b>
    /// 모으므로 <c>ldc.i4</c> 상수나 분기 오프셋도 섞여 온다. 그중 상위 바이트가 우연히
    /// <c>0x2B</c>(MethodSpec)면 <see cref="MetadataTokens.EntityHandle(int)"/> 는 통과하고
    /// <c>GetMethodSpecification</c> 이 <c>BadImageFormatException</c> 을 던진다 —
    /// 실제로 <c>Npc.Host</c> 를 훑다가 그렇게 됐다. <see cref="TryHandle"/> 의 주석이 말하는
    /// "던지지 않는다" 는 여기까지 와야 성립한다.
    /// </para>
    /// </summary>
    private static int Normalize(MetadataReader metadata, int token)
    {
        if (!TryHandle(token, out EntityHandle handle) || handle.Kind != HandleKind.MethodSpecification)
        {
            return token;
        }

        if (MetadataTokens.GetRowNumber(handle) > metadata.GetTableRowCount(TableIndex.MethodSpec))
        {
            return token;   // 토큰이 아니라 그냥 4바이트였다
        }

        EntityHandle method = metadata.GetMethodSpecification((MethodSpecificationHandle)handle).Method;

        return method.IsNil ? token : MetadataTokens.GetToken(method);
    }

    /// <summary>IL 이 잘려 있거나 토큰이 아닌 4바이트를 읽었을 수 있다 — 던지지 않는다.</summary>
    private static bool TryHandle(int token, out EntityHandle handle)
    {
        try
        {
            handle = MetadataTokens.EntityHandle(token);
            return true;
        }
        catch (ArgumentException)
        {
            handle = default;
            return false;
        }
    }

    private static string LocationOf(MetadataReader metadata, MethodDefinition method)
    {
        TypeDefinition type = metadata.GetTypeDefinition(method.GetDeclaringType());
        string ns = metadata.GetString(type.Namespace);
        string name = metadata.GetString(type.Name);

        return string.IsNullOrEmpty(ns)
            ? $"{name}.{metadata.GetString(method.Name)}"
            : $"{ns}.{name}.{metadata.GetString(method.Name)}";
    }

    private static string FullNameOf(MetadataReader metadata, TypeReferenceHandle handle)
    {
        TypeReference type = metadata.GetTypeReference(handle);
        string name = metadata.GetString(type.Name);

        // 중첩 타입은 바깥 타입이 ResolutionScope 다.
        if (type.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            return $"{FullNameOf(metadata, (TypeReferenceHandle)type.ResolutionScope)}+{name}";
        }

        string ns = metadata.GetString(type.Namespace);

        return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
    }

    // ------------------------------------------------------------------ IL 디코더

    /// <summary>IL 에서 4바이트 메타데이터 토큰 피연산자를 전부 뽑는다.</summary>
    private static List<int> Tokens(byte[] il)
    {
        var tokens = new List<int>();
        int i = 0;

        while (i < il.Length)
        {
            int opcode = il[i++];
            int operand;

            if (opcode == 0xFE)
            {
                if (i >= il.Length)
                {
                    break;
                }

                operand = TwoByteOperandLength(il[i++]);
            }
            else
            {
                operand = OneByteOperandLength(opcode);
            }

            if (operand == Switch)
            {
                if (i + 4 > il.Length)
                {
                    break;
                }

                int count = BitConverter.ToInt32(il, i);
                i += 4 + (4 * count);
                continue;
            }

            if (operand == 4 && i + 4 <= il.Length)
            {
                tokens.Add(BitConverter.ToInt32(il, i));
            }

            i += operand;
        }

        return tokens;
    }

    /// <summary><c>switch</c> 표시. 피연산자 길이가 가변이다.</summary>
    private const int Switch = -1;

    private static int OneByteOperandLength(int opcode) => opcode switch
    {
        // ldarg.s ldarga.s starg.s ldloc.s ldloca.s stloc.s
        >= 0x0E and <= 0x13 => 1,
        0x1F => 1,                          // ldc.i4.s
        0x20 or 0x22 => 4,                  // ldc.i4 ldc.r4
        0x21 or 0x23 => 8,                  // ldc.i8 ldc.r8
        0x27 or 0x28 or 0x29 => 4,          // jmp call calli
        >= 0x2B and <= 0x37 => 1,           // br.s ~ blt.un.s
        >= 0x38 and <= 0x44 => 4,           // br ~ blt.un
        0x45 => Switch,
        0x6F => 4,                          // callvirt
        >= 0x70 and <= 0x75 => 4,           // cpobj ldobj ldstr newobj castclass isinst
        0x79 => 4,                          // unbox
        >= 0x7B and <= 0x81 => 4,           // ldfld ~ stobj
        0x8C or 0x8D or 0x8F => 4,          // box newarr ldelema
        >= 0xA3 and <= 0xA5 => 4,           // ldelem stelem unbox.any
        0xC2 or 0xC6 => 4,                  // refanyval mkrefany
        0xD0 => 4,                          // ldtoken
        0xDD => 4,                          // leave
        0xDE => 1,                          // leave.s
        _ => 0,
    };

    private static int TwoByteOperandLength(int opcode) => opcode switch
    {
        0x06 or 0x07 => 4,                  // ldftn ldvirtftn
        >= 0x09 and <= 0x0E => 2,           // ldarg ldarga starg ldloc ldloca stloc
        0x12 => 1,                          // unaligned.
        0x15 or 0x16 => 4,                  // initobj constrained.
        0x19 => 1,                          // no.
        0x1C => 4,                          // sizeof
        _ => 0,
    };
}

/// <summary>
/// T5-05 — 난수 봉인 감사. docs/15 §3 · CLAUDE.md §2.3.
///
/// 게임 로직에 시드 없는 <see cref="Random"/> 이 하나라도 있으면 리플레이가 어긋난다.
/// <c>Npc.Sim</c> 은 뺀다 — 게임서버 대역이고 <c>SimOptions.Seed</c> 로 이미 시드가 고정돼 있다.
/// <c>Npc.Llm</c> 도 뺀다 — LLM 응답 자체가 비결정적이고 리플레이는 기록된 플랜을 쓴다.
/// </summary>
[Trait("Category", "Determinism")]
public sealed class RandomAuditTests
{
    /// <summary>감사 대상. 결정론이 걸린 게임 로직 셋이다.</summary>
    public static string[] Assemblies =>
    [
        typeof(BucketKey).Assembly.Location,
        typeof(NpcStore).Assembly.Location,
        typeof(PlanStore).Assembly.Location,
    ];

    private static bool IsRandom(string type, string member) =>
        string.Equals(type, "System.Random", StringComparison.Ordinal)
        || string.Equals(type, "System.Security.Cryptography.RandomNumberGenerator", StringComparison.Ordinal);

    [Fact]
    public void Determinism_NoUnseededRandom()
    {
        var found = new List<MemberUse>();

        foreach (string assembly in Assemblies)
        {
            found.AddRange(IlScanner.FindUses(assembly, IsRandom));
        }

        // 시드 있는 Random 도 통과시키지 않는다. CLAUDE.md §2.3 의 대체는 (npcId, tick) 해시(PlanHash)이고,
        // Random 을 하나 허용하면 다음 사람이 시드 없는 것을 옆에 놓는다.
        Assert.True(found.Count == 0, Report(found));
    }

    /// <summary>
    /// 스캐너가 실제로 뭔가를 잡는지 확인한다.
    /// <b>이게 없으면 위 테스트는 "아무것도 못 찾는 스캐너"로도 통과한다.</b>
    /// 대조군 어셈블리를 즉석에서 만들어 <c>new Random()</c> 을 심는다.
    /// </summary>
    [Fact]
    public void Scanner_FindsRandomInAControlAssembly()
    {
        string path = Path.Combine(Path.GetTempPath(), $"audit-control-{Guid.NewGuid():N}.dll");

        try
        {
            EmitControlAssembly(path);

            ImmutableArray<MemberUse> uses = IlScanner.FindUses(path, IsRandom);

            Assert.NotEmpty(uses);
            Assert.Contains(uses, u => u.Target.Contains("System.Random", StringComparison.Ordinal));

            // 위치까지 붙어야 리포트가 쓸모 있다.
            Assert.Contains(uses, u => u.Location.Contains("Roll", StringComparison.Ordinal));

            // 감시하지 않는 멤버는 잡지 않는다.
            Assert.Empty(IlScanner.FindUses(path, (t, _) => t == "System.Threading.Timer"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void EmitControlAssembly(string path)
    {
        var builder = new PersistedAssemblyBuilder(
            new AssemblyName("Npc.Tests.AuditControl"), typeof(object).Assembly);

        ModuleBuilder module = builder.DefineDynamicModule("Npc.Tests.AuditControl");
        TypeBuilder type = module.DefineType("Control", TypeAttributes.Public | TypeAttributes.Class);

        MethodBuilder method = type.DefineMethod(
            "Roll", MethodAttributes.Public | MethodAttributes.Static, typeof(int), Type.EmptyTypes);

        ILGenerator il = method.GetILGenerator();

        il.Emit(OpCodes.Newobj, typeof(Random).GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Callvirt, typeof(Random).GetMethod(nameof(Random.Next), Type.EmptyTypes)!);
        il.Emit(OpCodes.Ret);

        type.CreateType();

        using FileStream stream = File.Create(path);

        builder.Save(stream);
    }

    /// <summary>실패 메시지. 무엇을 어디서 부르는지와 대체 방법을 같이 적는다.</summary>
    internal static string Report(IReadOnlyCollection<MemberUse> uses)
    {
        if (uses.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();

        sb.Append(uses.Count).Append("건 발견 — 전부 PlanHash 기반 (npcId, tick) 해시로 바꾼다:\n");

        foreach (MemberUse use in uses.OrderBy(u => u.Assembly, StringComparer.Ordinal)
            .ThenBy(u => u.Location, StringComparer.Ordinal))
        {
            sb.Append("  ").Append(use).Append('\n');
        }

        return sb.ToString();
    }
}
