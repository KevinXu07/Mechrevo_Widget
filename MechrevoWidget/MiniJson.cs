using System.Globalization;
using System.Text;

namespace MechrevoWidget;

/// <summary>
/// 极简 JSON 解析器(只读)。本项目只需要从扁平对象里取字段,
/// 不值得为此引入 System.Text.Json 的一串依赖 DLL。
/// 对象 → Dictionary&lt;string, object?&gt;,数组 → List&lt;object?&gt;,
/// 字符串 → string,数字 → 原文 string(保留 "145" 这种写法),true/false → bool,null → null。
/// </summary>
internal static class MiniJson
{
    public static Dictionary<string, object?>? ParseObject(string json)
    {
        try
        {
            int i = 0;
            return ParseValue(json.TrimStart('﻿'), ref i) as Dictionary<string, object?>;
        }
        catch { return null; }
    }

    /// <summary>取字段并统一成字符串:字符串原样,数字取原文,其它返回 null。</summary>
    public static string? GetString(this Dictionary<string, object?> obj, string key) =>
        obj.TryGetValue(key, out var v) && v is string s ? s : null;

    private static object? ParseValue(string s, ref int i)
    {
        SkipWs(s, ref i);
        if (i >= s.Length) throw new FormatException("unexpected end");
        char c = s[i];
        switch (c)
        {
            case '{': return ParseObj(s, ref i);
            case '[': return ParseArr(s, ref i);
            case '"': return ParseStr(s, ref i);
            case 't': Expect(s, ref i, "true"); return true;
            case 'f': Expect(s, ref i, "false"); return false;
            case 'n': Expect(s, ref i, "null"); return null;
            default: return ParseNum(s, ref i);
        }
    }

    private static Dictionary<string, object?> ParseObj(string s, ref int i)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        i++; // {
        SkipWs(s, ref i);
        if (s[i] == '}') { i++; return d; }
        while (true)
        {
            SkipWs(s, ref i);
            var key = ParseStr(s, ref i);
            SkipWs(s, ref i);
            if (s[i++] != ':') throw new FormatException("expected ':'");
            d[key] = ParseValue(s, ref i);
            SkipWs(s, ref i);
            char c = s[i++];
            if (c == '}') return d;
            if (c != ',') throw new FormatException("expected ',' or '}'");
        }
    }

    private static List<object?> ParseArr(string s, ref int i)
    {
        var list = new List<object?>();
        i++; // [
        SkipWs(s, ref i);
        if (s[i] == ']') { i++; return list; }
        while (true)
        {
            list.Add(ParseValue(s, ref i));
            SkipWs(s, ref i);
            char c = s[i++];
            if (c == ']') return list;
            if (c != ',') throw new FormatException("expected ',' or ']'");
        }
    }

    private static string ParseStr(string s, ref int i)
    {
        if (s[i] != '"') throw new FormatException("expected string");
        i++;
        var sb = new StringBuilder();
        while (true)
        {
            char c = s[i++];
            if (c == '"') return sb.ToString();
            if (c != '\\') { sb.Append(c); continue; }
            char e = s[i++];
            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'u':
                    sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 4;
                    break;
                default: sb.Append(e); break; // \" \\ \/
            }
        }
    }

    private static string ParseNum(string s, ref int i)
    {
        int start = i;
        while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
        if (i == start) throw new FormatException($"unexpected '{s[i]}'");
        return s.Substring(start, i - start);
    }

    private static void Expect(string s, ref int i, string word)
    {
        if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) throw new FormatException("bad literal");
        i += word.Length;
    }

    private static void SkipWs(string s, ref int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
    }
}
