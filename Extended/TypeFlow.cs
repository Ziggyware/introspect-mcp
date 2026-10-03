namespace IntrospectMcp.Extended;

public sealed class TypeFlow
{
    private readonly AssemblyCatalog _catalog;
    private readonly TypeModel _model;
    private readonly MemberDescriptor[] _members;
    public TypeFlow(AssemblyCatalog catalog, TypeModel model)
    { (_catalog, _model) = (catalog, model); _members = catalog.Members.Where(m => m.Unsupported is null && !m.Method.IsGenericMethodDefinition).ToArray(); }
    public static IEnumerable<(string Name, Type Type)> Inputs(MemberDescriptor m)
    {
        if (m.NeedsTarget) yield return ("target", m.Method.DeclaringType!);
        foreach (var p in m.Method.GetParameters().Where(p => !(p.IsOut && !p.IsIn) && !CallScope.IsInjected(p.ParameterType)))
            yield return (JsonSupport.ParameterName(p), JsonSupport.ValueType(p.ParameterType));
    }
    public static IEnumerable<(string? Name, Type Type)> Outputs(MemberDescriptor m)
    {
        if (m.ReturnType != typeof(void)) yield return (null, m.ReturnType);
        foreach (var p in m.Method.GetParameters().Where(p => p.ParameterType.IsByRef && !p.IsIn)) yield return (JsonSupport.ParameterName(p), JsonSupport.ValueType(p.ParameterType));
    }
    public JsonObject Hints(Type type) => new()
    {
        ["producers"] = new JsonArray(_members.SelectMany(m => Outputs(m).Where(o => type.IsAssignableFrom(o.Type)).Select(o => (JsonNode)new JsonObject { ["method"] = m.Name, ["output"] = o.Name, ["type"] = JsonSupport.TypeName(o.Type) })).Take(3).ToArray()),
        ["consumers"] = new JsonArray(_members.Where(m => Inputs(m).Any(p => p.Type.IsAssignableFrom(type))).Take(3).Select(m => (JsonNode)new JsonObject { ["method"] = m.Name, ["signature"] = m.Signature }).ToArray())
    };
    private sealed record Available(Type Type, string Step, string? Output);
    private sealed record State(List<JsonObject> Steps, List<Available> Values, HashSet<string> Used);
    public JsonObject Plans(Type desired, int maxDepth)
    {
        var queue = new Queue<State>(); queue.Enqueue(new(new(), new(), new(StringComparer.Ordinal)));
        var plans = new JsonArray(); int expanded = 0;
        var visits = new Dictionary<string, int>(StringComparer.Ordinal);
        while (queue.Count > 0 && plans.Count < 3 && expanded++ < 2048)
        {
            var state = queue.Dequeue();
            if (state.Steps.Count >= maxDepth) continue;
            foreach (var member in _members)
            {
                if (state.Used.Contains(member.Name)) continue;
                var outputs = Outputs(member).ToArray();
                if (outputs.Length == 0) continue;
                var args = Arguments(member, state.Values);
                if (args is null) continue;
                string stepId = "step" + (state.Steps.Count + 1);
                var next = new State(new(state.Steps), new(state.Values), new(state.Used, StringComparer.Ordinal));
                next.Steps.Add(new() { ["id"] = stepId, ["method"] = member.Name, ["arguments"] = args });
                next.Used.Add(member.Name);
                next.Values.AddRange(outputs.Select(o => new Available(o.Type, stepId, o.Name)));
                var matched = outputs.FirstOrDefault(o => desired.IsAssignableFrom(o.Type));
                if (matched.Type is not null)
                {
                    var result = new JsonObject { ["$step"] = stepId }; if (matched.Name is not null) result["output"] = matched.Name;
                    plans.Add(new JsonObject { ["tool"] = "pipeline", ["arguments"] = new JsonObject { ["steps"] = new JsonArray(next.Steps.Select(s => (JsonNode)s.DeepClone()).ToArray()) },
                        ["resultReference"] = result, ["description"] = "Ready to run with synthetic literal samples. Review side effects and replace example values before use." });
                    if (plans.Count >= 3) break;
                }
                string key = string.Join("|", next.Values.Select(v => JsonSupport.TypeName(v.Type)).Distinct().OrderBy(x => x, StringComparer.Ordinal));
                visits.TryGetValue(key, out int count);
                if (count < 3 && queue.Count < 2048) { visits[key] = count + 1; queue.Enqueue(next); }
            }
        }
        return new() { ["type"] = JsonSupport.TypeName(desired), ["maxDepth"] = maxDepth, ["plans"] = plans, ["truncated"] = expanded >= 2048,
            ["reason"] = plans.Count == 0 ? "no_literal_source_plan_within_depth" : null, ["hints"] = Hints(desired) };
    }
    private JsonObject? Arguments(MemberDescriptor member, IReadOnlyList<Available> available)
    {
        var args = new JsonObject(); bool usesPrior = false;
        foreach (var (name, type) in Inputs(member))
        {
            var input = available.LastOrDefault(v => type.IsAssignableFrom(v.Type));
            if (input is not null)
            {
                var reference = new JsonObject { ["$step"] = input.Step }; if (input.Output is not null) reference["output"] = input.Output;
                args[name] = reference; usesPrior = true; continue;
            }
            var parameter = member.Method.GetParameters().FirstOrDefault(p => p.Name == name);
            if (parameter is not null && (parameter.HasDefaultValue || _model.Nullable(parameter))) continue;
            try { args[name] = _model.Sample(type); }
            catch { return null; }
        }
        // A longer path must actually depend on its preceding values, not append unrelated sources.
        return available.Count > 0 && !usesPrior ? null : args;
    }
}
