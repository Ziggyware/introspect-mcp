using System.Linq;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System;
using System.IO;
using System.Collections.Generic;
using System.Collections;

// Pure, MCP-SDK-independent gate: true iff BuildCallableDelegate must be used instead
// of a plain method.CreateDelegate(...) fast path. Split out on its own so the decision
// itself — not just the IL it triggers — is directly unit-testable.
public static class AdaptationDecision
{
    public static bool NeedsAdaptation(MethodInfo method) =>
        method.GetParameters().Any(p => ParameterAdapterRegistry.NeedsAdapter(p.ParameterType))
        || ReturnAdapterRegistry.NeedsAdapter(method.ReturnType);
}
public static class UnrepresentableTypeRegistry
{
    private static readonly Type[] BlockedByAssignability =
    {
        typeof(Stream), typeof(TextWriter), typeof(TextReader),
        typeof(System.Runtime.InteropServices.SafeHandle),
    };

    public static bool IsUnrepresentable(Type t) =>
        t == typeof(object) ||
        BlockedByAssignability.Any(b => b.IsAssignableFrom(t));

    public static bool MethodIsUnrepresentable(MethodInfo m) =>
        IsUnrepresentable(m.ReturnType) || m.GetParameters().Any(p => IsUnrepresentable(p.ParameterType));
}


public static class ReturnAdapterRegistry
{
    private static readonly List<(Type BaseType, Func<object, object> Adapt)> Adapters = new()
    {
        (typeof(Encoding), e => ((Encoding)e).WebName),
    };

    // Cache resolved lookups — IsAssignableFrom scan is O(n) per distinct type,
    // and this runs once per method during tool registration, not per-call, but
    // caching costs nothing and protects against future hot-path use.
    private static readonly Dictionary<Type, Func<object, object>?> Cache = new();

    public static bool NeedsAdapter(Type t) => Resolve(t) != null;

    public static object Adapt(Type t, object value) =>
        (Resolve(t) ?? throw new InvalidOperationException($"No return adapter for '{t}'."))(value);

    private static Func<object, object>? Resolve(Type t)
    {
        if (Cache.TryGetValue(t, out var cached)) return cached;
        var match = Adapters.FirstOrDefault(a => a.BaseType.IsAssignableFrom(t)).Adapt;
        Cache[t] = match;
        return match;
    }
}
public static class ParameterAdapterRegistry
{
    private static readonly List<(Type BaseType, Func<string, object> Parse, Func<object> Fallback)> Adapters = new()
    {
        (typeof(Encoding), s => Encoding.GetEncoding(s), () => Encoding.UTF8),
        (typeof(CultureInfo), s => CultureInfo.GetCultureInfo(s), () => CultureInfo.InvariantCulture),
    };

    private static readonly Dictionary<Type, (Func<string, object> Parse, Func<object> Fallback)?> Cache = new();

    public static bool NeedsAdapter(Type t) => Resolve(t) != null;

    public static object Adapt(Type t, string? value)
    {
        var entry = Resolve(t) ?? throw new InvalidOperationException($"No parameter adapter for '{t}'.");
        return string.IsNullOrEmpty(value) ? entry.Fallback() : entry.Parse(value);
    }

    private static (Func<string, object> Parse, Func<object> Fallback)? Resolve(Type t)
    {
        if (Cache.TryGetValue(t, out var cached)) return cached;
        var match = Adapters.Where(a => a.BaseType.IsAssignableFrom(t))
            .Select(a => ((Func<string, object>, Func<object>)?)(a.Parse, a.Fallback))
            .FirstOrDefault();
        Cache[t] = match;
        return match;
    }
}

public static class AdapterBuilder
{
    public static Delegate BuildCallableDelegate(MethodInfo method)
    {
        var originalParams = method.GetParameters();
        if (originalParams.Any(p => p.ParameterType.IsByRef))
            throw new NotSupportedException($"{method.DeclaringType!.Name}.{method.Name}: ref/out parameters not yet supported.");

        var needsParamAdaptation = originalParams.Any(p => ParameterAdapterRegistry.NeedsAdapter(p.ParameterType));
        var needsReturnAdaptation = ReturnAdapterRegistry.NeedsAdapter(method.ReturnType);

        if (!needsParamAdaptation && !needsReturnAdaptation)
            return method.CreateDelegate(Expression.GetDelegateType(
                originalParams.Select(p => p.ParameterType).Append(method.ReturnType).ToArray()));

        var exposedParamTypes = originalParams
            .Select(p => ParameterAdapterRegistry.NeedsAdapter(p.ParameterType) ? typeof(string) : p.ParameterType)
            .ToArray();
        var effectiveReturnType = needsReturnAdaptation ? typeof(string) : method.ReturnType;

        var dm = new DynamicMethod(
            $"{method.DeclaringType!.Name}_{method.Name}_Adapted",
            effectiveReturnType, exposedParamTypes, typeof(AdapterBuilder).Module, skipVisibility: true);

        for (int i = 0; i < originalParams.Length; i++)
            dm.DefineParameter(i + 1, ParameterAttributes.None,
                string.IsNullOrEmpty(originalParams[i].Name) ? $"param{i}" : originalParams[i].Name);

        var il = dm.GetILGenerator();
        var paramAdapt = typeof(ParameterAdapterRegistry).GetMethod(nameof(ParameterAdapterRegistry.Adapt))!;
        var returnAdapt = typeof(ReturnAdapterRegistry).GetMethod(nameof(ReturnAdapterRegistry.Adapt))!;

        for (int i = 0; i < originalParams.Length; i++)
        {
            if (ParameterAdapterRegistry.NeedsAdapter(originalParams[i].ParameterType))
            {
                il.Emit(OpCodes.Ldtoken, originalParams[i].ParameterType);
                il.Emit(OpCodes.Call, typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle))!);
                il.Emit(OpCodes.Ldarg, i);
                il.Emit(OpCodes.Call, paramAdapt);
                il.Emit(OpCodes.Unbox_Any, originalParams[i].ParameterType);
            }
            else il.Emit(OpCodes.Ldarg, i);
        }
        il.Emit(OpCodes.Call, method);

        if (needsReturnAdaptation)
        {
            // Box the real return value into a local FIRST, so Adapt(Type, object)
            // receives its two args in the correct stack order (type-token, then value).
            if (method.ReturnType.IsValueType)
                il.Emit(OpCodes.Box, method.ReturnType);
            var boxedLocal = il.DeclareLocal(typeof(object));
            il.Emit(OpCodes.Stloc, boxedLocal);
            il.Emit(OpCodes.Ldtoken, method.ReturnType);
            il.Emit(OpCodes.Call, typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle))!);
            il.Emit(OpCodes.Ldloc, boxedLocal);
            il.Emit(OpCodes.Call, returnAdapt);
            il.Emit(OpCodes.Castclass, typeof(string));
        }

        il.Emit(OpCodes.Ret);
        var delegateType = Expression.GetDelegateType(exposedParamTypes.Append(effectiveReturnType).ToArray());
        return dm.CreateDelegate(delegateType);
    }
}
