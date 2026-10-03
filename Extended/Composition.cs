using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace IntrospectMcp.Extended;

public sealed partial class InvocationEngine
{
    public bool CompactMode { get; private set; }
    public TypeFlow Flow => _flow ??= new(Catalog, Model);
    private TypeFlow? _flow;
    partial void AddCompositionTools(List<McpServerTool> tools)
    {
        tools.Add(Tool("pipeline", "Execute up to 32 ordered method calls. {$step:id} passes the LIVE result of an earlier step, optionally with path or output. Stops on first error, retaining completed results. dryRun validates bindings without calling target methods, constructors, or getters; it is not a transaction or a guarantee of runtime success.",
            new() { ["steps"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 32, ["items"] = JsonSupport.ObjectSchema(new() { ["id"] = JsonSupport.StringSchema("Unique step ID"), ["method"] = JsonSupport.StringSchema("Exact method/constructor name"), ["arguments"] = new JsonObject { ["type"] = "object" } }, "id", "method") },
                ["dryRun"] = new JsonObject { ["type"] = "boolean", ["default"] = false } }, Pipeline, "steps"));
        tools.Add(Tool("how_to_get", "Breadth-first type-flow search for up to 3 ready-to-run pipelines, starting with synthetic literal inputs. Does not invoke methods. Inspect side effects before executing a plan.",
            new() { ["type"] = JsonSupport.StringSchema(), ["maxDepth"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 6, ["default"] = 3 } },
            (a, s) => Task.FromResult<JsonNode?>(Flow.Plans(Catalog.ResolveType(JsonSupport.RequiredString(a, "type")), JsonSupport.Integer(a, "maxDepth", 3, 1, 6))), "type"));
        tools.Add(Tool("type_flow", "Top three producers and consumers for a CLR type; assignability-aware, including ref/out outputs.", new() { ["type"] = JsonSupport.StringSchema() },
            (a, s) => Task.FromResult<JsonNode?>(Flow.Hints(Catalog.ResolveType(JsonSupport.RequiredString(a, "type")))), "type"));
        tools.Add(Tool("session_info", "Start here: calling conventions, capabilities, budgets, coverage and lifecycle controls for this managed-DLL session.", new(), (a, s) => Task.FromResult<JsonNode?>(new JsonObject
        {
            ["mode"] = "extended", ["discovery"] = CompactMode ? "compact" : "individual", ["assembly"] = Catalog.Assembly.GetName().Name,
            ["workflow"] = JsonSupport.Node(new[] { "list_types -> describe_type -> invoke_method", "how_to_get -> pipeline(dryRun:true) -> pipeline", "list_handles -> release_handle" }),
            ["references"] = JsonSupport.Node(new[] { "{$handle:id}", "{$ref:id,path:Property[0].Name}", "{$type:ConcreteClrType,...data}", "{$method:ExactMethodName,target:{$handle:id}}", "{$step:earlierStep,path:Property,output:optionalRefOutName}" }),
            ["limits"] = new JsonObject { ["toolBudget"] = Options.ToolBudget, ["schemaTokenBudget"] = Options.SchemaTokenBudget, ["pageSize"] = Options.PageSize, ["maxHandles"] = Options.MaxHandles, ["ttlSeconds"] = Options.HandleTtl.TotalSeconds, ["maxPipelineSteps"] = 32 },
            ["warnings"] = JsonSupport.Node(new[] { "Trusted DLLs only: this is not a sandbox.", "No expression evaluation, native DLL loading, or network transport added.", "Cancellation is cooperative; dryRun cannot predict user-code failures.", "DTO load-time self-tests execute constructors/setters/getters." }),
            ["coverage"] = Coverage()
        })));
        var methodNames = Catalog.Members.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        var direct = tools.Where(t => methodNames.Contains(t.ProtocolTool.Name)).ToArray();
        long estimatedTokens = direct.Sum(t => (long)(t.ProtocolTool.InputSchema.GetRawText().Length + (t.ProtocolTool.Description?.Length ?? 0)) / 4);
        CompactMode = CompactMode || direct.Length > Options.ToolBudget || estimatedTokens > Options.SchemaTokenBudget;
        if (CompactMode) tools.RemoveAll(t => methodNames.Contains(t.ProtocolTool.Name));
    }
    private async Task<JsonNode?> Pipeline(JsonObject args, CallScope scope)
    {
        if (args["steps"] is not JsonArray steps || steps.Count is < 1 or > 32) throw new BindingException("invalid_pipeline", "Provide 1..32 ordered steps.", "steps");
        bool dryRun = args["dryRun"]?.GetValue<bool>() ?? false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var parsed = new List<(string Id, MemberDescriptor Member, JsonObject Arguments)>();
        foreach (var (raw, i) in steps.Select((n, i) => (n, i)))
        {
            var step = JsonSupport.Object(raw, $"steps[{i}]");
            string id = JsonSupport.RequiredString(step, "id");
            if (!Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) || !ids.Add(id))
                throw new BindingException("invalid_step_id", "Step IDs must be unique, 1..64 letters, digits, '_' or '-'.", $"steps[{i}].id");
            if (step.Any(p => p.Key is not "id" and not "method" and not "arguments")) throw new BindingException("unknown_argument", "Step accepts id, method and arguments only.", $"steps[{i}]");
            parsed.Add((id, Catalog.Find(JsonSupport.RequiredString(step, "method")), step["arguments"] is null ? new() : JsonSupport.Object(step["arguments"], $"steps[{i}].arguments")));
        }
        var completed = new JsonArray();
        foreach (var step in parsed)
        {
            await using var child = NewScope(scope.Cancellation);
            child.DryRun = dryRun; child.RetainIn = scope;
            foreach (var prior in scope.Steps) child.Steps.Add(prior.Key, prior.Value);
            foreach (var prior in scope.StepOutputs) child.StepOutputs.Add(prior.Key, prior.Value);
            try
            {
                var result = await InvokeAsync(step.Member, step.Arguments, child);
                scope.Steps.Add(step.Id, child.LiveResult);
                scope.StepOutputs.Add(step.Id, new(child.LiveOutputs));
                result["id"] = step.Id; result["method"] = step.Member.Name; completed.Add(result);
            }
            catch (Exception e)
            {
                var result = Error(e, scope.Id);
                result["failedStep"] = step.Id; result["completed"] = completed.Count; result["steps"] = completed; result["dryRun"] = dryRun;
                result["sideEffectsRolledBack"] = false;
                return result;
            }
        }
        return new JsonObject { ["callId"] = scope.Id, ["ok"] = true, ["dryRun"] = dryRun, ["completed"] = completed.Count, ["steps"] = completed, ["result"] = completed.Last()?["result"]?.DeepClone() };
    }
}
