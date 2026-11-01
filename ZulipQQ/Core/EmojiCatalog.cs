using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ZulipQQ.Core;

/// <summary>
/// 一个表情。Unicode 表情的 <see cref="Code"/> 是码点（如 <c>1f44d</c>），<see cref="Text"/> 是字符；
/// 组织自定义表情的 <see cref="Code"/> 是表情 id，<see cref="ImageUrl"/> 是图片地址。
/// <see cref="Type"/> 即表情回应的 reaction_type。
/// </summary>
public sealed record EmojiEntry(string Name, string Code, string Type, IReadOnlyList<string> Aliases, string? Text, string? ImageUrl)
{
    public const string UnicodeType = "unicode_emoji";
    public const string RealmType = "realm_emoji";
}

/// <summary>
/// 表情目录：组织自定义表情在前，然后是 Unicode 表情（按服务器数据中的顺序）。名称和别名都可以查找，
/// 组织自定义表情与 Unicode 表情重名时以自定义表情为准（与 Zulip 一致）。
/// </summary>
public sealed class EmojiCatalog
{
    private readonly Dictionary<string, EmojiEntry> _byName = new(StringComparer.Ordinal);

    public EmojiCatalog(IEnumerable<EmojiEntry> realm, IEnumerable<EmojiEntry> unicode)
    {
        var realmList = realm.ToList();
        All = [.. realmList, .. unicode];
        // 先放 Unicode，再用自定义表情覆盖同名项
        foreach (var e in All.Where(e => e.Type == EmojiEntry.UnicodeType))
        {
            foreach (var name in e.Aliases.Prepend(e.Name))
                _byName.TryAdd(name, e);
        }
        foreach (var e in realmList)
            _byName[e.Name] = e;
    }

    public static EmojiCatalog Empty { get; } = new([], []);

    public IReadOnlyList<EmojiEntry> All { get; }

    /// <summary>按名称或别名查找。</summary>
    public EmojiEntry? TryGet(string name) => _byName.GetValueOrDefault(name);

    /// <summary>
    /// 搜索：名称或别名包含关键字（忽略大小写，可带开头的冒号）。名称或别名以关键字开头的排在前面，
    /// 其余保持目录顺序。关键字为空时返回全部。
    /// </summary>
    public IReadOnlyList<EmojiEntry> Search(string query)
    {
        var q = query.Trim().TrimStart(':');
        if (q.Length == 0)
            return All;
        var prefix = new List<EmojiEntry>();
        var contains = new List<EmojiEntry>();
        foreach (var e in All)
        {
            var names = e.Aliases.Prepend(e.Name).ToList();
            if (names.Exists(n => n.StartsWith(q, StringComparison.OrdinalIgnoreCase)))
                prefix.Add(e);
            else if (names.Exists(n => n.Contains(q, StringComparison.OrdinalIgnoreCase)))
                contains.Add(e);
        }
        return [.. prefix, .. contains];
    }

    /// <summary>
    /// 解析 <c>server_emoji_data_url</c> 的内容：<c>{"code_to_names": {"1f44d": ["+1", "thumbs_up", …]}}</c>，
    /// 第一个名字是规范名，其余为别名。无法转换为字符的码点跳过。
    /// </summary>
    public static IReadOnlyList<EmojiEntry> ParseUnicode(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new List<EmojiEntry>();
        if (!doc.RootElement.TryGetProperty("code_to_names", out var map) || map.ValueKind != JsonValueKind.Object)
            return result;
        foreach (var p in map.EnumerateObject())
        {
            var names = p.Value.ValueKind == JsonValueKind.Array
                ? p.Value.EnumerateArray().Select(n => n.GetString()).OfType<string>().ToList()
                : [];
            if (names.Count == 0 || UnicodeFromCode(p.Name) is not { } text)
                continue;
            result.Add(new EmojiEntry(names[0], p.Name, EmojiEntry.UnicodeType, names.Skip(1).ToList(), text, null));
        }
        // 数据按码点排序，键帽和国旗（Windows 上显示为字母）会排在最前面；按区块调整为表情脸在前
        return [.. result.OrderBy(e => BlockRank(e.Code))];
    }

    // 数据中没有分类，按首个码点所在的 Unicode 区块排序（OrderBy 是稳定排序，区块内保持原顺序）
    private static int BlockRank(string code)
    {
        var dash = code.IndexOf('-');
        if (!int.TryParse(dash < 0 ? code : code[..dash], System.Globalization.NumberStyles.HexNumber, null, out var cp))
            return 7;
        return cp switch
        {
            >= 0x1F600 and <= 0x1F64F => 0, // 表情脸
            >= 0x1F900 and <= 0x1F9FF => 1, // 补充表情与人物
            >= 0x1F440 and <= 0x1F4FF => 2, // 手势、人物、物品
            >= 0x1F300 and <= 0x1F43F => 3, // 自然、天气、动物、食物
            >= 0x1F680 and <= 0x1F6FF or >= 0x1FA70 and <= 0x1FAFF => 4, // 交通、物品
            >= 0x1F1E6 and <= 0x1F1FF or < 0x2000 => 7, // 国旗、键帽、© ®
            >= 0x2600 and <= 0x27BF => 5, // 杂项符号
            _ => 6,
        };
    }

    /// <summary>把 <c>1f469-200d-1f4bb</c> 这样的码点串转为字符；无法解析时返回 null。</summary>
    public static string? UnicodeFromCode(string code) => ZulipHtml.EmojiFromClass("emoji-" + code);
}
