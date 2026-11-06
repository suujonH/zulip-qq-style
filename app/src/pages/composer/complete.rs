//! 补全：判断光标前正在输入什么，从候选中筛选排序。候选的数据由调用方给出。

use zulip_core::markdown::{self, MarkdownModel, code_language};
use zulip_core::text::mention;

pub const MAX_ITEMS: usize = 8;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Kind {
    Mention,
    Channel,
    Emoji,
    Language,
    Snippet,
}

/// 光标前正在输入的内容：`[start, caret)` 会被替换。
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Trigger {
    pub kind: Kind,
    pub start: usize,
    pub query: String,
}

#[derive(Clone, Debug, PartialEq, Default)]
pub struct Item {
    pub title: String,
    pub subtitle: Option<String>,
    /// 替换进文字的内容；语言补全是语言名。
    pub insert: String,
    pub avatar_url: Option<String>,
    pub avatar_text: Option<String>,
    /// 表情的字符或图片。
    pub glyph: Option<String>,
    /// 排序用的名字。
    pub sort: String,
}

/// 向前找到 `mark`：它在开头或空白之后，且到光标之间没有空白。
fn marked_query(text: &str, caret: usize, mark: char) -> Option<(usize, &str)> {
    let (at, _) = text[..caret]
        .char_indices()
        .rev()
        .take_while(|(_, c)| !c.is_whitespace())
        .find(|(_, c)| *c == mark)?;
    if text[..at]
        .chars()
        .next_back()
        .is_some_and(|c| !c.is_whitespace())
    {
        return None;
    }
    Some((at, &text[at + mark.len_utf8()..caret]))
}

pub fn detect(text: &str, caret: usize, model: &MarkdownModel) -> Option<Trigger> {
    if caret > text.len() || !text.is_char_boundary(caret) {
        return None;
    }
    if let Some((start, query)) = code_language::query_at(text, caret, model) {
        return Some(Trigger {
            kind: Kind::Language,
            start,
            query: query.to_owned(),
        });
    }
    let line = model.line_index_at(caret).and_then(|i| model.lines.get(i));
    // 代码里不补全；已经完成的提及、表情等不再触发
    if line.is_some_and(|l| {
        matches!(
            l.kind,
            markdown::LineKind::FenceBody | markdown::LineKind::IndentedCode
        ) && l.fence != Some(markdown::FenceKind::Quote)
    }) {
        return None;
    }
    if model
        .spans
        .iter()
        .any(|s| s.kind == markdown::SpanKind::Code && s.start < caret && caret <= s.end)
    {
        return None;
    }
    let masked = mention::mask(text, &markdown::mention_spans(model, text));
    if let Some((start, query)) = mention::try_get_query(&masked, caret) {
        return Some(Trigger {
            kind: Kind::Mention,
            start,
            query: query.to_owned(),
        });
    }
    if let Some((start, query)) = marked_query(text, caret, '#')
        && !query.starts_with(['*', '#'])
    {
        return Some(Trigger {
            kind: Kind::Channel,
            start,
            query: query.to_owned(),
        });
    }
    if let Some((start, query)) = marked_query(text, caret, ':')
        && !query.is_empty()
        && !query.contains(':')
    {
        return Some(Trigger {
            kind: Kind::Emoji,
            start,
            query: query.to_owned(),
        });
    }
    let line_start = text[..caret].rfind('\n').map_or(0, |i| i + 1);
    if let Some(query) = text[line_start..caret].strip_prefix('/')
        && !query.contains(char::is_whitespace)
    {
        return Some(Trigger {
            kind: Kind::Snippet,
            start: line_start,
            query: query.to_owned(),
        });
    }
    None
}

/// 按匹配程度和名字排序，取前几个。`rank` 为 `None` 的候选去掉。
pub fn rank_items(mut items: Vec<(u8, Item)>) -> Vec<Item> {
    items.sort_by(|a, b| a.0.cmp(&b.0).then_with(|| a.1.sort.cmp(&b.1.sort)));
    items
        .into_iter()
        .take(MAX_ITEMS)
        .map(|(_, item)| item)
        .collect()
}

pub fn language_items(query: &str) -> Vec<Item> {
    code_language::search(query, MAX_ITEMS)
        .into_iter()
        .map(|language| Item {
            title: language.to_owned(),
            insert: language.to_owned(),
            ..Item::default()
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    use zulip_core::markdown::{NoResolver, parse};

    fn at_end(text: &str) -> Option<(Kind, String)> {
        let model = parse(text, &NoResolver);
        detect(text, text.len(), &model).map(|t| (t.kind, t.query))
    }

    #[test]
    fn triggers() {
        assert_eq!(at_end("你好 @阿"), Some((Kind::Mention, "阿".into())));
        assert_eq!(at_end("#综"), Some((Kind::Channel, "综".into())));
        assert_eq!(at_end("a :smi"), Some((Kind::Emoji, "smi".into())));
        assert_eq!(at_end("```py"), Some((Kind::Language, "py".into())));
        assert_eq!(at_end("x\n/常"), Some((Kind::Snippet, "常".into())));
    }

    #[test]
    fn no_trigger() {
        for text in [
            "mail@example",
            "a#b",
            "12:30",
            "a /b",
            ":",
            "`@a`",
            "```\n@a",
            "/a b",
        ] {
            assert_eq!(at_end(text), None, "{text}");
        }
    }

    #[test]
    fn items_sorted_by_rank_then_name() {
        let item = |sort: &str| Item {
            sort: sort.into(),
            ..Item::default()
        };
        let sorted = rank_items(vec![(1, item("a")), (0, item("c")), (0, item("b"))]);
        let names: Vec<&str> = sorted.iter().map(|i| i.sort.as_str()).collect();
        assert_eq!(names, ["b", "c", "a"]);
    }

    #[test]
    fn languages_filtered() {
        assert_eq!(language_items("rus")[0].title, "rust");
    }
}
