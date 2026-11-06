//! 把服务器渲染的消息 HTML 改写成聊天气泡里显示的内容。

use ego_tree::{NodeId, NodeRef};
use scraper::{ElementRef, Node};
use std::collections::{HashMap, HashSet};
use zulip_core::html::{
    InlineImage, UploadLink, emoji_from_class, extract_inline_images, inline_preview_paths,
    inlined_previews, is_file_name_label, is_upload_only_paragraph, mention_display,
    normalize_upload_path, parse_fragment, upload_link_of,
};
use zulip_core::markdown::table::{TableAlign, table_from_cells};

const INLINE_IMAGE: &str = "message_inline_image";
const INLINE_VIDEO: &str = "message_inline_video";
const IMAGE_MAX: f64 = 240.0;
const IMAGE_DEFAULT: (f64, f64) = (240.0, 135.0);

/// 不显示也不展开子节点的标签。
const DROPPED: &[&str] = &[
    "script", "style", "iframe", "object", "embed", "link", "meta", "form", "input", "button",
    "svg", "math", "video", "audio", "base",
];
/// 原样保留的标签；其它标签只保留子节点。
const KEPT: &[&str] = &[
    "p",
    "br",
    "hr",
    "ul",
    "ol",
    "li",
    "strong",
    "em",
    "b",
    "i",
    "s",
    "del",
    "code",
    "span",
    "div",
    "h1",
    "h2",
    "h3",
    "h4",
    "h5",
    "h6",
    "a",
    "sub",
    "sup",
    "time",
    "small",
    "thead",
    "tbody",
    "tr",
    "th",
    "td",
    "blockquote",
];
const VOID: &[&str] = &["br", "hr"];
const KEPT_ATTRS: &[&str] = &[
    "class",
    "href",
    "title",
    "colspan",
    "rowspan",
    "start",
    "datetime",
    "data-user-id",
    "data-user-group-id",
];

#[derive(Clone, Debug, PartialEq)]
pub enum Segment {
    Html(String),
    File(UploadLink),
}

#[derive(Clone, Debug, Default, PartialEq)]
pub struct Rendered {
    pub segments: Vec<Segment>,
    /// 还没有本地文件的图片地址。
    pub pending: Vec<String>,
}

pub struct RenderOptions<'a> {
    /// “/me” 消息的发送者。
    pub me: Option<&'a str>,
    /// 已下载图片的本地地址。
    pub local_image: &'a dyn Fn(&str) -> Option<String>,
}

fn has_class(e: ElementRef, class: &str) -> bool {
    e.value().classes().any(|c| c == class)
}

fn escape_into(out: &mut String, text: &str) {
    for c in text.chars() {
        match c {
            '&' => out.push_str("&amp;"),
            '<' => out.push_str("&lt;"),
            '>' => out.push_str("&gt;"),
            '"' => out.push_str("&quot;"),
            '\'' => out.push_str("&#39;"),
            c => out.push(c),
        }
    }
}

fn escape(text: &str) -> String {
    let mut out = String::with_capacity(text.len());
    escape_into(&mut out, text);
    out
}

fn is_preview(e: ElementRef) -> bool {
    has_class(e, INLINE_IMAGE) && !has_class(e, INLINE_VIDEO)
}

fn is_body_image(e: ElementRef) -> bool {
    e.value().name() == "img"
        && has_class(e, "inline-image")
        && !e
            .ancestors()
            .filter_map(ElementRef::wrap)
            .any(|a| has_class(a, INLINE_IMAGE))
}

fn first_image(e: ElementRef) -> Option<ElementRef> {
    if e.value().name() == "img" {
        return Some(e);
    }
    e.descendants()
        .filter_map(ElementRef::wrap)
        .find(|d| d.value().name() == "img")
}

/// 图片框的显示大小：等比缩小到边长上限内，没有尺寸信息时 16:9。
fn fit_image(dimensions: Option<&str>) -> (f64, f64) {
    let parsed = dimensions.and_then(|d| {
        let (w, h) = d.split_once('x')?;
        let (w, h): (f64, f64) = (w.parse().ok()?, h.parse().ok()?);
        (w > 0.0 && h > 0.0).then_some((w, h))
    });
    let Some((w, h)) = parsed else {
        return IMAGE_DEFAULT;
    };
    let scale = (IMAGE_MAX / w).min(IMAGE_MAX / h).min(1.0);
    ((w * scale).round().max(16.0), (h * scale).round().max(16.0))
}

/// 视频预览和只有上传链接的段落里的上传文件。
fn uploads_in(e: ElementRef) -> Vec<(String, String)> {
    e.descendants()
        .filter_map(ElementRef::wrap)
        .filter(|d| d.value().name() == "a")
        .filter_map(|a| {
            let path = normalize_upload_path(a.value().attr("href")?)?;
            Some((path, a.text().collect::<String>()))
        })
        .collect()
}

struct Walker<'a, 'o> {
    out: String,
    segments: Vec<Segment>,
    options: &'o RenderOptions<'o>,
    images: Vec<InlineImage>,
    image_index: HashMap<NodeId, usize>,
    /// 显示在文件名链接处的图片：链接节点 → 预览元素。
    inlined_at: HashMap<NodeId, ElementRef<'a>>,
    moved: HashSet<NodeId>,
    previews: HashSet<String>,
    tables: usize,
    pending: Vec<String>,
}

impl<'a> Walker<'a, '_> {
    fn flush(&mut self) {
        if !self.out.trim().is_empty() {
            self.segments
                .push(Segment::Html(std::mem::take(&mut self.out)));
        }
        self.out.clear();
    }

    fn top_level(&mut self, node: NodeRef<'a, Node>) {
        let Some(e) = ElementRef::wrap(node) else {
            self.node(node);
            return;
        };
        if self.moved.contains(&e.id()) {
            return;
        }
        if has_class(e, INLINE_VIDEO) {
            for (path, text) in uploads_in(e) {
                self.file(&path, &text);
            }
            return;
        }
        if e.value().name() == "p" && is_upload_only_paragraph(e) {
            let links = uploads_in(e);
            let labels_only = links.iter().all(|(path, text)| {
                !self.previews.contains(path) || is_file_name_label(text, path)
            });
            if labels_only {
                for (path, text) in links {
                    if !self.previews.contains(&path) {
                        self.file(&path, &text);
                    }
                }
                return;
            }
        }
        self.node(node);
    }

    fn file(&mut self, path: &str, text: &str) {
        self.flush();
        self.segments
            .push(Segment::File(upload_link_of(path, Some(text))));
    }

    fn children(&mut self, node: NodeRef<'a, Node>) {
        for child in node.children() {
            self.node(child);
        }
    }

    fn node(&mut self, node: NodeRef<'a, Node>) {
        match node.value() {
            Node::Text(text) => escape_into(&mut self.out, text),
            Node::Element(_) => {
                if let Some(e) = ElementRef::wrap(node) {
                    self.element(e);
                }
            }
            _ => {}
        }
    }

    fn element(&mut self, e: ElementRef<'a>) {
        if self.moved.contains(&e.id()) {
            return;
        }
        let name = e.value().name();
        if DROPPED.contains(&name) {
            return;
        }
        if let Some(&index) = self.image_index.get(&e.id()) {
            self.image(e, index, false);
            return;
        }
        if let Some(preview) = self.inlined_at.get(&e.id()).copied() {
            if let Some(&index) = self.image_index.get(&preview.id()) {
                self.image(preview, index, true);
            }
            return;
        }
        let classes = |c: &str| has_class(e, c);
        if classes("user-mention") || classes("user-group-mention") || classes("topic-mention") {
            self.mention(e);
        } else if classes("emoji") {
            self.emoji(e);
        } else if classes("katex-display") || classes("katex") {
            self.tex(e);
        } else if classes("codehilite") || name == "pre" {
            self.code_block(e);
        } else if name == "table" {
            self.table(e);
        } else if name == "blockquote" {
            self.quote(e);
        } else if classes("spoiler-block") {
            self.spoiler(e);
        } else if name == "a" && self.is_hidden_label(e) {
        } else if KEPT.contains(&name) {
            self.tag(e);
        } else {
            self.children(*e);
        }
    }

    /// 有预览的上传链接，文字只是文件名时由预览代替。
    fn is_hidden_label(&self, a: ElementRef) -> bool {
        a.value()
            .attr("href")
            .and_then(normalize_upload_path)
            .is_some_and(|path| {
                self.previews.contains(&path)
                    && is_file_name_label(&a.text().collect::<String>(), &path)
            })
    }

    fn open_tag(&mut self, e: ElementRef) {
        let name = e.value().name();
        self.out.push('<');
        self.out.push_str(name);
        for (attr, value) in e.value().attrs() {
            if KEPT_ATTRS.contains(&attr) {
                self.out.push(' ');
                self.out.push_str(attr);
                self.out.push_str("=\"");
                escape_into(&mut self.out, value);
                self.out.push('"');
            }
        }
        if matches!(name, "td" | "th")
            && let Some(align) = cell_align(e)
        {
            self.out.push_str(" style=\"text-align:");
            self.out.push_str(align);
            self.out.push('"');
        }
        self.out.push('>');
    }

    fn tag(&mut self, e: ElementRef<'a>) {
        let name = e.value().name();
        self.open_tag(e);
        if VOID.contains(&name) {
            return;
        }
        if matches!(name, "td" | "th") {
            self.cell(e);
        } else {
            self.children(*e);
        }
        self.out.push_str("</");
        self.out.push_str(name);
        self.out.push('>');
    }

    /// 表格单元格的文字中，服务器转义后的 `<br />` 还原成换行。
    fn cell(&mut self, e: ElementRef<'a>) {
        for child in e.children() {
            if let Node::Text(text) = child.value() {
                let escaped = escape(text)
                    .replace("&lt;br /&gt;", "<br>")
                    .replace("&lt;br/&gt;", "<br>")
                    .replace("&lt;br&gt;", "<br>");
                self.out.push_str(&escaped);
            } else {
                self.node(child);
            }
        }
    }

    fn image(&mut self, e: ElementRef, index: usize, inline: bool) {
        let Some(image) = self.images.get(index).cloned() else {
            return;
        };
        let (w, h) = fit_image(
            first_image(e).and_then(|img| img.value().attr("data-original-dimensions")),
        );
        let block = is_preview(e) && !inline;
        let tag = if block { "div" } else { "span" };
        let class = if block {
            "img-preview block"
        } else {
            "img-preview"
        };
        let source = self.image_source(&image.src);
        self.out.push_str(&format!(
            "<{tag} class=\"{class}\" data-index=\"{index}\" style=\"width:{w}px;height:{h}px\"><img {source} alt=\"\"></{tag}>"
        ));
    }

    /// `img` 的地址属性：已下载的直接显示，否则等下载完由脚本填入。
    fn image_source(&mut self, url: &str) -> String {
        let remote = escape(url);
        match (self.options.local_image)(url) {
            Some(local) => format!("data-remote=\"{remote}\" src=\"{}\"", escape(&local)),
            None => {
                if !self.pending.iter().any(|p| p == url) {
                    self.pending.push(url.to_owned());
                }
                format!("data-remote=\"{remote}\"")
            }
        }
    }

    fn mention(&mut self, e: ElementRef) {
        let display = mention_display(e);
        let silent = if has_class(e, "silent") { " silent" } else { "" };
        self.out
            .push_str(&format!("<span class=\"mention{silent}\""));
        for attr in ["data-user-id", "data-user-group-id"] {
            if let Some(value) = e.value().attr(attr) {
                self.out
                    .push_str(&format!(" {attr}=\"{}\"", escape(value)));
            }
        }
        self.out.push('>');
        escape_into(&mut self.out, &display);
        self.out.push_str("</span>");
    }

    fn emoji(&mut self, e: ElementRef) {
        let title = e.value().attr("title").unwrap_or_default();
        if e.value().name() == "img" {
            if let Some(src) = e.value().attr("src") {
                let source = self.image_source(src);
                self.out.push_str(&format!(
                    "<img class=\"emoji-img\" {source} title=\"{}\" alt=\"\">",
                    escape(title)
                ));
            }
            return;
        }
        let unicode = e.value().classes().find_map(emoji_from_class);
        match unicode {
            Some(text) => {
                self.out.push_str(&format!(
                    "<span class=\"emoji-char\" title=\"{}\">{}</span>",
                    escape(title),
                    escape(&text)
                ));
            }
            None => escape_into(&mut self.out, &e.text().collect::<String>()),
        }
    }

    /// 公式降级为 TeX 原文。
    fn tex(&mut self, e: ElementRef) {
        let source = e
            .descendants()
            .filter_map(ElementRef::wrap)
            .find(|d| d.value().name() == "annotation")
            .map_or_else(
                || e.text().collect::<String>(),
                |a| a.text().collect::<String>(),
            );
        let display = has_class(e, "katex-display");
        let (open, close) = if display {
            ("<div class=\"tex-block\"><code class=\"tex\">", "</code></div>")
        } else {
            ("<code class=\"tex\">", "</code>")
        };
        self.out.push_str(open);
        escape_into(&mut self.out, source.trim());
        self.out.push_str(close);
    }

    fn code_block(&mut self, e: ElementRef) {
        self.out.push_str("<pre class=\"code-block scroll\"><code>");
        let text: String = e.text().collect();
        escape_into(&mut self.out, text.strip_suffix('\n').unwrap_or(&text));
        self.out.push_str("</code></pre>");
    }

    fn table(&mut self, e: ElementRef<'a>) {
        let index = self.tables;
        self.tables += 1;
        self.out.push_str(&format!(
            "<div class=\"table-host\" data-table=\"{index}\"><div class=\"table-tools\"><div class=\"table-more\">⋯</div></div><div class=\"table-scroll scroll\"><table>"
        ));
        self.children(*e);
        self.out.push_str("</table></div></div>");
    }

    fn quote(&mut self, e: ElementRef<'a>) {
        self.out.push_str("<blockquote class=\"quote\">");
        let header = quote_header(e);
        if let Some(h) = &header {
            self.out.push_str(&format!(
                "<div class=\"quote-head\" data-href=\"{}\"><span class=\"quote-author\">{}</span><span class=\"quote-jump\">↗</span></div>",
                escape(&h.href),
                escape(&h.author)
            ));
        }
        for child in e.children() {
            if header.as_ref().is_some_and(|h| h.node == child.id()) {
                continue;
            }
            self.node(child);
        }
        self.out.push_str("</blockquote>");
    }

    fn spoiler(&mut self, e: ElementRef<'a>) {
        let child = |class: &str| {
            e.children()
                .filter_map(ElementRef::wrap)
                .find(|c| has_class(*c, class))
        };
        let label = child("spoiler-header")
            .map(|h| h.text().collect::<String>())
            .map(|t| t.trim().to_owned())
            .filter(|t| !t.is_empty())
            .unwrap_or_else(|| "折叠".to_owned());
        self.out.push_str("<div class=\"spoiler\"><div class=\"spoiler-head\"><span class=\"spoiler-arrow\">▸</span>");
        escape_into(&mut self.out, &label);
        self.out.push_str("</div><div class=\"spoiler-body\">");
        if let Some(body) = child("spoiler-content") {
            self.children(*body);
        }
        self.out.push_str("</div></div>");
    }
}

fn cell_align(e: ElementRef) -> Option<&'static str> {
    let style = e.value().attr("style")?.replace(' ', "");
    ["left", "center", "right"]
        .into_iter()
        .find(|a| style.contains(&format!("text-align:{a}")))
}

struct QuoteHeader {
    node: NodeId,
    author: String,
    href: String,
}

/// 引用块开头“@某人 [说](…near…)：”这一段。
fn quote_header(quote: ElementRef) -> Option<QuoteHeader> {
    let p = quote.children().find_map(ElementRef::wrap)?;
    if p.value().name() != "p" {
        return None;
    }
    let elements: Vec<ElementRef> = p.children().filter_map(ElementRef::wrap).collect();
    let [mention, link] = elements.as_slice() else {
        return None;
    };
    let href = link.value().attr("href")?;
    if !has_class(*mention, "user-mention") || !href.contains("/near/") {
        return None;
    }
    Some(QuoteHeader {
        node: p.id(),
        author: mention_display(*mention).trim_start_matches('@').to_owned(),
        href: href.to_owned(),
    })
}

/// 改写一条消息。
pub fn render(html: &str, options: &RenderOptions) -> Rendered {
    let doc = parse_fragment(html);
    let root = doc.root_element();
    let mut image_index = HashMap::new();
    let mut next = 0;
    for e in root.descendants().filter_map(ElementRef::wrap) {
        if is_body_image(e) || (is_preview(e) && first_image(e).is_some()) {
            image_index.insert(e.id(), next);
            next += 1;
        }
    }
    let mut inlined_at = HashMap::new();
    let mut moved = HashSet::new();
    for (link, preview) in inlined_previews(&doc) {
        // 预览元素就在段落里时，它的链接在它自己里面，不用移动
        if link.ancestors().any(|a| a.id() == preview.id()) {
            continue;
        }
        inlined_at.insert(link.id(), preview);
        moved.insert(preview.id());
    }
    let mut walker = Walker {
        out: String::new(),
        segments: Vec::new(),
        options,
        images: extract_inline_images(html),
        image_index,
        inlined_at,
        moved,
        previews: inline_preview_paths(root),
        tables: 0,
        pending: Vec::new(),
    };
    for child in root.children() {
        walker.top_level(child);
    }
    walker.flush();
    let mut segments = walker.segments;
    if let Some(name) = options.me
        && let Some(Segment::Html(first)) = segments.first_mut()
    {
        *first = first.replacen(
            "/me ",
            &format!("<span class=\"me-name\">{}</span> ", escape(name)),
            1,
        );
    }
    Rendered {
        segments,
        pending: walker.pending,
    }
}

/// 第 `index` 个表格的 Markdown 源码。
pub fn table_markdown(html: &str, index: usize, padded: bool) -> Option<String> {
    let doc = parse_fragment(html);
    let table = doc
        .root_element()
        .descendants()
        .filter_map(ElementRef::wrap)
        .filter(|e| e.value().name() == "table")
        .nth(index)?;
    let mut rows: Vec<Vec<String>> = Vec::new();
    let mut aligns = Vec::new();
    for row in table
        .descendants()
        .filter_map(ElementRef::wrap)
        .filter(|e| e.value().name() == "tr")
    {
        let cells: Vec<ElementRef> = row
            .children()
            .filter_map(ElementRef::wrap)
            .filter(|c| matches!(c.value().name(), "td" | "th"))
            .collect();
        if rows.is_empty() {
            aligns = cells
                .iter()
                .map(|c| match cell_align(*c) {
                    Some("center") => TableAlign::Center,
                    Some("right") => TableAlign::Right,
                    _ => TableAlign::Left,
                })
                .collect();
        }
        rows.push(
            cells
                .iter()
                .map(|c| {
                    let mut out = String::new();
                    inline_markdown(*c, &mut out);
                    out.replace("<br />", "\n").trim().to_owned()
                })
                .collect(),
        );
    }
    let borrowed: Vec<Vec<&str>> = rows
        .iter()
        .map(|r| r.iter().map(String::as_str).collect())
        .collect();
    Some(table_from_cells(&borrowed, &aligns, padded))
}

/// 选中内容（改写后的 HTML）对应的 Markdown。
pub fn html_to_markdown(html: &str) -> String {
    let doc = parse_fragment(html);
    let mut blocks = Vec::new();
    collect_blocks(*doc.root_element(), &mut blocks);
    blocks.join("\n\n").trim().to_owned()
}

fn is_block(name: &str) -> bool {
    matches!(
        name,
        "p" | "div"
            | "ul"
            | "ol"
            | "pre"
            | "blockquote"
            | "h1"
            | "h2"
            | "h3"
            | "h4"
            | "h5"
            | "h6"
            | "table"
            | "hr"
    )
}

fn collect_blocks(node: NodeRef<Node>, out: &mut Vec<String>) {
    let mut inline = String::new();
    for child in node.children() {
        let block = ElementRef::wrap(child).filter(|e| is_block(e.value().name()));
        let Some(e) = block else {
            inline_node(child, &mut inline);
            continue;
        };
        if !inline.trim().is_empty() {
            out.push(inline.trim().to_owned());
        }
        inline.clear();
        block_markdown(e, out);
    }
    if !inline.trim().is_empty() {
        out.push(inline.trim().to_owned());
    }
}

fn block_markdown(e: ElementRef, out: &mut Vec<String>) {
    let name = e.value().name();
    match name {
        "pre" => {
            let text: String = e.text().collect();
            out.push(format!("```\n{}\n```", text.trim_end_matches('\n')));
        }
        "ul" | "ol" => {
            let start: usize = e
                .value()
                .attr("start")
                .and_then(|s| s.parse().ok())
                .unwrap_or(1);
            let mut lines = Vec::new();
            for (i, li) in e
                .children()
                .filter_map(ElementRef::wrap)
                .filter(|c| c.value().name() == "li")
                .enumerate()
            {
                let mut parts = Vec::new();
                collect_blocks(*li, &mut parts);
                let body = parts.join("\n").replace('\n', "\n  ");
                if name == "ol" {
                    lines.push(format!("{}. {body}", start + i));
                } else {
                    lines.push(format!("- {body}"));
                }
            }
            out.push(lines.join("\n"));
        }
        "blockquote" => {
            let mut parts = Vec::new();
            collect_blocks(*e, &mut parts);
            out.push(
                parts
                    .join("\n\n")
                    .lines()
                    .map(|l| format!("> {l}").trim_end().to_owned())
                    .collect::<Vec<_>>()
                    .join("\n"),
            );
        }
        "h1" | "h2" | "h3" | "h4" | "h5" | "h6" => {
            let level = name[1..].parse().unwrap_or(1);
            let mut text = String::new();
            inline_markdown(e, &mut text);
            out.push(format!("{} {}", "#".repeat(level), text.trim()));
        }
        "hr" => out.push("---".to_owned()),
        "table" => {
            if let Some(md) = table_markdown(&e.html(), 0, false) {
                out.push(md);
            }
        }
        _ if has_class(e, "table-tools") || has_class(e, "quote-head") => {}
        _ => collect_blocks(*e, out),
    }
}

fn inline_markdown(e: ElementRef, out: &mut String) {
    for child in e.children() {
        inline_node(child, out);
    }
}

fn inline_node(node: NodeRef<Node>, out: &mut String) {
    match node.value() {
        Node::Text(text) => out.push_str(text),
        Node::Element(_) => {
            let Some(e) = ElementRef::wrap(node) else {
                return;
            };
            let wrapped = |out: &mut String, mark: &str| {
                let mut inner = String::new();
                inline_markdown(e, &mut inner);
                out.push_str(mark);
                out.push_str(&inner);
                out.push_str(mark);
            };
            match e.value().name() {
                "strong" | "b" => wrapped(out, "**"),
                "em" | "i" => wrapped(out, "*"),
                "del" | "s" => wrapped(out, "~~"),
                "code" => {
                    let text: String = e.text().collect();
                    let mark = if has_class(e, "tex") { "$$" } else { "`" };
                    out.push_str(mark);
                    out.push_str(&text);
                    out.push_str(mark);
                }
                "br" => out.push_str("<br />"),
                "a" => {
                    let mut inner = String::new();
                    inline_markdown(e, &mut inner);
                    match e.value().attr("href") {
                        Some(href) if href != inner => {
                            out.push_str(&format!("[{inner}]({href})"));
                        }
                        _ => out.push_str(&inner),
                    }
                }
                "span" if has_class(e, "mention") => {
                    let text: String = e.text().collect();
                    let name = text.trim_start_matches('@');
                    match e.value().attr("data-user-id") {
                        Some(id) if id != "*" => out.push_str(&format!("@**{name}|{id}**")),
                        _ => out.push_str(&format!("@**{name}**")),
                    }
                }
                _ if has_class(e, "img-preview") => out.push_str("[图片]"),
                _ => inline_markdown(e, out),
            }
        }
        _ => {}
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn run(html: &str) -> Rendered {
        render(
            html,
            &RenderOptions {
                me: None,
                local_image: &|_| None,
            },
        )
    }

    fn html_of(html: &str) -> String {
        run(html)
            .segments
            .into_iter()
            .filter_map(|s| match s {
                Segment::Html(h) => Some(h),
                Segment::File(_) => None,
            })
            .collect()
    }

    #[test]
    fn drops_scripts_and_event_attributes() {
        let out = html_of(r#"<p onclick="x()">a<script>evil()</script></p><iframe src="x"></iframe>"#);
        assert_eq!(out, "<p>a</p>");
    }

    #[test]
    fn inline_image_becomes_preview_with_fitted_size() {
        let html = r#"<div class="message_inline_image"><a href="/user_uploads/1/a/x.png"><img data-original-dimensions="960x480" src="/user_uploads/thumbnail/1/a/x.png/840x560.webp"></a></div>"#;
        let result = run(html);
        let Segment::Html(out) = &result.segments[0] else {
            panic!("应当是 HTML");
        };
        assert!(out.contains("img-preview block"));
        assert!(out.contains(r#"data-index="0""#));
        assert!(out.contains("width:240px;height:120px"));
        assert_eq!(
            result.pending,
            vec!["/user_uploads/thumbnail/1/a/x.png/840x560.webp"]
        );
    }

    #[test]
    fn any_element_with_inline_image_class_is_a_preview() {
        let html = r#"<span class="message_inline_image foo"><img src="/a.png"></span>"#;
        assert!(html_of(html).contains(r#"img-preview"#));
    }

    #[test]
    fn local_image_is_used_directly() {
        let result = render(
            r#"<p><img class="inline-image" src="/a.png"></p>"#,
            &RenderOptions {
                me: None,
                local_image: &|url| Some(format!("http://local{url}")),
            },
        );
        assert!(result.pending.is_empty());
        assert_eq!(
            result.segments,
            vec![Segment::Html(
                "<p><span class=\"img-preview\" data-index=\"0\" style=\"width:240px;height:135px\"><img data-remote=\"/a.png\" src=\"http://local/a.png\" alt=\"\"></span></p>".to_owned()
            )]
        );
    }

    #[test]
    fn upload_only_paragraph_becomes_file_card() {
        let result = run(r#"<p>看</p><p><a href="/user_uploads/1/a/r.pdf">r.pdf</a></p><p>尾</p>"#);
        assert_eq!(result.segments.len(), 3);
        let Segment::File(link) = &result.segments[1] else {
            panic!("应当是文件");
        };
        assert_eq!(link.path, "/user_uploads/1/a/r.pdf");
        assert_eq!(link.file_name, "r.pdf");
    }

    #[test]
    fn file_name_link_is_replaced_by_its_preview() {
        let html = r#"<p><a href="/user_uploads/1/a/x.png">x.png</a></p><div class="message_inline_image"><a href="/user_uploads/1/a/x.png"><img src="/t.webp"></a></div>"#;
        let out = run(html);
        assert_eq!(out.segments.len(), 1);
        assert!(matches!(&out.segments[0], Segment::Html(h) if h.contains("img-preview")));
    }

    #[test]
    fn video_preview_becomes_file_card() {
        let html = r#"<div class="message_inline_image message_inline_video"><a href="/user_uploads/1/a/v.mp4"><video src="/x"></video></a></div>"#;
        let out = run(html);
        assert!(matches!(&out.segments[0], Segment::File(l) if l.file_name == "v.mp4"));
    }

    #[test]
    fn table_gets_host_tools_and_scroll() {
        let out = html_of("<table><thead><tr><th style=\"text-align: center;\">a</th></tr></thead><tbody><tr><td>x&lt;br /&gt;y</td></tr></tbody></table>");
        assert!(out.contains(r#"class="table-host" data-table="0""#));
        assert!(out.contains("table-more"));
        assert!(out.contains("table-scroll scroll"));
        assert!(out.contains(r#"<th style="text-align:center">a</th>"#));
        assert!(out.contains("<td>x<br>y</td>"));
    }

    #[test]
    fn code_block_keeps_plain_text() {
        let out = html_of(r#"<div class="codehilite"><pre><span></span><code><span class="k">fn</span> a&lt;b&gt;
</code></pre></div>"#);
        assert_eq!(
            out,
            "<pre class=\"code-block scroll\"><code>fn a&lt;b&gt;</code></pre>"
        );
    }

    #[test]
    fn tex_falls_back_to_source() {
        let out = html_of(r#"<span class="katex"><span class="katex-mathml"><math><semantics><annotation encoding="application/x-tex">x^2</annotation></semantics></math></span><span class="katex-html">x2</span></span>"#);
        assert_eq!(out, "<code class=\"tex\">x^2</code>");
    }

    #[test]
    fn spoiler_defaults_header() {
        let out = html_of(r#"<div class="spoiler-block"><div class="spoiler-header"></div><div class="spoiler-content" aria-hidden="true"><p>秘密</p></div></div>"#);
        assert!(out.contains("折叠"));
        assert!(out.contains("<div class=\"spoiler-body\"><p>秘密</p></div>"));
    }

    #[test]
    fn quote_header_is_merged() {
        let out = html_of(r##"<blockquote><p><span class="user-mention silent" data-user-id="9">张三</span> <a href="/#narrow/channel/1-a/topic/b/near/12">说</a>：</p><p>内容</p></blockquote>"##);
        assert!(out.contains("quote-author\">张三"));
        assert!(out.contains("near/12"));
        assert!(out.ends_with("<p>内容</p></blockquote>"));
        assert!(!out.contains(">说<"));
    }

    #[test]
    fn emoji_class_becomes_unicode() {
        let out = html_of(r#"<p><span aria-label="smile" class="emoji emoji-1f604" role="img" title="smile">:smile:</span></p>"#);
        assert!(out.contains("😄"));
        assert!(!out.contains(":smile:"));
    }

    #[test]
    fn me_message_replaces_prefix() {
        let result = render(
            "<p>/me 挥手</p>",
            &RenderOptions {
                me: Some("张三"),
                local_image: &|_| None,
            },
        );
        assert_eq!(
            result.segments,
            vec![Segment::Html(
                "<p><span class=\"me-name\">张三</span> 挥手</p>".to_owned()
            )]
        );
    }

    #[test]
    fn table_source_keeps_alignment_and_formatting() {
        let html = r#"<table><thead><tr><th>甲</th><th style="text-align: right;">乙</th></tr></thead><tbody><tr><td><strong>1</strong></td><td>2</td></tr></tbody></table>"#;
        let plain = table_markdown(html, 0, false).expect("表格存在");
        assert!(plain.contains("**1**"));
        assert!(plain.lines().nth(1).is_some_and(|l| l.contains(":")));
        assert!(table_markdown(html, 1, false).is_none());
    }

    #[test]
    fn selection_converts_to_markdown() {
        let html = r#"<p>a <strong>b</strong> <code>c</code> <a href="http://x">d</a></p><ul><li>一</li><li>二</li></ul><blockquote class="quote"><p>q</p></blockquote><pre class="code-block"><code>e
f</code></pre>"#;
        assert_eq!(
            html_to_markdown(html),
            "a **b** `c` [d](http://x)\n\n- 一\n- 二\n\n> q\n\n```\ne\nf\n```"
        );
    }
}
