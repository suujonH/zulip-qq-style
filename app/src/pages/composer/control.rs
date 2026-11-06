//! 输入框的控制器：连接编辑区 JS、内容模型和会话。JS 消息的处理、发送、草稿、输入中通知、补全、上传都在这里。

use super::ComposerHandle;
use super::complete::{self, Item, Kind, Trigger};
use super::core::{
    CellPos, EditorCore, Sel, link_markdown, strip_stale_uploads, upload_placeholder,
};
use super::dialogs;
use super::render::Lookup;
use crate::services::{plugins, services};
use crate::ui::{Overlays, TextPrompt};
use crate::windows;
use chrono::{Local, TimeZone};
use dioxus::dioxus_core::{Runtime, current_scope_id, spawn_forever};
use dioxus::prelude::*;
use serde::Deserialize;
use serde_json::{Value, json};
use std::collections::{HashMap, HashSet};
use std::future::Future;
use std::io::Write as _;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;
use tokio::sync::mpsc::{UnboundedReceiver, UnboundedSender, unbounded_channel};
use zulip_core::ConversationKey;
use zulip_core::html::to_preview;
use zulip_core::markdown::format::{self, FormatKind};
use zulip_core::markdown::table::{self, TableAlign};
use zulip_core::markdown::{SpanKind, quote};
use zulip_core::offsets;
use zulip_core::text::mention::match_rank;
use zulip_session::{DeleteTarget, Session};
use zulip_store::AccountSettings;

const EDITOR_JS: &str = include_str!("editor.js");
const DRAFT_DELAY: Duration = Duration::from_secs(1);
const PROGRESS_INTERVAL: Duration = Duration::from_millis(100);
const ACTIVE_KINDS: [FormatKind; 7] = [
    FormatKind::Bold,
    FormatKind::Italic,
    FormatKind::Code,
    FormatKind::Link,
    FormatKind::Quote,
    FormatKind::NumberedList,
    FormatKind::BulletedList,
];

/// 上次从输入框复制的（显示的文字，Markdown）：粘贴回来时恢复格式。
static LAST_COPY: Mutex<Option<(String, String)>> = Mutex::new(None);

#[derive(Deserialize)]
#[serde(tag = "t", rename_all = "kebab-case")]
enum Incoming {
    Ready,
    Sync {
        seq: u64,
        rev: u64,
        text: String,
        s: usize,
        e: usize,
    },
    Cell {
        seq: u64,
        rev: u64,
        table: usize,
        row: usize,
        col: usize,
        value: String,
        s: usize,
        e: usize,
    },
    Sel {
        rev: u64,
        s: usize,
        e: usize,
        cell: Option<[usize; 3]>,
        x: f64,
        y: f64,
    },
    Cmd {
        cmd: String,
    },
    Comp {
        on: bool,
    },
    Focus {
        on: bool,
    },
    Paste {
        text: String,
    },
    Insert {
        text: String,
    },
    File {
        id: u64,
        name: String,
        hex: String,
        last: bool,
    },
    TooBig,
    Act {
        act: String,
        q: usize,
    },
    Tablerect {
        rect: Option<TableRect>,
    },
    Widths {
        table: usize,
        required: Vec<f64>,
        minimum: Vec<f64>,
        available: f64,
    },
}

#[derive(Clone, Copy, Debug, Deserialize, PartialEq)]
pub struct TableRect {
    pub top: f64,
    pub left: f64,
    pub width: f64,
}

/// 工具栏和发送按钮需要的状态。
#[derive(Clone, Default, PartialEq)]
pub struct View {
    pub blank: bool,
    pub quote_only: bool,
    pub active: Vec<FormatKind>,
    pub heading: u8,
    pub table: Option<(usize, usize)>,
    pub align: TableAlign,
}

#[derive(Clone, PartialEq)]
pub struct Popup {
    pub kind: Kind,
    pub start: usize,
    pub query: String,
    pub items: Vec<Item>,
    pub selected: usize,
    pub x: f64,
    pub y: f64,
}

#[derive(Clone, PartialEq)]
pub struct EditState {
    pub id: i64,
    pub preview: String,
    pub original: String,
    stash: String,
}

/// 正在上传的文件。
#[derive(Clone)]
pub struct UploadRow {
    pub id: u64,
    pub name: String,
    pub progress: f64,
    placeholder: String,
    abort: tokio::task::AbortHandle,
}

struct PastedFile {
    path: PathBuf,
    name: String,
    file: std::fs::File,
}

#[derive(Clone, Copy)]
pub struct Ctl {
    pub core: CopyValue<EditorCore>,
    session: CopyValue<Arc<Session>>,
    key: CopyValue<ConversationKey>,
    pub overlays: Overlays,
    pub handle: ComposerHandle,
    pub dom_id: CopyValue<String>,
    scope: ScopeId,
    tx: CopyValue<UnboundedSender<Value>>,
    rx: CopyValue<Option<UnboundedReceiver<Value>>>,
    /// JS 最后一次输入的序号：回复里带上，JS 丢弃过期的补丁。
    seq: CopyValue<u64>,
    composing: CopyValue<bool>,
    focused: CopyValue<bool>,
    /// 输入法组字期间到达的异步修改（上传完成等），组字结束再做。
    deferred: CopyValue<Vec<Box<dyn FnOnce(Ctl)>>>,
    caret: CopyValue<(f64, f64)>,
    dismissed: CopyValue<Option<usize>>,
    draft_gen: CopyValue<u64>,
    fetching: CopyValue<HashSet<String>>,
    pasting: CopyValue<HashMap<u64, PastedFile>>,
    subscribers: CopyValue<Option<HashSet<i64>>>,
    next_upload: CopyValue<u64>,
    pub view: Signal<View>,
    pub popup: Signal<Option<Popup>>,
    pub table_rect: Signal<Option<TableRect>>,
    pub uploads: Signal<Vec<UploadRow>>,
    pub sending: Signal<bool>,
    pub edit: Signal<Option<EditState>>,
    pub source: Signal<bool>,
}

impl PartialEq for Ctl {
    fn eq(&self, other: &Self) -> bool {
        self.core == other.core
    }
}

fn next_dom_id() -> String {
    static NEXT: AtomicU64 = AtomicU64::new(0);
    format!("composer-{}", NEXT.fetch_add(1, Ordering::Relaxed))
}

fn build_lookup(session: &Session, images: HashMap<String, String>) -> Lookup {
    let users = session.users();
    Lookup {
        user_names: users.iter().map(|u| u.full_name.to_lowercase()).collect(),
        users: users.into_iter().map(|u| (u.id, u.full_name)).collect(),
        groups: HashSet::new(),
        channels: session
            .channels()
            .iter()
            .map(|c| c.name.to_lowercase())
            .collect(),
        emoji: session.emoji_catalog(),
        images,
    }
}

/// 创建控制器。只在组件里调用一次。
pub fn use_ctl(
    session: Arc<Session>,
    key: ConversationKey,
    overlays: Overlays,
    handle: ComposerHandle,
) -> Ctl {
    let view = use_signal(View::default);
    let popup = use_signal(|| None);
    let table_rect = use_signal(|| None);
    let uploads = use_signal(Vec::new);
    let sending = use_signal(|| false);
    let edit = use_signal(|| None);
    let source_signal = use_signal(|| false);
    use_hook(move || {
        let (tx, rx) = unbounded_channel();
        let source =
            AccountSettings::new(&services().db, session.account_id()).composer_source_mode();
        let draft = session
            .draft(&key)
            .map(|d| strip_stale_uploads(&d.content))
            .unwrap_or_default();
        let mut initial_source = source_signal;
        initial_source.set(source);
        let core = EditorCore::new(draft, source, build_lookup(&session, HashMap::new()));
        let ctl = Ctl {
            core: CopyValue::new(core),
            session: CopyValue::new(session),
            key: CopyValue::new(key),
            overlays,
            handle,
            dom_id: CopyValue::new(next_dom_id()),
            scope: current_scope_id(),
            tx: CopyValue::new(tx),
            rx: CopyValue::new(Some(rx)),
            seq: CopyValue::new(0),
            composing: CopyValue::new(false),
            focused: CopyValue::new(false),
            deferred: CopyValue::new(Vec::new()),
            caret: CopyValue::new((0.0, 0.0)),
            dismissed: CopyValue::new(None),
            draft_gen: CopyValue::new(0),
            fetching: CopyValue::new(HashSet::new()),
            pasting: CopyValue::new(HashMap::new()),
            subscribers: CopyValue::new(None),
            next_upload: CopyValue::new(0),
            view,
            popup,
            table_rect,
            uploads,
            sending,
            edit,
            source: source_signal,
        };
        ctl.update_view();
        ctl.load_subscribers();
        ctl.fetch_assets();
        ctl
    })
}

impl Ctl {
    pub fn session(self) -> Arc<Session> {
        self.session.peek().clone()
    }

    pub fn key(self) -> ConversationKey {
        self.key.peek().clone()
    }

    pub fn spawn(self, task: impl Future<Output = ()> + 'static) {
        let scope = self.scope;
        Runtime::current().in_scope(scope, || {
            spawn(task);
        });
    }

    fn is_editing(self) -> bool {
        self.edit.peek().is_some()
    }

    fn out(self, message: Value) {
        let _ = self.tx.peek().send(message);
    }

    // ───────── JS 连接 ─────────

    /// 编辑区挂载后调用。
    pub fn start(self) {
        let Some(mut rx) = self.rx.write_unchecked().take() else {
            return;
        };
        let id = self.dom_id.peek().clone();
        let script = format!(
            "{EDITOR_JS}; const ed = window.zulipComposer.attach({id:?}, (m) => dioxus.send(m)); for (;;) {{ ed.apply(await dioxus.recv()); }}"
        );
        self.spawn(async move {
            let mut eval = document::eval(&script);
            loop {
                tokio::select! {
                    message = eval.recv::<Value>() => match message {
                        Ok(message) => match serde_json::from_value::<Incoming>(message) {
                            Ok(message) => self.handle(message).await,
                            Err(error) => log::warn!("输入框消息无法解析：{error}"),
                        },
                        Err(error) => {
                            log::warn!("输入框脚本已结束：{error}");
                            return;
                        }
                    },
                    Some(message) = rx.recv() => {
                        let _ = eval.send(message);
                    }
                }
            }
        });
    }

    /// 组件销毁时：解除 JS，保存草稿，结束输入中。
    pub fn shutdown(self) {
        let id = self.dom_id.peek().clone();
        document::eval(&format!(
            "window.zulipComposer && window.zulipComposer.detach({id:?});"
        ));
        let session = self.session();
        let key = self.key();
        session.stop_typing();
        let text = match self.edit.peek().as_ref() {
            Some(edit) => edit.stash.clone(),
            None => self.core.peek().text().to_owned(),
        };
        spawn_forever(async move {
            if let Err(error) = session.save_draft(&key, &text).await {
                log::warn!("保存草稿失败：{error}");
            }
        });
    }

    fn options(self) -> Value {
        let session = self.session();
        json!({
            "popup": self.popup.peek().is_some(),
            "source": self.core.peek().source,
            "enterSends": session.settings().enter_sends,
            "editing": self.is_editing(),
            "maxBytes": i64::from(session.realm().max_file_upload_size_mib) * 1024 * 1024,
        })
    }

    pub fn send_options(self) {
        self.out(json!({ "opts": self.options() }));
    }

    /// 让编辑区重新得到焦点（菜单、对话框关闭后）。
    pub fn refocus(self) {
        self.out(json!({ "focus": true }));
    }

    // 把内容发给 JS。`force` 表示程序改了内容，否则只是对输入的回复
    fn push(self, force: bool, focus: bool, keep_scroll: bool) {
        let core = self.core.peek();
        let mut message = json!({
            "blocks": core.blocks(),
            "rev": core.rev,
            "seq": *self.seq.peek(),
            "force": force,
        });
        if force && (focus || *self.focused.peek()) {
            let (s, e) = core.sel_utf16();
            message["sel"] = json!({
                "s": s,
                "e": e,
                "cell": core.sel.cell.map(|p| [p.table, p.row, p.col]),
            });
            message["focus"] = json!(focus);
        }
        if keep_scroll {
            message["keepScroll"] = json!(true);
        }
        drop(core);
        self.out(message);
    }

    fn push_reply_with_sel(self) {
        let core = self.core.peek();
        let (s, e) = core.sel_utf16();
        let message = json!({
            "blocks": core.blocks(),
            "rev": core.rev,
            "seq": *self.seq.peek(),
            "force": false,
            "sel": { "s": s, "e": e, "cell": core.sel.cell.map(|p| [p.table, p.row, p.col]) },
        });
        drop(core);
        self.out(message);
    }

    /// 用户操作引起的修改。
    pub fn edit(self, change: impl FnOnce(&mut EditorCore)) {
        change(&mut self.core.write_unchecked());
        self.push(true, true, false);
        self.after_change(true);
    }

    /// 异步完成的修改：组字期间先放着，否则会打断输入法。
    fn when_idle(self, change: impl FnOnce(Ctl) + 'static) {
        if *self.composing.peek() {
            self.deferred.write_unchecked().push(Box::new(change));
        } else {
            change(self);
        }
    }

    fn after_change(self, text_changed: bool) {
        self.update_view();
        if text_changed {
            self.fetch_assets();
            if !self.is_editing() {
                let session = self.session();
                let has_text = !self.core.peek().text().is_empty();
                session.on_compose_input(&self.key.peek(), has_text);
                self.schedule_draft();
            }
        }
        self.refresh_popup();
    }

    fn update_view(self) {
        let core = self.core.peek();
        let text = core.text();
        let view = View {
            blank: text.trim().is_empty(),
            quote_only: quote::has_only_quotes(text),
            active: if core.source {
                Vec::new()
            } else {
                ACTIVE_KINDS
                    .into_iter()
                    .filter(|k| core.is_active(*k))
                    .collect()
            },
            heading: core.heading_level(),
            table: core.active_table_size(),
            align: core.active_align(),
        };
        drop(core);
        let mut signal = self.view;
        if *signal.peek() != view {
            signal.set(view);
        }
    }

    async fn handle(self, message: Incoming) {
        match message {
            Incoming::Ready => {
                self.send_options();
                self.push(true, true, false);
            }
            Incoming::Sync {
                seq,
                rev,
                text,
                s,
                e,
            } => {
                if rev != self.core.peek().rev {
                    return;
                }
                *self.seq.write_unchecked() = seq;
                let sel = Sel {
                    start: offsets::from_utf16(&text, s),
                    end: offsets::from_utf16(&text, e),
                    cell: None,
                };
                let moved = self.core.write_unchecked().sync(text, sel);
                if moved {
                    self.push_reply_with_sel();
                } else {
                    self.push(false, false, false);
                }
                self.after_change(true);
            }
            Incoming::Cell {
                seq,
                rev,
                table,
                row,
                col,
                value,
                s,
                e,
            } => {
                if rev != self.core.peek().rev {
                    return;
                }
                *self.seq.write_unchecked() = seq;
                let (start, end) = (
                    offsets::from_utf16(&value, s),
                    offsets::from_utf16(&value, e),
                );
                self.core.write_unchecked().sync_cell(
                    CellPos { table, row, col },
                    value,
                    start,
                    end,
                );
                self.push(false, false, false);
                self.after_change(true);
            }
            Incoming::Sel {
                rev,
                s,
                e,
                cell,
                x,
                y,
            } => {
                if rev != self.core.peek().rev {
                    return;
                }
                *self.caret.write_unchecked() = (x, y);
                let cell = cell.map(|[table, row, col]| CellPos { table, row, col });
                let sel = self.core.peek().sel_from_utf16(s, e, cell);
                let rerender = self.core.write_unchecked().set_sel(sel);
                if rerender {
                    self.core.write_unchecked().rev += 1;
                    self.push(true, false, false);
                }
                self.after_change(false);
            }
            Incoming::Cmd { cmd } => self.command(&cmd),
            Incoming::Comp { on } => {
                *self.composing.write_unchecked() = on;
                if !on {
                    let deferred = std::mem::take(&mut *self.deferred.write_unchecked());
                    for change in deferred {
                        change(self);
                    }
                }
            }
            Incoming::Focus { on } => {
                *self.focused.write_unchecked() = on;
                if !on {
                    self.close_popup();
                }
            }
            Incoming::Paste { text } => self.paste_text(&text),
            Incoming::Insert { text } => self.edit(|c| c.insert(&text)),
            Incoming::File {
                id,
                name,
                hex,
                last,
            } => self.pasted_chunk(id, &name, &hex, last),
            Incoming::TooBig => self.too_big(),
            Incoming::Act { act, q } => self.quote_action(&act, q),
            Incoming::Tablerect { rect } => {
                let mut signal = self.table_rect;
                signal.set(rect);
            }
            Incoming::Widths {
                table: index,
                required,
                minimum,
                available,
            } => {
                let cols = table::fit_columns(&required, available, Some(&minimum));
                self.out(json!({ "widths": { "table": index, "cols": cols } }));
            }
        }
    }

    fn command(self, cmd: &str) {
        match cmd {
            "copy" => self.copy(),
            "cut" => self.cut(),
            "delete-selection" => self.edit(|c| c.insert("")),
            "undo" => {
                if self.core.write_unchecked().undo() {
                    self.push(true, true, false);
                    self.after_change(true);
                }
            }
            "redo" => {
                if self.core.write_unchecked().redo() {
                    self.push(true, true, false);
                    self.after_change(true);
                }
            }
            "send" => self.send(),
            "cancel-edit" => {
                let mut editing = self.handle.editing;
                editing.set(None);
            }
            "popup-up" => self.move_popup(-1),
            "popup-down" => self.move_popup(1),
            "popup-accept" => self.accept_popup(None),
            "popup-close" => self.dismiss_popup(),
            "bold" => self.edit(|c| c.format(FormatKind::Bold)),
            "italic" => self.edit(|c| c.format(FormatKind::Italic)),
            "link" => self.edit(|c| c.format(FormatKind::Link)),
            "code" => self.edit(|c| c.format(FormatKind::Code)),
            "unprefix" => self.edit(EditorCore::remove_prefix),
            "merge-up" | "merge-down" => self.merge_blank_line(cmd == "merge-up"),
            "table-row-below" => self.edit(|c| c.table_op(|t, caret| t.insert_row(caret, true))),
            "table-row-up" => self.edit(|c| c.table_op(|t, caret| t.move_row(caret, -1))),
            "table-row-down" => self.edit(|c| c.table_op(|t, caret| t.move_row(caret, 1))),
            "table-col-left" => self.edit(|c| c.table_op(|t, caret| t.move_column(caret, -1))),
            "table-col-right" => self.edit(|c| c.table_op(|t, caret| t.move_column(caret, 1))),
            "table-del-row" => self.edit(|c| c.table_op(|t, caret| Some(t.delete_row(caret)))),
            other => {
                if let Some(level) = other.strip_prefix("heading-").and_then(|l| l.parse().ok()) {
                    self.edit(|c| c.heading(level));
                }
            }
        }
    }

    // 空行紧挨表格：删掉这一行，光标回到表格
    fn merge_blank_line(self, up: bool) {
        self.edit(|core| {
            let caret = core.sel.start;
            if up && caret > 0 {
                core.delete_range(caret - 1, caret);
            } else if !up {
                core.delete_range(caret, caret + 1);
            }
        });
    }

    fn quote_action(self, act: &str, index: usize) {
        match act {
            "toggle" => {
                self.core.write_unchecked().toggle_quote(index);
                self.push(true, false, false);
                self.render_quotes();
            }
            "edit" => {
                self.core.write_unchecked().edit_quote(index);
                self.push(true, true, false);
            }
            "end-edit" => {
                self.core.write_unchecked().end_quote_edit();
                self.push(true, true, false);
            }
            "delete" => self.edit(|c| c.delete_quote(index)),
            "jump" => {
                if let Some(id) = self.core.peek().quote_message_id(index) {
                    windows::open_chat(self.session().account_id(), self.key(), Some(id));
                }
            }
            _ => {}
        }
    }

    // 展开的引用显示发送后的样子
    fn render_quotes(self) {
        for (src, body) in self.core.peek().quotes_to_render() {
            let session = self.session();
            self.spawn(async move {
                if let Ok(html) = session.render_preview(&body).await {
                    self.core
                        .write_unchecked()
                        .rendered_quotes
                        .insert(src, html);
                    self.when_idle(|ctl| {
                        ctl.core.write_unchecked().rev += 1;
                        ctl.push(true, false, false);
                    });
                }
            });
        }
    }

    // ───────── 复制粘贴 ─────────

    pub fn has_selection(self) -> bool {
        !self.core.peek().sel.is_collapsed()
    }

    pub fn copy(self) {
        let core = self.core.peek();
        let markdown = core.selected_text();
        if markdown.is_empty() {
            return;
        }
        let plain = core.plain_text(&markdown);
        drop(core);
        self.remember_copy(&plain, &markdown);
        self.overlays.copy_text(&plain);
    }

    fn remember_copy(self, plain: &str, markdown: &str) {
        if let Ok(mut last) = LAST_COPY.lock() {
            *last = Some((plain.to_owned(), markdown.to_owned()));
        }
    }

    pub fn cut(self) {
        if self.has_selection() {
            self.copy();
            self.edit(|c| c.insert(""));
        }
    }

    /// 复制表格的原文。
    pub fn copy_table(self) {
        if let Some(source) = self.core.peek().active_table_source() {
            self.remember_copy(&source, &source);
            self.overlays.copy_text(&source);
        }
    }

    fn paste_text(self, text: &str) {
        let text = text.replace("\r\n", "\n").replace('\r', "\n");
        if text.is_empty() {
            return;
        }
        let copied = LAST_COPY.lock().ok().and_then(|last| {
            last.as_ref()
                .filter(|(plain, _)| plain.replace("\r\n", "\n") == text)
                .map(|(_, md)| md.clone())
        });
        let selected = self.core.peek().selected_text();
        self.edit(|core| match copied {
            Some(markdown) => core.insert_block(&markdown),
            None if is_url(&text) && !selected.is_empty() && !selected.contains('\n') => {
                core.insert(&format!("[{selected}]({text})"));
            }
            None => core.insert_block(&text),
        });
    }

    /// 右键菜单的粘贴：先看图片，再看文字。
    pub fn paste_from_clipboard(self) {
        if let Some(png) = crate::platform::paste_image().and_then(|image| image.to_png()) {
            let name = screenshot_name();
            match write_paste_file(&name, &png) {
                Ok(path) => self.upload(path, name),
                Err(error) => self.overlays.report_error("粘贴失败", error),
            }
            return;
        }
        match crate::platform::paste_text() {
            Some(text) => self.paste_text(&text),
            None => self.refocus(),
        }
    }

    fn pasted_chunk(self, id: u64, name: &str, hex: &str, last: bool) {
        let result = (|| -> std::io::Result<()> {
            let mut pasting = self.pasting.write_unchecked();
            if !pasting.contains_key(&id) {
                let name = if name.is_empty() {
                    screenshot_name()
                } else {
                    name.to_owned()
                };
                let path = unique_paste_path(&name)?;
                let file = std::fs::File::create(&path)?;
                pasting.insert(id, PastedFile { path, name, file });
            }
            if let Some(entry) = pasting.get_mut(&id) {
                entry.file.write_all(&decode_hex(hex))?;
            }
            Ok(())
        })();
        if let Err(error) = result {
            self.pasting.write_unchecked().remove(&id);
            self.overlays.report_error("粘贴失败", error);
            return;
        }
        if last && let Some(entry) = self.pasting.write_unchecked().remove(&id) {
            self.upload(entry.path, entry.name);
        }
    }

    // ───────── 上传 ─────────

    fn too_big(self) {
        let max = self.session().realm().max_file_upload_size_mib;
        self.overlays.toast(format!("文件超过 {max}MB，无法发送"));
    }

    /// 上传文件：在光标处放占位，完成后换成链接。
    pub fn upload(self, path: PathBuf, name: String) {
        let session = self.session();
        let max =
            u64::try_from(session.realm().max_file_upload_size_mib).unwrap_or(0) * 1024 * 1024;
        let total = match std::fs::metadata(&path) {
            Ok(meta) => meta.len(),
            Err(error) => {
                self.overlays.report_error("上传失败", error);
                return;
            }
        };
        if total > max {
            self.too_big();
            return;
        }
        let placeholder = {
            let core = self.core.peek();
            upload_placeholder(&name, |p| core.text().contains(p))
        };
        let inserted = placeholder.clone();
        self.when_idle(move |ctl| ctl.edit(|c| c.insert_smart(&inserted)));

        let sent = Arc::new(AtomicU64::new(0));
        let progress = sent.clone();
        let local = path.clone();
        let task = {
            let session = session.clone();
            tokio::spawn(async move {
                session
                    .upload_file(&local, move |bytes| {
                        progress.store(bytes, Ordering::Relaxed)
                    })
                    .await
            })
        };
        let id = *self.next_upload.peek();
        *self.next_upload.write_unchecked() = id + 1;
        let mut uploads = self.uploads;
        uploads.write().push(UploadRow {
            id,
            name: name.clone(),
            progress: 0.0,
            placeholder: placeholder.clone(),
            abort: task.abort_handle(),
        });
        self.update_view();
        self.spawn(async move {
            let mut task = task;
            let result = loop {
                tokio::select! {
                    result = &mut task => break result,
                    () = tokio::time::sleep(PROGRESS_INTERVAL) => {
                        let done = sent.load(Ordering::Relaxed) as f64 / total.max(1) as f64;
                        if let Some(row) = uploads.write().iter_mut().find(|r| r.id == id) {
                            row.progress = done;
                        }
                    }
                }
            };
            uploads.write().retain(|r| r.id != id);
            match result {
                Ok(Ok(uploaded)) => {
                    let label = uploaded.filename.clone().unwrap_or(name);
                    let link = link_markdown(&label, &uploaded.url);
                    self.when_idle(move |ctl| {
                        if let Some(local) = crate::shell::url_for(&path) {
                            let core = &mut *ctl.core.write_unchecked();
                            core.lookup.images.insert(uploaded.url.clone(), local);
                        }
                        ctl.replace_async(&placeholder, &link);
                    });
                }
                Ok(Err(error)) => {
                    self.overlays.report_error("上传失败", error);
                    self.when_idle(move |ctl| ctl.replace_async(&placeholder, ""));
                }
                // 取消时占位已经去掉
                Err(_) => {}
            }
        });
    }

    fn replace_async(self, placeholder: &str, replacement: &str) {
        self.core
            .write_unchecked()
            .replace_placeholder(placeholder, replacement);
        self.push(true, false, false);
        self.after_change(true);
    }

    pub fn cancel_upload(self, id: u64) {
        let mut uploads = self.uploads;
        let row = uploads.peek().iter().find(|r| r.id == id).cloned();
        if let Some(row) = row {
            row.abort.abort();
            uploads.write().retain(|r| r.id != id);
            self.when_idle(move |ctl| ctl.replace_async(&row.placeholder, ""));
        }
    }

    /// 选择文件后上传。
    pub fn pick_and_upload(self, images_only: bool) {
        self.spawn(async move {
            let filters = [crate::platform::FileFilter {
                name: "图片",
                extensions: &["png", "jpg", "jpeg", "gif", "webp", "bmp"],
            }];
            let (title, filters): (&str, &[_]) = if images_only {
                ("发送图片", &filters)
            } else {
                ("发送文件", &[])
            };
            for path in crate::platform::pick_files(title, filters).await {
                self.upload_path(path);
            }
            self.refocus();
        });
    }

    /// 拖放或选择的文件。
    pub fn upload_path(self, path: PathBuf) {
        let name = path
            .file_name()
            .map_or_else(String::new, |n| n.to_string_lossy().into_owned());
        self.upload(path, name);
    }

    // 编辑区里要显示的图片：下载到本地后重新渲染
    fn fetch_assets(self) {
        let core = self.core.peek();
        let urls: Vec<String> = core
            .model()
            .spans
            .iter()
            .filter_map(|span| {
                let data = span.data.as_deref()?;
                match span.kind {
                    SpanKind::UploadImage => Some(data.to_owned()),
                    SpanKind::Emoji => core
                        .lookup
                        .emoji
                        .get(&data.to_lowercase())?
                        .image_url
                        .clone(),
                    _ => None,
                }
            })
            .filter(|url| {
                !core.lookup.images.contains_key(url) && !self.fetching.peek().contains(url)
            })
            .collect();
        drop(core);
        for url in urls {
            self.fetching.write_unchecked().insert(url.clone());
            let session = self.session();
            self.spawn(async move {
                if let Ok(path) = session.image_file(&url).await
                    && let Some(local) = crate::shell::url_for(&path)
                {
                    self.core.write_unchecked().lookup.images.insert(url, local);
                    self.push(false, false, false);
                }
            });
        }
    }

    /// 用户、频道、表情的数据变了：提及是否存在可能不同。
    pub fn refresh_lookup(self) {
        let images = self.core.peek().lookup.images.clone();
        let lookup = build_lookup(&self.session(), images);
        self.core.write_unchecked().set_lookup(lookup);
        self.push(false, false, false);
    }

    // ───────── 草稿、输入中 ─────────

    fn schedule_draft(self) {
        let generation = *self.draft_gen.peek() + 1;
        *self.draft_gen.write_unchecked() = generation;
        self.spawn(async move {
            tokio::time::sleep(DRAFT_DELAY).await;
            if *self.draft_gen.peek() == generation && !self.is_editing() {
                let text = self.core.peek().text().to_owned();
                if let Err(error) = self.session().save_draft(&self.key(), &text).await {
                    log::warn!("保存草稿失败：{error}");
                }
            }
        });
    }

    // ───────── 发送 ─────────

    pub fn can_send(self) -> bool {
        let view = self.view.peek();
        !*self.sending.peek()
            && self.uploads.peek().is_empty()
            && (self.is_editing() || (!view.blank && !view.quote_only))
    }

    fn max_length_ok(self, text: &str) -> bool {
        let max = self.session().realm().max_message_length;
        if max > 0 && text.chars().count() > max as usize {
            self.overlays.toast("消息过长");
            return false;
        }
        true
    }

    pub fn send(self) {
        if !self.can_send() {
            return;
        }
        if self.is_editing() {
            self.save_edit();
            return;
        }
        self.dispatch(move |session, key, text| async move {
            session.send_message(&key, &text).await.map(|_| ())
        });
    }

    /// 定时发送。
    pub fn send_later(self) {
        if !self.can_send() || self.is_editing() {
            return;
        }
        self.spawn(async move {
            let Some(at) = dialogs::ask_time(self.overlays, "定时发送").await else {
                self.refocus();
                return;
            };
            self.dispatch(move |session, key, text| async move {
                session.schedule_message(&key, &text, at).await.map(|_| ())
            });
            let label = Local
                .timestamp_opt(at, 0)
                .single()
                .map(|t| t.format("%Y/%m/%d %H:%M").to_string())
                .unwrap_or_default();
            self.overlays.toast(format!("将在 {label} 发送"));
        });
    }

    /// 发送并设置提醒。
    pub fn send_with_reminder(self) {
        if !self.can_send() || self.is_editing() {
            return;
        }
        self.spawn(async move {
            let Some(at) = dialogs::ask_time(self.overlays, "发送并提醒").await else {
                self.refocus();
                return;
            };
            self.dispatch(move |session, key, text| async move {
                let id = session.send_message(&key, &text).await?;
                session.create_reminder(id, at).await.map(|_| ())
            });
        });
    }

    /// 投票等不经过编辑区的消息。
    pub fn send_text(self, text: String) {
        let session = self.session();
        let key = self.key();
        let overlays = self.overlays;
        self.spawn(async move {
            match plugins().filter_outgoing(&session, &key, &text).await {
                Ok(text) => {
                    overlays
                        .attempt("发送失败", session.send_message(&key, &text))
                        .await;
                }
                Err(rejected) => overlays.toast(rejected.to_string()),
            }
        });
    }

    fn dispatch<F, E>(
        self,
        action: impl FnOnce(Arc<Session>, ConversationKey, String) -> F + 'static,
    ) where
        F: Future<Output = Result<(), E>> + 'static,
        E: std::fmt::Display,
    {
        let text = self.core.peek().text().trim_end().to_owned();
        if !self.max_length_ok(&text) {
            return;
        }
        let mut sending = self.sending;
        sending.set(true);
        self.close_popup();
        self.spawn(async move {
            let session = self.session();
            let key = self.key();
            let filtered = plugins().filter_outgoing(&session, &key, &text).await;
            sending.set(false);
            let text = match filtered {
                Ok(text) => text,
                Err(rejected) => {
                    self.overlays.toast(rejected.to_string());
                    return;
                }
            };
            self.when_idle(|ctl| {
                ctl.core.write_unchecked().reset(String::new());
                ctl.push(true, true, false);
                ctl.update_view();
                *ctl.draft_gen.write_unchecked() += 1;
            });
            session.stop_typing();
            if let Err(error) = session.save_draft(&key, "").await {
                log::warn!("删除草稿失败：{error}");
            }
            self.overlays
                .attempt("发送失败", action(session, key, text))
                .await;
        });
    }

    // ───────── 编辑消息 ─────────

    /// `ComposerHandle.editing` 变化时调用。
    pub fn on_editing(self, id: Option<i64>) {
        let current = self.edit.peek().as_ref().map(|e| e.id);
        if current == id {
            return;
        }
        let mut edit = self.edit;
        match id {
            Some(id) => self.spawn(async move {
                let session = self.session();
                let Some(raw) = self
                    .overlays
                    .attempt("编辑失败", session.raw_content(id))
                    .await
                else {
                    let mut editing = self.handle.editing;
                    editing.set(None);
                    return;
                };
                let preview = session
                    .message(id)
                    .map(|m| to_preview(&m.content_html))
                    .unwrap_or_default();
                let previous = edit.peek().as_ref().map(|e| e.stash.clone());
                let stash = previous.unwrap_or_else(|| self.core.peek().text().to_owned());
                let original = format::with_trailing_line(&raw);
                session.stop_typing();
                edit.set(Some(EditState {
                    id,
                    preview,
                    original: original.clone(),
                    stash,
                }));
                self.core.write_unchecked().reset(original);
                self.send_options();
                self.push(true, true, false);
                self.after_change(false);
            }),
            None => {
                let state = edit.peek().clone();
                if let Some(state) = state {
                    edit.set(None);
                    self.core.write_unchecked().reset(state.stash);
                    self.send_options();
                    self.push(true, true, false);
                    self.after_change(false);
                }
            }
        }
    }

    fn save_edit(self) {
        let Some(state) = self.edit.peek().clone() else {
            return;
        };
        let text = self.core.peek().text().trim_end().to_owned();
        if text.trim().is_empty() {
            self.overlays.toast("消息内容不能为空");
            return;
        }
        if !self.max_length_ok(&text) {
            return;
        }
        let mut editing = self.handle.editing;
        if text == state.original.trim_end() {
            editing.set(None);
            return;
        }
        let mut sending = self.sending;
        sending.set(true);
        self.spawn(async move {
            let session = self.session();
            let saved = self
                .overlays
                .attempt("编辑失败", session.edit_message(state.id, &text))
                .await;
            sending.set(false);
            if saved.is_some() {
                editing.set(None);
            }
        });
    }

    // ───────── 补全 ─────────

    fn load_subscribers(self) {
        match self.key() {
            ConversationKey::Topic(topic) => {
                let session = self.session();
                self.spawn(async move {
                    if let Ok(ids) = session.load_subscribers(topic.stream_id).await {
                        *self.subscribers.write_unchecked() = Some(ids.into_iter().collect());
                    }
                });
            }
            ConversationKey::Dm(dm) => {
                *self.subscribers.write_unchecked() =
                    Some(dm.other_user_ids().iter().copied().collect());
            }
        }
    }

    fn close_popup(self) {
        let mut popup = self.popup;
        if popup.peek().is_some() {
            popup.set(None);
            self.send_options();
        }
    }

    fn dismiss_popup(self) {
        let start = self.popup.peek().as_ref().map(|p| p.start);
        *self.dismissed.write_unchecked() = start;
        self.close_popup();
    }

    fn move_popup(self, delta: isize) {
        let mut popup = self.popup;
        if let Some(p) = popup.write().as_mut() {
            let len = p.items.len() as isize;
            p.selected = (p.selected as isize + delta).rem_euclid(len) as usize;
        }
    }

    fn refresh_popup(self) {
        let trigger = {
            let core = self.core.peek();
            if !*self.focused.peek() || !core.sel.is_collapsed() {
                None
            } else {
                let text = core.active_text();
                let model = core.active_model(&text);
                complete::detect(&text, core.sel.start, &model)
            }
        };
        let Some(trigger) = trigger else {
            *self.dismissed.write_unchecked() = None;
            self.close_popup();
            return;
        };
        if *self.dismissed.peek() == Some(trigger.start) {
            return;
        }
        let items = self.items_for(&trigger);
        if items.is_empty() {
            self.close_popup();
            return;
        }
        let (x, y) = *self.caret.peek();
        let mut popup = self.popup;
        let was_open = popup.peek().is_some();
        let selected = popup
            .peek()
            .as_ref()
            .filter(|p| p.start == trigger.start && p.query == trigger.query)
            .map_or(0, |p| p.selected.min(items.len() - 1));
        let next = Popup {
            kind: trigger.kind,
            start: trigger.start,
            query: trigger.query,
            items,
            selected,
            x,
            y,
        };
        if popup.peek().as_ref() != Some(&next) {
            popup.set(Some(next));
        }
        if !was_open {
            self.send_options();
        }
    }

    fn items_for(self, trigger: &Trigger) -> Vec<Item> {
        let session = self.session();
        let query = trigger.query.as_str();
        match trigger.kind {
            Kind::Language => complete::language_items(query),
            Kind::Mention => self.mention_items(&session, query),
            Kind::Channel => complete::rank_items(
                session
                    .subscribed_channels()
                    .into_iter()
                    .filter_map(|channel| {
                        let rank = match_rank(query, &[Some(&channel.name)])?;
                        Some((
                            rank,
                            Item {
                                insert: format!("#**{}** ", channel.name),
                                sort: channel.name.to_lowercase(),
                                title: channel.name,
                                ..Item::default()
                            },
                        ))
                    })
                    .collect(),
            ),
            Kind::Emoji => session
                .emoji_catalog()
                .search(query)
                .into_iter()
                .take(complete::MAX_ITEMS)
                .map(|entry| Item {
                    title: format!(":{}:", entry.name),
                    insert: format!(":{}: ", entry.name),
                    glyph: entry.text.clone(),
                    avatar_url: entry.image_url.clone(),
                    ..Item::default()
                })
                .collect(),
            Kind::Snippet => complete::rank_items(
                session
                    .snippets()
                    .into_iter()
                    .filter_map(|snippet| {
                        let rank = match_rank(query, &[Some(&snippet.title)])?;
                        let first_line = snippet
                            .content
                            .lines()
                            .next()
                            .unwrap_or_default()
                            .to_owned();
                        Some((
                            rank,
                            Item {
                                insert: format::with_trailing_line(&snippet.content),
                                subtitle: Some(first_line),
                                sort: snippet.title.to_lowercase(),
                                title: snippet.title,
                                ..Item::default()
                            },
                        ))
                    })
                    .collect(),
            ),
        }
    }

    fn mention_items(self, session: &Session, query: &str) -> Vec<Item> {
        let mut items = Vec::new();
        if session.is_wildcard_allowed(&self.key.peek()) {
            let mut wildcard = |title: &str, name: &str| {
                if let Some(rank) = match_rank(query, &[Some(title), Some(name)]) {
                    items.push((
                        rank,
                        Item {
                            title: title.to_owned(),
                            subtitle: Some(name.to_owned()),
                            insert: format!("@**{name}** "),
                            avatar_text: Some(title.to_owned()),
                            sort: name.to_owned(),
                            ..Item::default()
                        },
                    ));
                }
            };
            wildcard("全体成员", "all");
            if matches!(*self.key.peek(), ConversationKey::Topic(_)) {
                wildcard("话题参与者", "topic");
            }
        }
        let me = session.self_id();
        let allowed = self.subscribers.peek();
        items.extend(session.users().into_iter().filter_map(|user| {
            if user.id == me || !user.is_active {
                return None;
            }
            if allowed.as_ref().is_some_and(|ids| !ids.contains(&user.id)) {
                return None;
            }
            let rank = match_rank(query, &[Some(&user.full_name), Some(&user.email)])?;
            Some((
                rank,
                Item {
                    insert: format!("@**{}|{}** ", user.full_name, user.id),
                    subtitle: Some(user.email.clone()),
                    avatar_url: user.avatar_url.clone(),
                    avatar_text: Some(user.full_name.clone()),
                    sort: user.full_name.to_lowercase(),
                    title: user.full_name,
                    ..Item::default()
                },
            ))
        }));
        complete::rank_items(items)
    }

    /// 接受补全：`index` 为空时是当前选中的一项。
    pub fn accept_popup(self, index: Option<usize>) {
        let Some(popup) = self.popup.peek().clone() else {
            return;
        };
        let Some(item) = popup.items.get(index.unwrap_or(popup.selected)).cloned() else {
            return;
        };
        self.edit(|core| {
            core.edit_active(|text, model, caret, _| match popup.kind {
                Kind::Language => zulip_core::markdown::code_language::accept(
                    text,
                    caret,
                    popup.start,
                    &item.insert,
                    model,
                ),
                _ => {
                    let end = popup.start + item.insert.len();
                    format::MarkdownEdit::new(
                        popup.start,
                        caret - popup.start,
                        item.insert.clone(),
                        end,
                        end,
                    )
                }
            });
        });
    }

    // ───────── 其它操作 ─────────

    pub fn account(self) -> i64 {
        self.session().account_id()
    }

    pub fn set_source(self, source: bool) {
        AccountSettings::new(&services().db, self.account()).set_composer_source_mode(source);
        let mut rich = self.source;
        rich.set(source);
        self.core.write_unchecked().set_source(source);
        self.send_options();
        self.push(true, true, true);
        self.after_change(false);
    }

    pub fn set_enter_sends(self, value: bool) {
        let session = self.session();
        let overlays = self.overlays;
        self.spawn(async move {
            overlays
                .attempt(
                    "设置失败",
                    session.set_zulip_setting("enter_sends", json!(value)),
                )
                .await;
            self.send_options();
            self.refocus();
        });
    }

    pub fn create_snippet(self) {
        let initial = self.core.peek().selected_text();
        let overlays = self.overlays;
        self.spawn(async move {
            let content = if initial.is_empty() {
                overlays
                    .prompt_text(TextPrompt::new("新建常用语", "内容", "").max_len(10000))
                    .await
            } else {
                Some(initial)
            };
            let Some(content) = content else {
                self.refocus();
                return;
            };
            let Some(title) = overlays
                .prompt_text(TextPrompt::new("常用语名称", "名称", "").max_len(60))
                .await
            else {
                self.refocus();
                return;
            };
            let session = self.session();
            if overlays
                .attempt("新建常用语失败", session.create_snippet(&title, &content))
                .await
                .is_some()
            {
                overlays.toast("已保存");
            }
            self.refocus();
        });
    }

    pub fn delete_snippet(self, id: i64, title: String) {
        let overlays = self.overlays;
        let session = self.session();
        self.spawn(async move {
            overlays
                .delete(&format!("常用语「{title}」"), None, || {
                    session.delete(DeleteTarget::Snippet(id))
                })
                .await;
            self.refocus();
        });
    }

    pub fn insert_time(self) {
        self.spawn(async move {
            let Some(at) = dialogs::ask_time(self.overlays, "全局时间").await else {
                self.refocus();
                return;
            };
            if let Some(time) = Local.timestamp_opt(at, 0).single() {
                self.edit(|core| {
                    core.edit_active(|text, _, s, e| {
                        format::insert_time(text, s, e, time.fixed_offset())
                    });
                });
            }
        });
    }

    pub fn insert_table(self) {
        self.spawn(async move {
            match dialogs::ask_table(self.overlays).await {
                Some((columns, rows)) => self.edit(|core| {
                    core.edit_active(|text, _, s, e| {
                        format::insert_table(text, s, e, columns, rows)
                    });
                }),
                None => self.refocus(),
            }
        });
    }

    pub fn create_poll(self) {
        self.spawn(async move {
            if let Some(text) = dialogs::ask_poll(self.overlays).await {
                self.send_text(text);
            }
            self.refocus();
        });
    }
}

/// 整段文字是一个网址。
fn is_url(text: &str) -> bool {
    (text.starts_with("http://") || text.starts_with("https://"))
        && !text.contains(char::is_whitespace)
}

fn screenshot_name() -> String {
    format!("截图{}.png", Local::now().format("%Y%m%d%H%M%S"))
}

fn unique_paste_path(name: &str) -> std::io::Result<PathBuf> {
    static NEXT: AtomicU64 = AtomicU64::new(0);
    let dir = services().paths.cache_dir().join("paste").join(format!(
        "{}-{}",
        Local::now().timestamp_millis(),
        NEXT.fetch_add(1, Ordering::Relaxed)
    ));
    std::fs::create_dir_all(&dir)?;
    // 文件名来自剪贴板：只取最后一段
    let name = std::path::Path::new(name)
        .file_name()
        .map_or_else(|| "file".into(), |n| n.to_owned());
    Ok(dir.join(name))
}

fn write_paste_file(name: &str, bytes: &[u8]) -> std::io::Result<PathBuf> {
    let path = unique_paste_path(name)?;
    std::fs::write(&path, bytes)?;
    Ok(path)
}

fn decode_hex(hex: &str) -> Vec<u8> {
    let value = |b: u8| (b as char).to_digit(16).unwrap_or(0) as u8;
    hex.as_bytes()
        .chunks_exact(2)
        .map(|pair| value(pair[0]) << 4 | value(pair[1]))
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn hex_round_trips() {
        assert_eq!(decode_hex("00ff10"), vec![0, 255, 16]);
    }

    #[test]
    fn urls_detected() {
        assert!(is_url("https://a.b/c"));
        assert!(!is_url("https://a.b c"));
        assert!(!is_url("a.b"));
    }

    #[test]
    fn paste_names_keep_only_the_last_segment() {
        let name = std::path::Path::new("..\\..\\evil.png")
            .file_name()
            .unwrap();
        assert_eq!(name, "evil.png");
    }
}
