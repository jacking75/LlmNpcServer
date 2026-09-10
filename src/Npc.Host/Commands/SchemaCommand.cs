using System.Diagnostics.CodeAnalysis;
using Npc.MasterData;
using Npc.MasterData.Schema;

namespace Npc.Host.Commands;

/// <summary>
/// <c>Npc.Host schema --out docs/schema/</c> (E-02).
///
/// <b>스키마는 생성물이다.</b> 손으로 관리하면 로더와 반드시 어긋나고, 어긋난 스키마는
/// 없는 것보다 나쁘다 — LLM 과 에디터가 그것을 근거로 틀린 것을 만든다.
/// 실체는 <see cref="SchemaCatalog"/> 이고 <c>npc schema</c> 도 같은 것을 부른다.
/// </summary>
public static class SchemaCommand
{
    /// <summary>서브커맨드 이름.</summary>
    public const string Name = "schema";

    /// <summary>사용법.</summary>
    public const string Usage =
        "사용법: Npc.Host schema [--out <dir>] [--masterdata <dir>]";

    /// <summary>돌린다. 0 = 성공, 2 = 인자 오류.</summary>
    [RequiresUnreferencedCode("JSON Schema 발행은 리플렉션을 쓴다. 도구 전용이다.")]
    [RequiresDynamicCode("JSON Schema 발행은 리플렉션을 쓴다. 도구 전용이다.")]
    public static int Run(string[] args, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        string outDir = SchemaCatalog.Directory;
        string masterData = "./masterdata";

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out" when i + 1 < args.Length:
                    outDir = args[++i];
                    break;

                case "--masterdata" when i + 1 < args.Length:
                    masterData = args[++i];
                    break;

                case "--help" or "-h":
                    output.WriteLine(Usage);
                    return 0;

                default:
                    output.WriteLine($"모르는 인자: {args[i]}");
                    output.WriteLine(Usage);
                    return 2;
            }
        }

        // 마스터데이터를 못 읽어도 정적 변형은 낸다 — 구조는 데이터 없이도 설명할 수 있다.
        MasterDataSet? data = null;

        try
        {
            data = MasterDataLoader.Load(masterData);
        }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException
            or DirectoryNotFoundException)
        {
            output.WriteLine($"경고 마스터데이터를 읽지 못해 허용 값을 채우지 못한다: {ex.Message}");
        }

        int count = SchemaCatalog.Write(outDir, data);

        output.WriteLine($"{outDir} 에 스키마 {count}개를 썼다.");

        if (data is null)
        {
            output.WriteLine("정적 변형(*.base.schema.json)만 나왔다.");
        }

        return 0;
    }
}
