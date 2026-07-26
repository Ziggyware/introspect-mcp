using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System;
// Assigns MCP tool names, disambiguating overload sets that would otherwise collide
// under the "{Type.Name}_{Method.Name}" convention.
public static class ToolNaming
{
    public static string BaseName(Type declaringType, MethodInfo method) => $"{declaringType.Name}_{method.Name}";


    private static string SanitizeToolNameSegment(string raw)
    {
        // Replace any char outside [A-Za-z0-9_.-] with '_', collapse repeats, trim to 128.
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' ? c : '_');
        }
        var result = Regex.Replace(sb.ToString(), "_{2,}", "_").Trim('_');
        return result.Length == 0 ? "Tool" : result;
    }

    public static IReadOnlyDictionary<MethodInfo, string> AssignNames(IEnumerable<(Type Type, MethodInfo Method)> methods)
    {
        var result = new Dictionary<MethodInfo, string>();

        foreach (var group in methods.GroupBy(m => BaseName(m.Type, m.Method)))
        {
            try
            {
                var list = group.ToList();
                if (list.Count == 1)
                {
                    result[list[0].Method] = group.Key;
                    continue;
                }
                foreach (var (type, method) in list)
                {
                    var typeName = SanitizeToolNameSegment(type.Name);
                    var methodName = SanitizeToolNameSegment(method.Name);
                    if (methodName.Contains("ToBase64"))
                    {
                    }
                    var paramSig = string.Join("_", method.GetParameters()
                        .Select(p => SanitizeToolNameSegment(p.ParameterType.Name)));

                    var candidate = $"{typeName}_{methodName}_{paramSig}";
                    if (candidate.Length > 128) candidate = candidate[..128].TrimEnd('_');

                    var suffix = string.Join("_", method.GetParameters().Select(p => p.ParameterType.Name));
                    result[method] = string.IsNullOrEmpty(suffix) ? group.Key : $"{group.Key}_{suffix}";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message + "\r\n" + (ex.StackTrace ?? ""));
            }
        }
        return result;
    }
}
