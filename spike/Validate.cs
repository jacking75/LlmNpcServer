using System.Text.Json;
using System.Text.Json.Nodes;

namespace Spike;

/// <summary>docs/03 §3 의 검증 단계. 스파이크는 1·2단만 만든다.</summary>
internal enum ValidationStage
{
    None = 0,
    Schema,
    Vocabulary,
}

/// <summary>docs/03 §3 의 <c>ValidationResult</c>.</summary>
/// <param name="FailedAt">None 이면 통과.</param>
/// <param name="Code">"V2.UNKNOWN_POI" 등.</param>
/// <param name="StepIndex">-1 = 문서 전체.</param>
/// <param name="Detail">사람이 읽는 이유. 재시도 피드백에 그대로 실린다.</param>
internal readonly record struct ValidationResult(
    ValidationStage FailedAt,
    string Code,
    int StepIndex,
    string Detail)
{
    public static ValidationResult Ok => new(ValidationStage.None, "", -1, "");

    public bool IsOk => FailedAt == ValidationStage.None;

    public override string ToString() =>
        IsOk ? "OK" : $"{Code}@{StepIndex} {Detail}";
}

/// <summary>
/// T0-08 — 검증기 1·2단 (축소판).
///
/// **강제 디코딩을 신뢰하지 않는다.** 제공사별 strict 지원 수준이 달라서, 스키마를 줬는데도
/// 어긋난 출력이 온다 (docs/03 §3 1단 주석).
/// </summary>
internal static class Validate
{
    /// <summary>docs/03 §2 의 POI 심볼 허용 목록. 스키마 생성기와 같은 목록을 본다.</summary>
    public static string[] PoiSymbols => SchemaGen.PoiSymbols;

    /// <summary>아키타입별 허용 액션. 정식판은 archetypes.json 이 들고 있다 (docs/01 §5).</summary>
    private static readonly Dictionary<string, string[]> AllowedActions = new()
    {
        ["blacksmith"] = ["MoveTo", "Work", "Gather", "Craft", "Trade", "Talk", "Eat", "Sleep", "Rest", "Store", "Wait"],
        ["farmer"] = ["MoveTo", "Work", "Gather", "Trade", "Talk", "Eat", "Sleep", "Rest", "Store", "Wait"],
        ["merchant"] = ["MoveTo", "Work", "Trade", "Talk", "Eat", "Sleep", "Rest", "Store", "Wait"],
        ["town_guard"] = ["MoveTo", "Guard", "Talk", "Eat", "Sleep", "Rest", "Store", "Wait"],
    };

    private static readonly string[] RootFields =
        ["schema", "goal", "reasoning", "steps", "on_step_fail", "loop"];

    private static readonly string[] StepFields = ["action", "args", "timeout_s"];

    private static string? _structuralSchema;

    /// <summary>
    /// 1단에서 쓰는 구조 전용 스키마. `action` 열거값을 빼둔다 —
    /// 액션 어휘는 2단(V2.UNKNOWN_ACTION)의 몫이라서 여기서 잡으면 실패 코드가 뭉개진다.
    /// </summary>
    public static string StructuralSchema
    {
        get
        {
            if (_structuralSchema is not null)
            {
                return _structuralSchema;
            }

            var node = JsonNode.Parse(SchemaGen.BuildFromCatalog(SchemaOptions.Full))!;
            var stepProps = node["properties"]!["steps"]!["items"]!["properties"]!;
            stepProps["action"] = new JsonObject { ["type"] = "string" };

            // args 의 평탄화 합집합도 뺀다. POI·아이템 어휘를 여기서 잡으면
            // V2.UNKNOWN_POI / V2.UNKNOWN_ITEM 이 V1.SCHEMA 로 뭉개진다.
            stepProps["args"] = new JsonObject { ["type"] = "object" };
            _structuralSchema = node.ToJsonString();
            return _structuralSchema;
        }
    }

    // ---------------------------------------------------------------- 본체
    /// <summary>플랜 JSON 을 1·2단으로 검증한다.</summary>
    /// <param name="json">LLM 이 뱉은 원문.</param>
    /// <param name="catalog">축소 마스터데이터.</param>
    /// <param name="archetype">아키타입 id. null 이면 ACTION_NOT_ALLOWED 검사를 건너뛴다.</param>
    /// <param name="ignoreUnknownArgs">
    /// 액션에 없는 인자를 무시한다. 강제 디코딩이 args 합집합을 통째로 채워 넣는 제공사(Gemini)를
    /// 상대로 "관용 통과율"을 같이 재려고 둔 스위치다 (T0-09).
    /// </param>
    public static ValidationResult Check(
        string json, MinCatalog catalog, string? archetype = null, bool ignoreUnknownArgs = false)
    {
        // ---------------- 1단: 스키마
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return new ValidationResult(ValidationStage.Schema, "V1.PARSE", -1, ex.Message);
        }

        using var scope = doc;
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            return new ValidationResult(ValidationStage.Schema, "V1.SCHEMA", -1, $"root is {root.ValueKind}");
        }

        foreach (var p in root.EnumerateObject())
        {
            if (!RootFields.Contains(p.Name, StringComparer.Ordinal))
            {
                return new ValidationResult(ValidationStage.Schema, "V1.EXTRA_FIELD", -1, p.Name);
            }
        }

        if (!root.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
        {
            return new ValidationResult(ValidationStage.Schema, "V1.SCHEMA", -1, "steps 없음");
        }

        var stepCount = steps.GetArrayLength();
        if (stepCount is < 3 or > 10)
        {
            return new ValidationResult(ValidationStage.Schema, "V1.STEP_COUNT", -1, $"{stepCount} steps");
        }

        for (var i = 0; i < stepCount; i++)
        {
            var s = steps[i];
            if (s.ValueKind != JsonValueKind.Object)
            {
                return new ValidationResult(ValidationStage.Schema, "V1.SCHEMA", i, $"step is {s.ValueKind}");
            }

            foreach (var p in s.EnumerateObject())
            {
                if (!StepFields.Contains(p.Name, StringComparer.Ordinal))
                {
                    return new ValidationResult(ValidationStage.Schema, "V1.EXTRA_FIELD", i, p.Name);
                }
            }
        }

        var (schemaOk, why) = SchemaGen.Check(StructuralSchema, json);
        if (!schemaOk)
        {
            return new ValidationResult(ValidationStage.Schema, "V1.SCHEMA", -1, why);
        }

        // ---------------- 2단: 어휘
        var allowed = archetype is not null && AllowedActions.TryGetValue(archetype, out var list) ? list : null;

        for (var i = 0; i < stepCount; i++)
        {
            var step = steps[i];
            var actionId = step.GetProperty("action").GetString() ?? "";
            var def = catalog.Find(actionId);
            if (def is null)
            {
                return new ValidationResult(ValidationStage.Vocabulary, "V2.UNKNOWN_ACTION", i, actionId);
            }

            if (allowed is not null && !allowed.Contains(actionId, StringComparer.Ordinal))
            {
                return new ValidationResult(ValidationStage.Vocabulary, "V2.ACTION_NOT_ALLOWED", i,
                    $"{archetype} 는 {actionId} 를 쓸 수 없다");
            }

            var args = step.GetProperty("args");
            if (args.ValueKind != JsonValueKind.Object)
            {
                return new ValidationResult(ValidationStage.Vocabulary, "V2.TYPE_MISMATCH", i, "args is not an object");
            }

            foreach (var arg in args.EnumerateObject())
            {
                var param = def.Params.FirstOrDefault(p => p.Name == arg.Name);
                if (param is null)
                {
                    if (ignoreUnknownArgs)
                    {
                        continue;
                    }

                    return new ValidationResult(ValidationStage.Vocabulary, "V2.UNKNOWN_ARG", i,
                        $"{actionId}.{arg.Name}");
                }

                var argResult = CheckArg(catalog, def, param, arg.Value, i);
                if (!argResult.IsOk)
                {
                    return argResult;
                }
            }

            foreach (var required in def.Params.Where(p => p.Required))
            {
                if (!args.TryGetProperty(required.Name, out _))
                {
                    return new ValidationResult(ValidationStage.Vocabulary, "V2.MISSING_REQUIRED_ARG", i,
                        $"{actionId}.{required.Name}");
                }
            }
        }

        return ValidationResult.Ok;
    }

    private static ValidationResult CheckArg(
        MinCatalog catalog, ActionDef def, ActionParam p, JsonElement v, int stepIndex)
    {
        ValidationResult Fail(string code, string detail) =>
            new(ValidationStage.Vocabulary, code, stepIndex, $"{def.Id}.{p.Name}: {detail}");

        switch (p.Type)
        {
            case "poi_ref":
                if (v.ValueKind != JsonValueKind.String)
                {
                    return Fail("V2.TYPE_MISMATCH", $"expected poi symbol, got {v.ValueKind}");
                }

                if (!PoiSymbols.Contains(v.GetString(), StringComparer.Ordinal))
                {
                    return Fail("V2.UNKNOWN_POI", v.GetString() ?? "");
                }

                break;

            case "item_ref":
                if (v.ValueKind != JsonValueKind.String)
                {
                    return Fail("V2.TYPE_MISMATCH", $"expected item id, got {v.ValueKind}");
                }

                var item = v.GetString()!;
                // values 가 있으면 그 목록이, 없으면 전체 아이템 어휘가 기준이다.
                var vocabulary = p.Values ?? catalog.ItemVocabulary;
                if (!vocabulary.Contains(item, StringComparer.Ordinal))
                {
                    return Fail("V2.UNKNOWN_ITEM", item);
                }

                break;

            case "npc_ref":
                if (v.ValueKind != JsonValueKind.String)
                {
                    return Fail("V2.TYPE_MISMATCH", $"expected npc symbol, got {v.ValueKind}");
                }

                var npc = v.GetString()!;
                var npcOk = npc == "self"
                    || npc.StartsWith("nearest:", StringComparison.Ordinal)
                    || npc.StartsWith("poi_owner:", StringComparison.Ordinal);
                if (!npcOk)
                {
                    return Fail("V2.TYPE_MISMATCH", $"npc_ref '{npc}' — self/nearest:/poi_owner: 만 허용");
                }

                break;

            case "enum":
                if (v.ValueKind != JsonValueKind.String)
                {
                    return Fail("V2.TYPE_MISMATCH", $"expected enum string, got {v.ValueKind}");
                }

                if (p.Values is { Length: > 0 } && !p.Values.Contains(v.GetString(), StringComparer.Ordinal))
                {
                    return Fail("V2.TYPE_MISMATCH", $"'{v.GetString()}' not in {{{string.Join(",", p.Values)}}}");
                }

                break;

            case "int":
                if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var n))
                {
                    return Fail("V2.TYPE_MISMATCH", $"expected int, got {v.ValueKind}");
                }

                if (n < (p.Min ?? int.MinValue) || n > (p.Max ?? int.MaxValue))
                {
                    return Fail("V2.RANGE", $"{n} not in {p.Min}..{p.Max}");
                }

                break;

            default:
                return Fail("V2.TYPE_MISMATCH", $"알 수 없는 파라미터 타입 '{p.Type}'");
        }

        return ValidationResult.Ok;
    }

    // ---------------------------------------------------------------- 완료 조건
    private const string ValidPlan = """
        { "schema": 1, "goal": "forge_and_sell", "loop": true, "on_step_fail": "fallback",
          "steps": [
            { "action": "MoveTo", "args": { "poi": "$nearest_field", "speed": "walk" }, "timeout_s": 300 },
            { "action": "Gather", "args": { "resource": "iron_ore", "count": 6 }, "timeout_s": 900 },
            { "action": "MoveTo", "args": { "poi": "$workplace" }, "timeout_s": 300 },
            { "action": "Craft", "args": { "recipe": "iron_sword", "count": 2 }, "timeout_s": 1800 },
            { "action": "MoveTo", "args": { "poi": "$home" }, "timeout_s": 300 },
            { "action": "Sleep", "args": { "until_time": "Dawn" } }
          ] }
        """;

    /// <summary>`spike validate` — 실패 코드마다 유발 픽스처를 하나씩 돌린다.</summary>
    public static int Run()
    {
        var c = SchemaGen.Load();

        (string Name, string Json, string Expected, string? Archetype)[] cases =
        [
            ("정상 플랜", ValidPlan, "", "blacksmith"),
            ("액션명 오타", ValidPlan.Replace("\"Gather\"", "\"Gathr\""), "V2.UNKNOWN_ACTION", "blacksmith"),
            ("미정의 POI", ValidPlan.Replace("\"$nearest_field\"", "\"$mine_shaft\""), "V2.UNKNOWN_POI", "blacksmith"),
            ("필수 인자 누락", ValidPlan.Replace("\"recipe\": \"iron_sword\", ", ""), "V2.MISSING_REQUIRED_ARG", "blacksmith"),
            ("깨진 JSON", ValidPlan[..^12], "V1.PARSE", "blacksmith"),
            ("스텝 2개", """
                { "schema": 1, "goal": "nap", "loop": false,
                  "steps": [ { "action": "Rest", "args": {} }, { "action": "Wait", "args": {} } ] }
                """, "V1.STEP_COUNT", "blacksmith"),
            ("루트 여분 필드", ValidPlan.Replace("\"schema\": 1,", "\"schema\": 1, \"npc_id\": 42,"), "V1.EXTRA_FIELD", "blacksmith"),
            ("스텝 여분 필드", ValidPlan.Replace("\"action\": \"Gather\",", "\"action\": \"Gather\", \"note\": \"hi\","), "V1.EXTRA_FIELD", "blacksmith"),
            ("타입 불일치", ValidPlan.Replace("\"count\": 6", "\"count\": \"many\""), "V2.TYPE_MISMATCH", "blacksmith"),
            ("범위 초과", ValidPlan.Replace("\"count\": 6", "\"count\": 999"), "V2.RANGE", "blacksmith"),
            ("정의되지 않은 인자", ValidPlan.Replace("\"resource\": \"iron_ore\",", "\"resource\": \"iron_ore\", \"hurry\": true,"), "V2.UNKNOWN_ARG", "blacksmith"),
            ("미정의 아이템", ValidPlan.Replace("\"iron_ore\"", "\"mithril\""), "V2.UNKNOWN_ITEM", "blacksmith"),
            ("아키타입 미허용 액션", ValidPlan.Replace(
                "{ \"action\": \"Gather\", \"args\": { \"resource\": \"iron_ore\", \"count\": 6 }, \"timeout_s\": 900 }",
                "{ \"action\": \"Guard\", \"args\": { \"poi\": \"$gate\", \"duration_s\": 600 }, \"timeout_s\": 900 }"),
                "V2.ACTION_NOT_ALLOWED", "blacksmith"),
            ("enum 값 오류", ValidPlan.Replace("\"speed\": \"walk\"", "\"speed\": \"sprint\""), "V2.TYPE_MISMATCH", "blacksmith"),
            ("npc_ref 형식 오류", ValidPlan.Replace(
                "{ \"action\": \"Sleep\", \"args\": { \"until_time\": \"Dawn\" } }",
                "{ \"action\": \"Talk\", \"args\": { \"npc\": \"Gareth the Smith\" } }"),
                "V2.TYPE_MISMATCH", "blacksmith"),
        ];

        var pass = 0;
        foreach (var (name, json, expected, archetype) in cases)
        {
            var r = Check(json, c, archetype);
            var actual = r.IsOk ? "" : r.Code;
            var ok = actual == expected;
            pass += ok ? 1 : 0;
            var want = expected.Length == 0 ? "OK" : expected;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL"),-4} {name,-22} expected {want,-24} got {r}");
        }

        Console.WriteLine($"\n{pass}/{cases.Length} 통과");
        return pass == cases.Length ? 0 : 1;
    }
}
