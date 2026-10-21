using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ZulipQQ.Zulip;

/// <summary>Zulip 请求参数。GET 时拼到查询串，其它方法以 x-www-form-urlencoded 发送。</summary>
public sealed class FormBuilder
{
    private readonly List<KeyValuePair<string, string>> _pairs = [];

    public IEnumerable<KeyValuePair<string, string>> Pairs => _pairs;

    public FormBuilder Add(string name, string value)
    {
        _pairs.Add(new(name, value));
        return this;
    }

    public FormBuilder Add(string name, long value) => Add(name, value.ToString(CultureInfo.InvariantCulture));

    public FormBuilder Add(string name, bool value) => Add(name, value ? "true" : "false");

    /// <summary>值按 Zulip 的 JSON 规则序列化后写入（数组、对象、需要 JSON 编码的字符串等）。</summary>
    public FormBuilder AddJson(string name, object value) => Add(name, ZulipJson.Serialize(value));

    public string ToQueryString()
    {
        var sb = new StringBuilder();
        foreach (var (name, value) in _pairs)
        {
            if (sb.Length > 0)
                sb.Append('&');
            sb.Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value));
        }
        return sb.ToString();
    }
}
