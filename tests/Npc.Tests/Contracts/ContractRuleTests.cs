using System.Reflection;
using Npc.Contracts;

namespace Npc.Tests.Contracts;

/// <summary>
/// docs/02 §1 의 N1~N4 를 리플렉션으로 강제한다. docs/02 §6 테스트 계약.
/// 이 규칙들은 "인프로세스라서 공짜였던 가정"을 미리 제거하기 위한 것이다.
/// 어기면 나중에 실제 네트워크를 붙일 때 NPC 서버 코드를 재작성하게 된다.
/// </summary>
[Trait("Category", "Contracts")]
public sealed class ContractRuleTests
{
    private static readonly Assembly s_contracts = typeof(NpcCommand).Assembly;

    /// <summary>Npc.Contracts 어셈블리의 모든 public struct.</summary>
    private static IEnumerable<Type> PublicStructs() =>
        s_contracts.GetExportedTypes().Where(t => t.IsValueType && !t.IsEnum);

    [Fact]
    public void Contracts_NoStringFields()
    {
        List<string> violations = [];

        foreach (Type type in PublicStructs())
        {
            foreach (MemberInfo member in ValueMembers(type))
            {
                Type memberType = MemberType(member);
                if (memberType == typeof(string) || memberType == typeof(char[]))
                {
                    violations.Add($"{type.Name}.{member.Name}");
                }
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void Contracts_NoDateTimeFields()
    {
        Type[] banned =
        [
            typeof(DateTime), typeof(DateTimeOffset), typeof(TimeSpan),
            typeof(DateOnly), typeof(TimeOnly),
        ];

        List<string> violations = [];

        foreach (Type type in PublicStructs())
        {
            foreach (MemberInfo member in ValueMembers(type))
            {
                if (banned.Contains(MemberType(member)))
                {
                    violations.Add($"{type.Name}.{member.Name}");
                }
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void Contracts_AllValueTypes()
    {
        List<string> violations = [];

        foreach (Type type in PublicStructs())
        {
            // readonly record struct 는 IsValueType && 모든 인스턴스 필드가 initonly 다.
            bool allFieldsReadOnly = type
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .All(f => f.IsInitOnly);

            if (!allFieldsReadOnly)
            {
                violations.Add($"{type.Name}: 가변 필드가 있다 (readonly struct 가 아니다)");
            }

            // 참조 필드·가변 컬렉션·object 금지 (N2).
            foreach (MemberInfo member in ValueMembers(type))
            {
                Type memberType = MemberType(member);
                if (!memberType.IsValueType && memberType != typeof(string))
                {
                    violations.Add($"{type.Name}.{member.Name}: 참조 타입 {memberType.Name}");
                }
            }
        }

        Assert.Empty(violations);
    }

    /// <summary>N1 — IGameServerLink 에 반환값 있는 전송 메서드가 없다.</summary>
    [Fact]
    public void Contracts_LinkHasNoRequestResponseMethod()
    {
        List<string> violations = [];

        foreach (MethodInfo method in typeof(IGameServerLink).GetMethods())
        {
            Type ret = method.ReturnType;

            if (ret == typeof(void) || ret == typeof(ValueTask) || ret == typeof(Task))
            {
                continue;
            }

            // 프로퍼티 getter 는 값을 반환해도 된다 — 전송 메서드가 아니다.
            if (method.IsSpecialName)
            {
                continue;
            }

            violations.Add($"IGameServerLink.{method.Name} → {ret.Name}");
        }

        Assert.Empty(violations);
    }

    /// <summary>N1 — Task&lt;T&gt;/ValueTask&lt;T&gt; 를 반환하는 멤버가 아예 없다.</summary>
    [Fact]
    public void Contracts_LinkHasNoGenericTaskMember()
    {
        List<string> violations = [];

        foreach (MethodInfo method in typeof(IGameServerLink).GetMethods())
        {
            Type ret = method.ReturnType;
            if (!ret.IsGenericType)
            {
                continue;
            }

            Type open = ret.GetGenericTypeDefinition();
            if (open == typeof(Task<>) || open == typeof(ValueTask<>))
            {
                violations.Add($"IGameServerLink.{method.Name} → {ret.Name}");
            }
        }

        Assert.Empty(violations);
    }

    /// <summary>N5 · N6 — 헤더 필수 필드가 required 로 선언돼 있다.</summary>
    [Fact]
    public void Contracts_HeadersAreRequired()
    {
        Assert.True(IsRequired(typeof(NpcCommand), nameof(NpcCommand.Correlation)));
        Assert.True(IsRequired(typeof(NpcCommand), nameof(NpcCommand.IssuedAt)));
        Assert.True(IsRequired(typeof(GameEvent), nameof(GameEvent.Sequence)));
        Assert.True(IsRequired(typeof(GameEvent), nameof(GameEvent.OccurredAt)));
    }

    private static bool IsRequired(Type type, string property) =>
        type.GetProperty(property)!
            .GetCustomAttributes(typeof(System.Runtime.CompilerServices.RequiredMemberAttribute), false)
            .Length > 0;

    /// <summary>패킷의 데이터 멤버 — public 프로퍼티와 public 필드.</summary>
    private static IEnumerable<MemberInfo> ValueMembers(Type type)
    {
        foreach (PropertyInfo p in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            // record struct 가 생성하는 EqualityContract 는 데이터가 아니다.
            if (p.Name is "EqualityContract")
            {
                continue;
            }

            yield return p;
        }

        foreach (FieldInfo f in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
        {
            yield return f;
        }
    }

    private static Type MemberType(MemberInfo member) => member switch
    {
        PropertyInfo p => p.PropertyType,
        FieldInfo f => f.FieldType,
        _ => throw new InvalidOperationException($"예상하지 못한 멤버 종류: {member.MemberType}"),
    };
}
