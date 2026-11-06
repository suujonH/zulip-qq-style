//! Markdown → 编辑区的 HTML。不变量：每行的文字（`data-src` 算它的原文）拼起来就是这一行的 Markdown，
//! 这样 JS 不用懂语法也能把 DOM 读回原文。符号用 `.zm` 隐藏，`.zr` 的 `data-s`、`data-e` 是光标进入时显示符号的范围。

use crate::ui::IconName;
use crate::widgets::file_icon;
use serde::Serialize;
use std::collections::{HashMap, HashSet};
use std::sync::Arc;
use zulip_core::emoji::EmojiCatalog;
use zulip_core::markdown::quote::QuoteBlock;
use zulip_core::markdown::table::{MarkdownTable, TableAlign, TableEditor};
use zulip_core::markdown::{
    FenceKind, Line, LineKind, MarkdownModel, MarkdownResolver, PrefixKind, Span, SpanKind,
};
use zulip_core::text::time;

/// 判断提及、频道、表情是否存在，并提供显示用的数据。来自会话的快照。
#[derive(Clone, Default)]
pub struct Lookup {
    pub users: HashMap<i64, String>,
    pub user_names: HashSet<String>,
    pub groups: HashSet<String>,
    pub channels: HashSet<String>,
    pub emoji: Arc<EmojiCatalog>,
    /// 远程图片 → 本地地址。
    pub images: HashMap<String, String>,
}

impl MarkdownResolver for Lookup {
    fn has_user(&self, name: &str, id: Option<i64>) -> bool {
        let name = name.to_lowercase();
        match id {
            Some(id) => self
                .users
                .get(&id)
                .is_some_and(|n| n.to_lowercase() == name),
            None => self.user_names.contains(&name),
        }
    }

    fn has_group(&self, name: &str) -> bool {
        self.groups.contains(&name.to_lowercase())
    }

    fn has_channel(&self, name: &str) -> bool {
        self.channels.contains(&name.to_lowercase())
    }

    fn has_emoji(&self, name: &str) -> bool {
        self.emoji.get(&name.to_lowercase()).is_some()
    }
}

/// 编辑区的一个块，JS 按 `key` 判断是否要更新。
#[derive(Clone, Debug, PartialEq, Serialize)]
pub struct Block {
    pub key: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub html: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub table: Option<TableBlock>,
}

impl Block {
    pub fn line(html: String) -> Self {
        Self {
            key: html.clone(),
            html: Some(html),
            table: None,
        }
    }
}

#[derive(Clone, Debug, PartialEq, Serialize)]
pub struct TableBlock {
    pub index: usize,
    pub src: String,
    pub aligns: Vec<&'static str>,
    /// 行 → 列 → 单元格里的行。
    pub rows: Vec<Vec<Vec<Block>>>,
}

pub struct RenderCtx<'a> {
    pub text: &'a str,
    pub model: &'a MarkdownModel,
    pub lookup: &'a Lookup,
    pub source: bool,
}

fn escape_into(out: &mut String, text: &str) {
    for c in text.chars() {
        match c {
            '&' => out.push_str("&amp;"),
            '<' => out.push_str("&lt;"),
            '>' => out.push_str("&gt;"),
            '"' => out.push_str("&quot;"),
            other => out.push(other),
        }
    }
}

pub fn escape(text: &str) -> String {
    let mut out = String::with_capacity(text.len());
    escape_into(&mut out, text);
    out
}

fn utf16_len(text: &str) -> usize {
    text.encode_utf16().count()
}

fn icon_html(name: IconName, size: u32) -> String {
    let (stroke, fill, even_odd) = name.paths();
    let mut out =
        format!(r#"<svg class="icon" width="{size}" height="{size}" viewBox="0 0 24 24">"#);
    if !fill.is_empty() {
        let rule = if even_odd { "evenodd" } else { "nonzero" };
        out.push_str(&format!(
            r#"<path class="solid" d="{fill}" fill-rule="{rule}"></path>"#
        ));
    }
    if !stroke.is_empty() {
        out.push_str(&format!(r#"<path class="stroke" d="{stroke}"></path>"#));
    }
    out.push_str("</svg>");
    out
}

/// 只有代码、链接、粗体、斜体在光标进入时显示符号。
fn reveals(kind: SpanKind) -> bool {
    matches!(
        kind,
        SpanKind::Code
            | SpanKind::Link
            | SpanKind::Url
            | SpanKind::Strong
            | SpanKind::Em
            | SpanKind::StrongEm
    )
}

fn span_class(kind: SpanKind) -> &'static str {
    match kind {
        SpanKind::Code => "zk",
        SpanKind::StrongEm => "zB zI",
        SpanKind::Strong => "zB",
        SpanKind::Em => "zI",
        SpanKind::Del => "zD",
        SpanKind::Math => "zX",
        SpanKind::Link | SpanKind::Url => "zu",
        _ => "",
    }
}

impl RenderCtx<'_> {
    /// 第 `index` 行的 HTML。`extra` 是额外的 class。
    pub fn line_html(&self, index: usize, extra: &str) -> String {
        let line = &self.model.lines[index];
        let mut class = String::from("zl");
        let mut attrs = String::new();
        let len = utf16_len(&self.text[line.start..line.end]);
        let body = self.line_body(line, len, &mut class, &mut attrs);
        if !extra.is_empty() {
            class.push(' ');
            class.push_str(extra);
        }
        self.block_classes(index, &mut class);
        format!(r#"<div class="{class}"{attrs}>{body}<br></div>"#)
    }

    fn line_body(&self, line: &Line, len: usize, class: &mut String, attrs: &mut String) -> String {
        let mut out = String::new();
        let whole = &self.text[line.start..line.end];
        match line.kind {
            LineKind::FenceOpen | LineKind::FenceClose | LineKind::Rule if !self.source => {
                class.push_str(match line.kind {
                    LineKind::FenceOpen => " zr zl-fence",
                    LineKind::FenceClose => " zr zl-close",
                    _ => " zr zl-rule",
                });
                attrs.push_str(&format!(r#" data-s="0" data-e="{len}""#));
                out.push_str(r#"<span class="zm">"#);
                escape_into(&mut out, whole);
                out.push_str("</span>");
                if line.kind == LineKind::FenceOpen {
                    out.push_str(&format!(
                        r#"<span class="zf" data-label="{}"></span>"#,
                        escape(&fence_label(line))
                    ));
                }
                return out;
            }
            LineKind::FenceOpen
            | LineKind::FenceClose
            | LineKind::Rule
            | LineKind::TableSeparator => {
                class.push_str(" zl-dim");
                escape_into(&mut out, whole);
                return out;
            }
            LineKind::FenceBody
                if matches!(line.fence, Some(FenceKind::Code | FenceKind::Math)) =>
            {
                class.push_str(" zl-mono");
                self.prefixes(&mut out, line, class, attrs);
                escape_into(
                    &mut out,
                    &self.text[line.content_start.min(line.end)..line.end],
                );
                return out;
            }
            LineKind::IndentedCode => {
                class.push_str(" zl-mono");
                escape_into(&mut out, whole);
                return out;
            }
            LineKind::Heading => {
                class.push_str(&format!(" zl-h{}", line.heading_level.clamp(1, 6)));
            }
            _ => {}
        }
        self.prefixes(&mut out, line, class, attrs);
        let spans: Vec<&Span> = self
            .model
            .spans
            .iter()
            .filter(|s| s.start >= line.content_start && s.end <= line.end)
            .collect();
        self.inline(&mut out, line, line.content_start, line.end, &spans);
        out
    }

    // 行首符号：渲染模式下隐藏，圆点换成 “•”。之后到内容之前的文字（缩进）照常
    fn prefixes(&self, out: &mut String, line: &Line, class: &mut String, attrs: &mut String) {
        let mut pos = line.start;
        let mut hidden_to = 0;
        for prefix in &line.prefixes {
            escape_into(out, &self.text[pos..prefix.start]);
            let raw = &self.text[prefix.start..prefix.end];
            if self.source {
                out.push_str(r#"<span class="zs">"#);
                escape_into(out, raw);
                out.push_str("</span>");
            } else if prefix.kind == PrefixKind::Ordered {
                escape_into(out, raw);
            } else {
                out.push_str(r#"<span class="zm">"#);
                escape_into(out, raw);
                out.push_str("</span>");
                if prefix.kind == PrefixKind::Bullet {
                    out.push_str(r#"<span class="zb"></span>"#);
                }
                hidden_to = prefix.end;
            }
            pos = prefix.end;
        }
        escape_into(out, &self.text[pos..line.content_start.max(pos)]);
        if !self.source
            && line.quote_depth > 0
            && line.prefixes.iter().any(|p| p.kind == PrefixKind::Quote)
        {
            class.push_str(" zl-bar");
        }
        if hidden_to > 0 {
            attrs.push_str(&format!(
                r#" data-cs="{}""#,
                utf16_len(&self.text[line.start..hidden_to])
            ));
        }
    }

    // 嵌套的行内元素（解析结果不会交叉；万一交叉，后一个按文字显示）
    fn inline(&self, out: &mut String, line: &Line, from: usize, to: usize, spans: &[&Span]) {
        let mut pos = from;
        let mut i = 0;
        while i < spans.len() {
            let span = spans[i];
            if span.start < pos || span.end > to {
                i += 1;
                continue;
            }
            escape_into(out, &self.text[pos..span.start]);
            let inner_end = spans[i + 1..]
                .iter()
                .position(|s| s.start >= span.end)
                .map_or(spans.len(), |n| i + 1 + n);
            self.span(out, line, span, &spans[i + 1..inner_end]);
            pos = span.end;
            i = inner_end;
        }
        escape_into(out, &self.text[pos..to]);
    }

    fn span(&self, out: &mut String, line: &Line, span: &Span, inner: &[&Span]) {
        let raw = &self.text[span.start..span.end];
        if span.kind.is_atomic() {
            if self.source {
                out.push_str(r#"<span class="zs">"#);
                escape_into(out, raw);
                out.push_str("</span>");
            } else {
                out.push_str(&format!(
                    r#"<span class="za {}" contenteditable="false" data-src="{}">"#,
                    atom_class(span.kind),
                    escape(raw)
                ));
                self.atom(out, span);
                out.push_str("</span>");
            }
            return;
        }
        let class = span_class(span.kind);
        let (mark, reveal) = if self.source {
            ("zs", false)
        } else {
            ("zm", reveals(span.kind))
        };
        if reveal {
            out.push_str(&format!(
                r#"<span class="zr {class}" data-s="{}" data-e="{}">"#,
                utf16_len(&self.text[line.start..span.start]),
                utf16_len(&self.text[line.start..span.end]),
            ));
        } else {
            out.push_str(&format!(r#"<span class="{class}">"#));
        }
        // 没有文字的链接显示地址
        let empty_link =
            span.kind == SpanKind::Link && span.content_start == span.content_end && !self.source;
        let write_mark = |out: &mut String, text: &str| {
            if !text.is_empty() {
                out.push_str(&format!(r#"<span class="{mark}">"#));
                escape_into(out, text);
                out.push_str("</span>");
            }
        };
        write_mark(out, &self.text[span.start..span.content_start]);
        if empty_link {
            let label = span.data.clone().unwrap_or_default();
            out.push_str(&format!(
                r#"<span class="zf" data-label="{}"></span>"#,
                escape(&label)
            ));
        }
        self.inline(out, line, span.content_start, span.content_end, inner);
        write_mark(out, &self.text[span.content_end..span.end]);
        out.push_str("</span>");
    }

    fn atom(&self, out: &mut String, span: &Span) {
        let data = span.data.as_deref().unwrap_or_default();
        match span.kind {
            SpanKind::Emoji => match self.lookup.emoji.get(&data.to_lowercase()) {
                Some(entry) => match (&entry.text, &entry.image_url) {
                    (Some(text), None) => escape_into(out, text),
                    (_, Some(url)) => match self.lookup.images.get(url) {
                        Some(local) => out.push_str(&format!(
                            r#"<img class="zemoji" src="{}" alt=":{}:">"#,
                            escape(local),
                            escape(data)
                        )),
                        None => escape_into(out, &format!(":{data}:")),
                    },
                    _ => escape_into(out, &format!(":{data}:")),
                },
                None => escape_into(out, &format!(":{data}:")),
            },
            SpanKind::Time => {
                let label = time::parse_datetime(data)
                    .map_or_else(|| data.to_owned(), |t| time::local_label(&t));
                escape_into(out, &label);
            }
            SpanKind::UploadImage => match self.lookup.images.get(data) {
                Some(local) => out.push_str(&format!(
                    r#"<img class="zthumb" src="{}" alt="">"#,
                    escape(local)
                )),
                None => out.push_str(r#"<span class="zthumb zthumb-empty"></span>"#),
            },
            SpanKind::UploadFile => {
                out.push_str(&icon_html(file_icon(data), 16));
                out.push_str("<span>");
                escape_into(out, &self.text[span.content_start..span.content_end]);
                out.push_str("</span>");
            }
            SpanKind::UploadPlaceholder => {
                escape_into(out, &format!("上传中：{data}…"));
            }
            _ => escape_into(out, data),
        }
    }

    // 连续的引用或代码行连成一块：标出第一行和最后一行
    fn block_classes(&self, index: usize, class: &mut String) {
        if self.source {
            return;
        }
        let Some(kind) = block_kind(&self.model.lines[index]) else {
            return;
        };
        class.push_str(if kind == BlockKind::Quote {
            " zl-bq"
        } else {
            " zl-bc"
        });
        let same = |i: Option<usize>| {
            i.and_then(|i| self.model.lines.get(i))
                .is_some_and(|l| block_kind(l) == Some(kind))
        };
        if !same(index.checked_sub(1)) {
            class.push_str(" zl-first");
        }
        if !same(Some(index + 1)) {
            class.push_str(" zl-last");
        }
    }

    pub fn table_block(
        &self,
        table: MarkdownTable,
        index: usize,
        cell_lines: impl Fn(usize, usize, &str) -> Vec<Block>,
    ) -> Block {
        let editor = TableEditor::new(self.model, self.text, table);
        let aligns = editor
            .aligns()
            .into_iter()
            .map(|a| match a {
                TableAlign::Left => "left",
                TableAlign::Center => "center",
                TableAlign::Right => "right",
            })
            .collect();
        let rows = editor
            .cell_values()
            .iter()
            .enumerate()
            .map(|(r, cells)| {
                cells
                    .iter()
                    .enumerate()
                    .map(|(c, value)| cell_lines(r, c, value))
                    .collect()
            })
            .collect();
        let block = TableBlock {
            index,
            src: editor.source().to_owned(),
            aligns,
            rows,
        };
        Block {
            key: serde_json::to_string(&block).unwrap_or_default(),
            html: None,
            table: Some(block),
        }
    }
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum BlockKind {
    Quote,
    Code,
}

fn block_kind(line: &Line) -> Option<BlockKind> {
    if line.quote_depth > 0 {
        return Some(BlockKind::Quote);
    }
    (line.kind == LineKind::IndentedCode
        || matches!(
            line.fence,
            Some(FenceKind::Code | FenceKind::Math | FenceKind::Spoiler)
        ))
    .then_some(BlockKind::Code)
}

fn atom_class(kind: SpanKind) -> &'static str {
    match kind {
        SpanKind::UserMention | SpanKind::GroupMention | SpanKind::ChannelLink => "zn",
        SpanKind::Time => "zw",
        SpanKind::Emoji => "ze",
        SpanKind::UploadImage => "zp",
        SpanKind::UploadFile => "zfile-chip",
        _ => "zh",
    }
}

fn fence_label(line: &Line) -> String {
    let info = line.fence_info.as_deref().unwrap_or_default();
    match line.fence {
        Some(FenceKind::Quote) => "引用".to_owned(),
        Some(FenceKind::Math) => "公式".to_owned(),
        Some(FenceKind::Spoiler) if info.is_empty() => "折叠".to_owned(),
        Some(FenceKind::Spoiler) => format!("折叠 {info}"),
        _ if info.is_empty() => "代码".to_owned(),
        _ => format!("代码 {info}"),
    }
}

/// 收起或展开的引用面板。整段原文放在 `data-src`，编辑时 `src` 只有引用行。
pub fn quote_panel(
    q: usize,
    quote: &QuoteBlock,
    src: &str,
    preview: &str,
    expanded: Option<&str>,
    editing: bool,
) -> String {
    let mut out = format!(
        r#"<div class="zq{}" contenteditable="false" data-src="{}" data-q="{q}">"#,
        if editing { " editing" } else { "" },
        escape(src),
    );
    let open = editing || expanded.is_some();
    out.push_str(&format!(
        r#"<div class="zq-head"><div class="zq-title{}" data-act="{}"><span class="zq-chevron">{}</span><span class="zq-text">{}：{}</span></div>"#,
        if open { " open" } else { "" },
        if editing { "end-edit" } else { "toggle" },
        icon_html(IconName::ChevronRight, 12),
        escape(&quote.sender_name),
        escape(preview),
    ));
    let button = |act: &str, tip: &str, icon: IconName| {
        format!(
            r#"<button class="icon-btn" type="button" title="{tip}" data-act="{act}" style="width:22px;height:22px">{}</button>"#,
            icon_html(icon, 14)
        )
    };
    if !editing {
        out.push_str(&button("edit", "编辑引用", IconName::Edit));
    }
    if quote.message_id.is_some() {
        out.push_str(&button("jump", "跳转到原消息", IconName::ArrowRightUp));
    }
    out.push_str(&button("delete", "删除引用", IconName::Close));
    out.push_str("</div>");
    if let Some(body) = expanded {
        out.push_str(&format!(r#"<div class="zq-body selectable">{body}</div>"#));
    }
    out.push_str("</div>");
    out
}

#[cfg(test)]
mod tests {
    use super::*;
    use zulip_core::markdown::{NoResolver, parse};

    /// 去掉标签后的文字（`data-src` 算原文）要等于这一行的 Markdown。
    fn text_of(html: &str) -> String {
        let mut out = String::new();
        let mut rest = html;
        while let Some(open) = rest.find('<') {
            out.push_str(&rest[..open]);
            let close = rest[open..].find('>').map_or(rest.len(), |i| open + i);
            let tag = &rest[open..=close.min(rest.len() - 1)];
            rest = &rest[(close + 1).min(rest.len())..];
            if let Some(start) = tag.find("data-src=\"") {
                let value = &tag[start + 10..];
                let value = &value[..value.find('"').unwrap_or(0)];
                out.push_str(value);
                // 跳过元素内部的显示文字
                let end = rest.find("</span>").unwrap_or(0);
                rest = &rest[end + 7..];
            }
        }
        out.replace("&lt;", "<")
            .replace("&gt;", ">")
            .replace("&quot;", "\"")
            .replace("&amp;", "&")
    }

    fn render(text: &str, source: bool) -> Vec<String> {
        let model = parse(text, &NoResolver);
        let lookup = Lookup::default();
        let ctx = RenderCtx {
            text,
            model: &model,
            lookup: &lookup,
            source,
        };
        (0..model.lines.len())
            .map(|i| ctx.line_html(i, ""))
            .collect()
    }

    #[test]
    fn text_round_trips() {
        let doc = "# 标题\n**粗体** *斜* `代码` ~~删~~ [链接](http://a.b)\n> 引用\n- 列表\n1. 有序\n```py\nx < 1 & y\n```\n---\n<普通> \"引号\"";
        for source in [false, true] {
            let lines = render(doc, source);
            let joined: Vec<String> = lines.iter().map(|l| text_of(l)).collect();
            assert_eq!(joined.join("\n"), doc, "source={source}");
        }
    }

    #[test]
    fn marks_hidden_only_in_rich_mode() {
        let rich = render("**a**", false);
        assert!(
            rich[0].contains(r#"class="zr zB" data-s="0" data-e="5""#),
            "{}",
            rich[0]
        );
        assert!(rich[0].contains(r#"<span class="zm">**</span>"#));
        let source = render("**a**", true);
        assert!(source[0].contains(r#"<span class="zs">**</span>"#));
        assert!(!source[0].contains("zm"));
    }

    #[test]
    fn reveal_range_counts_utf16() {
        let html = render("😀 **a**", false);
        assert!(html[0].contains(r#"data-s="3" data-e="8""#), "{}", html[0]);
    }

    #[test]
    fn hidden_prefix_marks_content_start() {
        let html = render("## 标题", false);
        assert!(html[0].contains(r#"data-cs="3""#), "{}", html[0]);
        assert!(html[0].contains("zl-h2"));
        assert!(!render("## 标题", true)[0].contains("data-cs"));
    }

    #[test]
    fn code_lines_form_one_block() {
        let html = render("```\na\n```", false);
        assert!(html[0].contains("zl-first") && !html[0].contains("zl-last"));
        assert!(html[1].contains("zl-mono"));
        assert!(html[2].contains("zl-last"));
    }
}
