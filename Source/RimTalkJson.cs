using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace RimInfluence;

internal static class RimTalkJson
{
    private static readonly Type JsonType = AccessTools.TypeByName("RimTalk.Util.JsonUtil");
    private static readonly MethodInfo ParseMethod = JsonType?.GetMethod("ParseJsonValue", BindingFlags.Public | BindingFlags.Static);
    private static readonly MethodInfo SerializeMethod = JsonType?.GetMethod("SerializeJsonValue", BindingFlags.Public | BindingFlags.Static);

    public static IDictionary<string, object> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || ParseMethod == null) return null;
        object[] parameters = { json, 0 };
        return ParseMethod.Invoke(null, parameters) as IDictionary<string, object>;
    }

    public static string Serialize(object value)
    {
        return SerializeMethod?.Invoke(null, new[] { value, (object)false }) as string ?? "";
    }

    public static IDictionary<string, object> Object(object value) => value as IDictionary<string, object>;

    public static string String(IDictionary<string, object> value, string key)
    {
        return value != null && value.TryGetValue(key, out object result) ? result?.ToString() ?? "" : "";
    }
}
