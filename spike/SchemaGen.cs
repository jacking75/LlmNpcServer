using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Spike;

// ---------------------------------------------------------------- 축소 마스터데이터 모델
/// <summary>액션 파라미터 1개. docs/01 §2.3 의 타입 표를 따른다.</summary>
internal sealed record ActionParam(
    string Name,
    string Type,
    bool Required,
    string[]? Values,
    int? Min,
    int? Max,
    string? Default);

/// <summary>액션 1개. docs/01 §2.1 의 필드 중 스파이크에 필요한 것만.</summary>
internal sealed record ActionDef(
    string Id,
    int Code,
    string Category,
    string Desc,
    ActionParam[] Params,
    string[] Requires,
    string[] Forbids,
    string[] Grants,
    string[] Clears,
    int Cost,
    int DefaultTimeoutS);

/// <summary>월드 플래그 1개. docs/01 §1.</summary>
internal sealed record FlagDef(int Bit, string Id, string Desc);

/// <summary>축소 마스터데이터 전체. 기동 시 1회 로드하고 이후 불변.</summary>
internal sealed record MinCatalog(ActionDef[] Actions, FlagDef[] Flags, string[] ItemVocabulary)
{
    public ActionDef? Find(string id) => Actions.FirstOrDefault(a => a.Id == id);
}

/// <summary>스키마에 무엇을 넣을지. G0-1 미달 시 요소별로 끄면서 원인을 좁힌다.</summary>
/// <param name="Name">파일 이름에 붙는 변종 이름.</param>
/// <param name="Const">`schema: {"const": 1}` 사용.</param>
/// <param name="Pattern">`goal` 에 정규식 사용.</param>
/// <param name="StringLength">`reasoning` 에 maxLength 사용.</param>
/// <param name="ItemBounds">`steps` 에 minItems/maxItems 사용.</param>
/// <param name="TimeoutBounds">`timeout_s` 에 minimum/maximum 사용.</param>
/// <param name="NoAdditionalProperties">`additionalProperties: false` 사용.</param>
/// <param name="ArgsUnion">
/// `args` 에 전 액션 파라미터의 평탄화 합집합을 properties 로 싣는다.
/// **끄면 안 된다.** T0-09 에서 확인: `args` 를 properties 없는 object 로 두면 Gemini 의
/// 강제 디코딩이 매 스텝 `args: {}` 만 뱉는다 (docs/measurements/W1_schema.md).
/// </param>
internal readonly record struct SchemaOptions(
    string Name,
    bool Const,
    bool Pattern,
    bool StringLength,
    bool ItemBounds,
    bool TimeoutBounds,
    bool NoAdditionalProperties,
    bool ArgsUnion)
{
    /// <summary>docs/03 §2 + args 평탄화 합집합.</summary>
    public static SchemaOptions Full => new("full", true, true, true, true, true, true, true);

    /// <summary>
    /// 제공사별 강제 디코딩이 흔히 거부하는 요소를 뺀 것 (docs/12 §4 의 위험 표).
    /// Gemini 의 responseSchema 는 pattern·const·maxLength·additionalProperties 를 지원하지 않는다.
    /// </summary>
    public static SchemaOptions Relaxed => new("relaxed", false, false, false, true, true, false, true);

    /// <summary>docs/03 §2 원안 — `args` 가 빈 object. 실패를 재현해 보이기 위해 남긴다.</summary>
    public static SchemaOptions BareArgs => new("bare", true, true, true, true, true, true, false);

    public static SchemaOptions ByName(string name) => name switch
    {
        "relaxed" => Relaxed,
        "bare" => BareArgs,
        _ => Full,
    };
}

/// <summary>
/// T0-05 — `actions.min.json` 에서 플랜 JSON Schema 를 생성한다.
/// `action` 열거값을 손으로 유지하면 반드시 카탈로그와 어긋난다 (docs/03 §2 주석).
/// </summary>
internal static class SchemaGen
{
    private static MinCatalog? _cached;

    public static string DataDir => Path.Combine(Program.SpikeRoot, "data");

    public static string OutDir
    {
        get
        {
            var dir = Path.Combine(Program.SpikeRoot, "out");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    // ---------------------------------------------------------------- 로드
    /// <summary>축소 마스터데이터를 읽는다. 프로세스 생애 동안 1회.</summary>
    public static MinCatalog Load()
    {
        if (_cached is not null)
        {
            return _cached;
        }

        using var actionsDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(DataDir, "actions.min.json")));
        using var flagsDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(DataDir, "flags.min.json")));

        var flags = flagsDoc.RootElement.GetProperty("flags").EnumerateArray()
            .Select(f => new FlagDef(
                f.GetProperty("bit").GetInt32(),
                f.GetProperty("id").GetString()!,
                f.GetProperty("desc").GetString()!))
            .ToArray();

        var vocab = actionsDoc.RootElement.GetProperty("item_vocabulary").EnumerateArray()
            .Select(v => v.GetString()!)
            .ToArray();

        var actions = actionsDoc.RootElement.GetProperty("actions").EnumerateArray()
            .Select(ReadAction)
            .ToArray();

        var catalog = new MinCatalog(actions, flags, vocab);

        // 참조 무결성 — docs/01 §11 V2 의 축소판. 어기면 기동 실패다.
        var flagIds = flags.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var a in actions)
        {
            foreach (var f in a.Requires.Concat(a.Forbids).Concat(a.Grants).Concat(a.Clears))
            {
                if (!flagIds.Contains(f))
                {
                    throw new InvalidDataException($"{a.Id}: 정의되지 않은 flag '{f}'");
                }
            }
        }

        _cached = catalog;
        return catalog;
    }

    private static ActionDef ReadAction(JsonElement a)
    {
        var ps = new List<ActionParam>();
        foreach (var p in a.GetProperty("params").EnumerateObject())
        {
            var v = p.Value;
            ps.Add(new ActionParam(
                Name: p.Name,
                Type: v.GetProperty("type").GetString()!,
                Required: v.TryGetProperty("required", out var r) && r.GetBoolean(),
                Values: v.TryGetProperty("values", out var vals)
                    ? vals.EnumerateArray().Select(x => x.GetString()!).ToArray()
                    : null,
                Min: v.TryGetProperty("min", out var mn) ? mn.GetInt32() : null,
                Max: v.TryGetProperty("max", out var mx) ? mx.GetInt32() : null,
                Default: v.TryGetProperty("default", out var df)
                    ? df.ValueKind == JsonValueKind.String ? df.GetString() : df.GetRawText()
                    : null));
        }

        static string[] Arr(JsonElement e, string name) =>
            e.GetProperty(name).EnumerateArray().Select(x => x.GetString()!).ToArray();

        return new ActionDef(
            Id: a.GetProperty("id").GetString()!,
            Code: a.GetProperty("code").GetInt32(),
            Category: a.GetProperty("category").GetString()!,
            Desc: a.GetProperty("desc").GetString()!,
            Params: [.. ps],
            Requires: Arr(a, "requires"),
            Forbids: Arr(a, "forbids"),
            Grants: Arr(a, "grants"),
            Clears: Arr(a, "clears"),
            Cost: a.GetProperty("cost").GetInt32(),
            DefaultTimeoutS: a.GetProperty("default_timeout_s").GetInt32());
    }

    /// <summary>docs/03 §2 의 POI 심볼 허용 목록. 스키마와 검증기가 같은 목록을 본다.</summary>
    public static readonly string[] PoiSymbols =
    [
        "$home", "$workplace", "$market", "$tavern", "$temple", "$gate",
        "$nearest_field", "$nearest_safe", "$nearest_shelter",
    ];

    // ---------------------------------------------------------------- 생성
    /// <summary>액션 id 목록에서 플랜 스키마를 만든다. `oneOf` 를 쓰지 않고 중첩은 3단을 넘지 않는다.</summary>
    /// <param name="actionIds">`action` 열거값.</param>
    /// <param name="opt">스키마 변종.</param>
    /// <param name="defs">args 합집합을 만들 액션 정의. null 이면 args 는 빈 object 가 된다.</param>
    public static string Build(IEnumerable<string> actionIds, SchemaOptions opt, IReadOnlyList<ActionDef>? defs = null)
    {
        var actions = new JsonArray();
        foreach (var id in actionIds)
        {
            actions.Add(id);
        }

        var stepProps = new JsonObject
        {
            ["action"] = new JsonObject { ["enum"] = actions },
            // 액션별 oneOf 분기는 강제 디코딩 FSM 을 폭발시킨다 (docs/03 §2 주석). 대신
            // 파라미터 이름을 **평탄화해 합집합**으로 싣는다 — 전부 optional 이고, 어떤
            // 액션이 어떤 인자를 받는지는 검증기 2단이 본다.
            ["args"] = opt.ArgsUnion && defs is not null
                ? ArgsUnionSchema(defs, opt)
                : new JsonObject { ["type"] = "object" },
            ["timeout_s"] = TimeoutSchema(opt),
        };

        var step = new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("action", "args"),
            ["properties"] = stepProps,
        };
        if (opt.NoAdditionalProperties)
        {
            step["additionalProperties"] = false;
        }

        var steps = new JsonObject
        {
            ["type"] = "array",
            ["items"] = step,
        };
        if (opt.ItemBounds)
        {
            steps["minItems"] = 3;
            steps["maxItems"] = 10;
        }

        var goal = new JsonObject { ["type"] = "string" };
        if (opt.Pattern)
        {
            goal["pattern"] = "^[a-z][a-z0-9_]{2,31}$";
        }

        var reasoning = new JsonObject { ["type"] = "string" };
        if (opt.StringLength)
        {
            reasoning["maxLength"] = 200;
        }

        var root = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["title"] = "NpcPlan",
            ["type"] = "object",
            ["required"] = new JsonArray("schema", "goal", "steps", "loop"),
            ["properties"] = new JsonObject
            {
                ["schema"] = opt.Const
                    ? new JsonObject { ["const"] = 1 }
                    : new JsonObject { ["type"] = "integer" },
                ["goal"] = goal,
                ["reasoning"] = reasoning,
                ["loop"] = new JsonObject { ["type"] = "boolean" },
                ["on_step_fail"] = new JsonObject
                {
                    ["enum"] = new JsonArray("fallback", "retry_once", "skip", "replan"),
                },
                ["steps"] = steps,
            },
        };
        if (opt.NoAdditionalProperties)
        {
            root["additionalProperties"] = false;
        }

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// 전 액션 파라미터의 평탄화 합집합. 같은 이름은 타입이 같아야 하고, 값 목록은 합집합,
    /// 정수 범위는 최소~최대의 합집합을 쓴다. 좁히는 일은 검증기 2단이 한다.
    /// </summary>
    private static JsonObject ArgsUnionSchema(IReadOnlyList<ActionDef> defs, SchemaOptions opt)
    {
        var props = new JsonObject();

        // 카탈로그 등장 순서를 유지한다 — 이름 정렬로 바꾸면 프리픽스 SHA 가 흔들린다.
        var seen = new List<string>();
        var byName = new Dictionary<string, List<ActionParam>>(StringComparer.Ordinal);
        foreach (var p in defs.SelectMany(d => d.Params))
        {
            if (!byName.TryGetValue(p.Name, out var list))
            {
                byName[p.Name] = list = [];
                seen.Add(p.Name);
            }

            list.Add(p);
        }

        foreach (var name in seen)
        {
            var ps = byName[name];
            var type = ps[0].Type;
            props[name] = type switch
            {
                // 심볼·아이템은 열거값으로 못 박는다. 강제 디코딩이 환각 POI 를 아예 못 만든다.
                "poi_ref" => new JsonObject { ["type"] = "string", ["enum"] = ToArray(PoiSymbols) },
                "item_ref" => new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = ToArray(ps.SelectMany(p => p.Values ?? []).Distinct(StringComparer.Ordinal)),
                },
                "enum" => new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = ToArray(ps.SelectMany(p => p.Values ?? []).Distinct(StringComparer.Ordinal)),
                },
                // npc_ref 는 self / nearest:<archetype> / poi_owner:<poi> — 열거로 못 박기 어렵다.
                "npc_ref" => new JsonObject { ["type"] = "string" },
                "int" => IntSchema(ps, opt),
                _ => new JsonObject { ["type"] = "string" },
            };
        }

        var args = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            // 비워 둔 채로 두면 제공사가 "전부 필수"로 해석해 매 스텝에 전 인자를 채워 넣는다.
            ["required"] = new JsonArray(),
        };
        if (opt.NoAdditionalProperties)
        {
            args["additionalProperties"] = false;
        }

        return args;
    }

    private static JsonObject IntSchema(List<ActionParam> ps, SchemaOptions opt)
    {
        var o = new JsonObject { ["type"] = "integer" };
        if (opt.TimeoutBounds)
        {
            o["minimum"] = ps.Min(p => p.Min ?? 0);
            o["maximum"] = ps.Max(p => p.Max ?? 0);
        }

        return o;
    }

    private static JsonArray ToArray(IEnumerable<string> values)
    {
        var a = new JsonArray();
        foreach (var v in values)
        {
            a.Add(v);
        }

        return a;
    }

    private static JsonObject TimeoutSchema(SchemaOptions opt)
    {
        var t = new JsonObject { ["type"] = "integer" };
        if (opt.TimeoutBounds)
        {
            t["minimum"] = 5;
            t["maximum"] = 7200;
        }

        return t;
    }

    /// <summary>지정한 변종의 스키마 문자열. 캐시하지 않는다 — 기동 시 1회만 부른다.</summary>
    public static string BuildFromCatalog(SchemaOptions opt)
    {
        var c = Load();
        return Build(c.Actions.Select(a => a.Id), opt, c.Actions);
    }

    // ---------------------------------------------------------------- 검증 도우미
    /// <summary>JSON Schema 검증. 실패 시 (false, 이유). 검증기 1단(V1.SCHEMA)이 이걸 쓴다.</summary>
    public static (bool Ok, string Detail) Check(string schemaJson, string instanceJson)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(instanceJson);
        }
        catch (JsonException ex)
        {
            return (false, $"parse: {ex.Message}");
        }

        using var _ = doc;
        var schema = JsonSchema.FromText(schemaJson);
        var result = schema.Evaluate(doc.RootElement, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
            RequireFormatValidation = false,
        });

        if (result.IsValid)
        {
            return (true, "");
        }

        var reasons = Flatten(result)
            .Where(r => r is { IsValid: false, Errors.Count: > 0 })
            .SelectMany(r => r.Errors!.Select(e => $"{r.InstanceLocation}: {e.Key} {e.Value}"))
            .Distinct()
            .Take(5);

        return (false, string.Join(" | ", reasons));
    }

    private static IEnumerable<EvaluationResults> Flatten(EvaluationResults r)
    {
        yield return r;
        foreach (var d in r.Details ?? [])
        {
            foreach (var x in Flatten(d))
            {
                yield return x;
            }
        }
    }

    // ---------------------------------------------------------------- 완료 조건
    /// <summary>docs/03 §1 의 예시 플랜. 완료 조건 검증용으로 사양에서 그대로 옮겼다.</summary>
    private const string SpecExamplePlan = """
        {
          "schema": 1,
          "goal": "restock_and_forge",
          "reasoning": "ore depleted; siege raises weapon demand",
          "steps": [
            { "action": "MoveTo",  "args": { "poi": "$nearest_field", "speed": "run" }, "timeout_s": 300 },
            { "action": "Mine",    "args": { "resource": "iron_ore", "count": 8 }, "timeout_s": 900 },
            { "action": "MoveTo",  "args": { "poi": "$workplace" }, "timeout_s": 300 },
            { "action": "Craft",   "args": { "recipe": "iron_sword", "count": 3 }, "timeout_s": 1800 },
            { "action": "Store",   "args": { "item": "iron_sword", "count": 3 } },
            { "action": "MoveTo",  "args": { "poi": "$home" }, "timeout_s": 300 },
            { "action": "Sleep",   "args": { "until_time": "Morning" } }
          ],
          "on_step_fail": "fallback",
          "loop": true
        }
        """;

    /// <summary>
    /// docs/03 §2 스키마가 열거하는 37개 액션. 예시 플랜의 `Mine` 이 축소 카탈로그(12개)에
    /// 없으므로, 완료 조건 확인은 이 정식 목록으로 생성한 스키마에 대해서도 한 번 돌린다.
    /// </summary>
    private static readonly string[] SpecActionIds =
    [
        "MoveTo", "Follow", "Wander", "Flee", "Patrol",
        "Work", "Gather", "Mine", "Farm", "Fish", "Craft", "Cook", "Repair",
        "Talk", "Trade", "Greet", "Gossip", "Pray", "Perform",
        "Eat", "Drink", "Sleep", "Rest", "Bathe",
        "Attack", "Defend", "Guard", "CallForHelp", "Retreat",
        "PickUp", "Drop", "Store", "Withdraw", "Equip",
        "Wait", "Observe", "Emote",
    ];

    /// <summary>`spike schema` — 스키마를 생성해 out/ 에 쓰고 완료 조건을 확인한다.</summary>
    public static int Run()
    {
        var catalog = Load();
        Console.WriteLine($"catalog: actions={catalog.Actions.Length} flags={catalog.Flags.Length}");

        var ok = true;
        foreach (var opt in new[] { SchemaOptions.Full, SchemaOptions.Relaxed, SchemaOptions.BareArgs })
        {
            var json = BuildFromCatalog(opt);
            var path = Path.Combine(OutDir, opt.Name == "full" ? "plan.schema.json" : $"plan.schema.{opt.Name}.json");
            File.WriteAllText(path, json);
            Console.WriteLine($"wrote {path} ({json.Length} chars)");

            // (1) 사양 예시 플랜 — docs/03 §2 의 정식 37개 열거값으로 생성한 스키마
            var specSchema = Build(SpecActionIds, opt);
            var (okSpec, whySpec) = Check(specSchema, SpecExamplePlan);
            Console.WriteLine($"  [{opt.Name}] docs/03 §1 예시 플랜 (정식 37 액션 열거) : {(okSpec ? "PASS" : "FAIL " + whySpec)}");
            ok &= okSpec;

            // (2) 같은 플랜을 축소 카탈로그(12개)에 맞춘 것 — Mine 은 min 카탈로그에 없다
            var minPlan = SpecExamplePlan
                .Replace("\"Mine\"", "\"Gather\"")
                .Replace("\"resource\": \"iron_ore\"", "\"resource\": \"iron_ore\"");
            var (okMin, whyMin) = Check(json, minPlan);
            Console.WriteLine($"  [{opt.Name}] 같은 플랜 (Mine→Gather, 축소 12 액션 열거) : {(okMin ? "PASS" : "FAIL " + whyMin)}");
            ok &= okMin;

            // (3) 스키마가 실제로 걸러내야 하는 것들
            var badAction = minPlan.Replace("\"Gather\"", "\"Excavate\"");
            var (okBad, _) = Check(json, badAction);
            Console.WriteLine($"  [{opt.Name}] 미정의 액션(Excavate) 거부 : {(okBad ? "FAIL (통과시켜 버렸다)" : "PASS")}");
            ok &= !okBad;

            var tooShort = """
                { "schema": 1, "goal": "nap", "loop": false,
                  "steps": [ { "action": "Rest", "args": {} } ] }
                """;
            var (okShort, _) = Check(json, tooShort);
            var shortExpected = !opt.ItemBounds;   // minItems 를 뺀 변종은 통과가 정상
            Console.WriteLine($"  [{opt.Name}] 스텝 3개 미만 거부 : {(okShort == shortExpected ? "PASS" : "FAIL")}");
            ok &= okShort == shortExpected;
        }

        Console.WriteLine(ok ? "schema OK" : "schema FAILED");
        return ok ? 0 : 1;
    }
}
