using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ZulipQQ.Core;

/// <summary>
/// 代码块语言补全：在 <c>```</c>（或 <c>~~~</c>）开头的行末输入时，列出服务器支持的语言（Pygments 的常用别名）
/// 与 Zulip 的特殊块（quote、spoiler、math），按输入的文字搜索。
/// </summary>
public static partial class CodeLanguages
{
    /// <summary>按常用程度排列（与网页版补全的优先顺序大致相同）。</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        "python", "javascript", "typescript", "java", "c", "cpp", "csharp", "go", "rust", "kotlin",
        "swift", "ruby", "php", "html", "css", "json", "yaml", "xml", "sql", "bash",
        "shell", "console", "powershell", "text", "markdown", "diff", "dockerfile", "makefile", "ini", "toml",
        "quote", "spoiler", "math",
        "scss", "sass", "less", "jsx", "tsx", "vue", "graphql", "lua", "perl", "r", "scala",
        "haskell", "elixir", "erlang", "clojure", "dart", "objective-c", "matlab", "groovy", "nginx", "vim",
        "latex", "protobuf", "cmake", "fsharp", "ocaml", "julia", "zig", "nim", "http", "apache",
        "batch", "vbnet", "nasm", "solidity", "hcl", "coffeescript", "d", "fortran", "pascal", "prolog",
        "scheme", "common-lisp", "tex", "properties", "csv", "js", "ts", "py", "sh", "cs",
    ];

    /// <summary>
    /// 光标位于开头围栏行（<see cref="MdLineKind.FenceOpen"/>）的末尾时，取出正在输入的语言：
    /// <paramref name="start"/> 为语言的起点，<paramref name="query"/> 为已输入的部分（可以为空）。
    /// </summary>
    public static bool TryGetQuery(string text, int caret, MarkdownModel model, out int start, out string query)
    {
        start = -1;
        query = "";
        if (caret < 0 || caret > text.Length || model.Lines.Count == 0)
            return false;
        var lineStart = caret == 0 ? 0 : text.LastIndexOf('\n', caret - 1) + 1;
        var lineEnd = text.IndexOf('\n', caret);
        if (lineEnd < 0)
            lineEnd = text.Length;
        if (!string.IsNullOrWhiteSpace(text[caret..lineEnd]))
            return false;
        var line = model.Lines[Math.Clamp(model.LineIndexAt(caret), 0, model.Lines.Count - 1)];
        if (line.Kind != MdLineKind.FenceOpen || line.Start != lineStart)
            return false;
        if (OpenFencePattern().Match(text[lineStart..caret]) is not { Success: true } m)
            return false;
        start = lineStart + m.Groups["lang"].Index;
        query = m.Groups["lang"].Value;
        return true;
    }

    /// <summary>按输入的文字搜索（不区分大小写）：前缀匹配在前，其次包含，各自保持常用顺序。</summary>
    public static IReadOnlyList<string> Search(string query, int max) =>
        All.Select(l => (Language: l, Rank: l.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0
                : l.Contains(query, StringComparison.OrdinalIgnoreCase) ? 1 : -1))
            .Where(x => x.Rank >= 0)
            .OrderBy(x => x.Rank)
            .Select(x => x.Language)
            .Take(max)
            .ToList();

    /// <summary>
    /// 选中语言：把 <c>[start, caret)</c> 换成语言；围栏还没有结束时补上空行和结束围栏，光标放在空行上，
    /// 已经结束时光标移到下一行（代码块的第一行）。
    /// </summary>
    public static MarkdownEdit Accept(string text, int caret, int start, string language, MarkdownModel model)
    {
        var index = Math.Clamp(model.LineIndexAt(caret), 0, model.Lines.Count - 1);
        var line = model.Lines[index];
        var m = OpenFencePattern().Match(text[line.Start..start] + language);
        var prefix = m.Success ? m.Groups["prefix"].Value : "";
        var fence = m.Success ? m.Groups["fence"].Value : "```";
        if (model.Lines.Skip(index + 1).Any(l => l.Kind == MdLineKind.FenceClose))
        {
            // 替换后本行的行尾 + 1：下一行的开头
            var next = line.End + language.Length - (caret - start) + 1;
            return new MarkdownEdit(start, caret - start, language, next, next);
        }
        var replacement = $"{language}\n{prefix}\n{prefix}{fence}";
        var at = start + language.Length + 1 + prefix.Length;
        return new MarkdownEdit(start, caret - start, replacement, at, at);
    }

    [GeneratedRegex(@"^(?<prefix>(?: {0,3}> ?)*)(?<fence>`{3,}|~{3,})(?<lang>[^\s`~]*)$")]
    private static partial Regex OpenFencePattern();
}
