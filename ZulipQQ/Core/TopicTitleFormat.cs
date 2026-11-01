using System;
using System.Text.RegularExpressions;

namespace ZulipQQ.Core;

/// <summary>
/// 话题会话的显示标题模板（设置 > 群消息设置 > 显示）。可用变量 <c>{Channel}</c>（频道名）与 <c>{Topic}</c>（话题名，
/// 有备注时为备注），不区分大小写；其它文字原样保留。
/// </summary>
public static partial class TopicTitleFormat
{
    /// <summary>默认模板：只显示话题名。</summary>
    public const string Default = "{Topic}";

    /// <summary>
    /// 按模板生成标题。一次扫描替换，频道名、话题名中恰好含有变量文字时不会被再次替换。
    /// 模板为空白，或结果为空白时，返回话题名。
    /// </summary>
    public static string Format(string? template, string channel, string topic)
    {
        if (string.IsNullOrWhiteSpace(template))
            return topic;
        var result = Variable().Replace(template, m =>
            m.Groups[1].Value.Equals("Channel", StringComparison.OrdinalIgnoreCase) ? channel : topic).Trim();
        return result.Length == 0 ? topic : result;
    }

    [GeneratedRegex(@"\{(Channel|Topic)\}", RegexOptions.IgnoreCase)]
    private static partial Regex Variable();
}
