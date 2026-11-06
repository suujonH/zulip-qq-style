//! 代码块语言补全：在 ` ``` `（或 `~~~`）开头的行末输入时，列出服务器支持的语言（Pygments 的常用别名）
//! 与 Zulip 的特殊块（quote、spoiler、math），按输入的文字搜索。

use super::format::MarkdownEdit;
use super::{LineKind, MarkdownModel};
use crate::util::{contains_ignore_case, is_blank, starts_with_ignore_case, static_regex};

static_regex!(OPEN_FENCE = r"^(?<prefix>(?: {0,3}> ?)*)(?<fence>`{3,}|~{3,})(?<lang>[^\s`~]*)$");

/// 按常用程度排列（与网页版补全的优先顺序大致相同）。
pub const ALL: &[&str] = &[
    "python",
    "javascript",
    "typescript",
    "java",
    "c",
    "cpp",
    "csharp",
    "go",
    "rust",
    "kotlin",
    "swift",
    "ruby",
    "php",
    "html",
    "css",
    "json",
    "yaml",
    "xml",
    "sql",
    "bash",
    "shell",
    "console",
    "powershell",
    "text",
    "markdown",
    "diff",
    "dockerfile",
    "makefile",
    "ini",
    "toml",
    "quote",
    "spoiler",
    "math",
    "scss",
    "sass",
    "less",
    "jsx",
    "tsx",
    "vue",
    "graphql",
    "lua",
    "perl",
    "r",
    "scala",
    "haskell",
    "elixir",
    "erlang",
    "clojure",
    "dart",
    "objective-c",
    "matlab",
    "groovy",
    "nginx",
    "vim",
    "latex",
    "protobuf",
    "cmake",
    "fsharp",
    "ocaml",
    "julia",
    "zig",
    "nim",
    "http",
    "apache",
    "batch",
    "vbnet",
    "nasm",
    "solidity",
    "hcl",
    "coffeescript",
    "d",
    "fortran",
    "pascal",
    "prolog",
    "scheme",
    "common-lisp",
    "tex",
    "properties",
    "csv",
    "js",
    "ts",
    "py",
    "sh",
    "cs",
];

/// 光标位于开头围栏行（[`LineKind::FenceOpen`]）的末尾时，取出正在输入的语言：
/// 返回语言的起点和已输入的部分（可以为空）。
pub fn query_at<'a>(
    text: &'a str,
    caret: usize,
    model: &MarkdownModel,
) -> Option<(usize, &'a str)> {
    if caret > text.len() || !text.is_char_boundary(caret) {
        return None;
    }
    let line_start = text[..caret].rfind('\n').map_or(0, |i| i + 1);
    let line_end = text[caret..].find('\n').map_or(text.len(), |i| caret + i);
    if !is_blank(&text[caret..line_end]) {
        return None;
    }
    let line = &model.lines[model.clamped_line_index(caret)?];
    if line.kind != LineKind::FenceOpen || line.start != line_start {
        return None;
    }
    let lang = OPEN_FENCE
        .captures(&text[line_start..caret])?
        .name("lang")?;
    Some((
        line_start + lang.start(),
        &text[line_start + lang.start()..caret],
    ))
}

/// 按输入的文字搜索（不区分大小写）：前缀匹配在前，其次包含，各自保持常用顺序。
pub fn search(query: &str, max: usize) -> Vec<&'static str> {
    let prefix = ALL.iter().filter(|l| starts_with_ignore_case(l, query));
    let contains = ALL
        .iter()
        .filter(|l| !starts_with_ignore_case(l, query) && contains_ignore_case(l, query));
    prefix.chain(contains).copied().take(max).collect()
}

/// 选中语言：把 `[start, caret)` 换成语言；围栏还没有结束时补上空行和结束围栏，光标放在空行上，
/// 已经结束时光标移到下一行（代码块的第一行）。
pub fn accept(
    text: &str,
    caret: usize,
    start: usize,
    language: &str,
    model: &MarkdownModel,
) -> MarkdownEdit {
    let index = model.clamped_line_index(caret).unwrap_or(0);
    let line_start = model.lines.get(index).map_or(0, |l| l.start);
    let line_end = model.lines.get(index).map_or(text.len(), |l| l.end);
    let head = format!("{}{language}", &text[line_start..start]);
    let m = OPEN_FENCE.captures(&head);
    let prefix = m
        .as_ref()
        .and_then(|c| c.name("prefix"))
        .map_or("", |g| g.as_str());
    let fence = m
        .as_ref()
        .and_then(|c| c.name("fence"))
        .map_or("```", |g| g.as_str());
    let len = caret - start;
    if model
        .lines
        .iter()
        .skip(index + 1)
        .any(|l| l.kind == LineKind::FenceClose)
    {
        // 替换后本行的行尾 + 1：下一行的开头
        let next = line_end + language.len() - len + 1;
        return MarkdownEdit::new(start, len, language.to_owned(), next, next);
    }
    let at = start + language.len() + 1 + prefix.len();
    MarkdownEdit::new(
        start,
        len,
        format!("{language}\n{prefix}\n{prefix}{fence}"),
        at,
        at,
    )
}

#[cfg(test)]
mod tests {
    use super::super::{NoResolver, parse};
    use super::*;

    fn model(text: &str) -> MarkdownModel {
        parse(text, &NoResolver)
    }

    #[test]
    fn query_at_fence_line_end() {
        for (text, start, query) in [("```", 3, ""), ("```py", 3, "py"), ("x\n```ru", 5, "ru")] {
            assert_eq!(
                query_at(text, text.len(), &model(text)),
                Some((start, query)),
                "{text}"
            );
        }
    }

    #[test]
    fn query_not_on_fence_line() {
        let text = "abc";
        assert_eq!(query_at(text, 3, &model(text)), None);
        let text = "```py\ncode";
        assert_eq!(query_at(text, text.len(), &model(text)), None);
        assert_eq!(query_at("", 0, &MarkdownModel::default()), None);
    }

    #[test]
    fn search_prefix_before_contains() {
        assert_eq!(search("PY", 2), ["python", "py"]);
        assert_eq!(
            search("script", 9),
            ["javascript", "typescript", "coffeescript"]
        );
        assert_eq!(search("", 2), ["python", "javascript"]);
        assert!(search("zzzz", 5).is_empty());
    }

    #[test]
    fn accept_adds_closing_fence() {
        let text = "> ```p";
        let edit = accept(text, text.len(), 5, "python", &model(text));
        assert_eq!(edit.apply(text), "> ```python\n> \n> ```");
        assert_eq!(edit.selection_start, "> ```python\n> ".len());
    }

    #[test]
    fn accept_moves_to_next_line_when_closed() {
        let text = "```p\ncode\n```";
        let edit = accept(text, 4, 3, "python", &model(text));
        assert_eq!(edit.apply(text), "```python\ncode\n```");
        assert_eq!(edit.selection_start, "```python\n".len());
    }
}
