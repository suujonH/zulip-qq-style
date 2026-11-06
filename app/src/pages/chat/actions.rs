//! 消息操作：右键菜单、链接、图片、文件、复制。

use super::clipboard;
use super::content::{html_to_markdown, table_markdown};
use super::dialogs;
use super::store::Chat;
use crate::pages::composer::open_emoji_picker;
use crate::platform::{self, ClipboardImage};
use crate::services::plugins;
use crate::state::{AppCommand, send_command};
use crate::ui::{IconName, MenuEntry, Rect};
use crate::widgets::{self, FileSource, open_file, open_topic_card, open_user_card};
use crate::windows;
use dioxus::prelude::*;
use std::path::Path;
use std::sync::Arc;
use zulip_core::ConversationKey;
use zulip_core::emoji::{self, EmojiEntry};
use zulip_core::html::{
    extract_inline_images, normalize_upload_path, to_plain_text, upload_link_of,
};
use zulip_core::links;
use zulip_core::model::{ChatMessage, MessageState};
use zulip_core::text::time;
use zulip_plugin_host::{NativePlugin, PluginMenu};
use zulip_session::{DeleteTarget, Session};

const QUICK_REACTIONS: usize = 7;
const CARD_ANCHOR: f64 = 4.0;
const HOUR: i64 = 3600;

fn anchor_at(x: f64, y: f64) -> Rect {
    Rect {
        x,
        y,
        w: CARD_ANCHOR,
        h: CARD_ANCHOR,
    }
}

pub fn open_title_card(chat: Chat, e: &MouseEvent) {
    let at = e.client_coordinates();
    let anchor = anchor_at(at.x, at.y);
    match chat.current_key() {
        key @ ConversationKey::Topic(_) => open_topic_card(chat.overlays, anchor, key),
        ConversationKey::Dm(d) => {
            let self_id = chat.session().self_id();
            let others = d.other_user_ids();
            if let [user] = others {
                open_user_card(chat.overlays, anchor, *user);
            } else if d.is_self_dm() {
                open_user_card(chat.overlays, anchor, self_id);
            }
        }
    }
}

fn open_external(chat: Chat, url: &str) {
    chat.overlays.open_file(Path::new(url));
}

pub fn open_in_browser(chat: Chat) {
    open_external(chat, &super::web_link(chat));
}

/// 消息里的链接地址可能是相对路径。
pub fn absolute_url(realm: &str, href: &str) -> String {
    if href.contains("://") || href.starts_with("mailto:") {
        href.to_owned()
    } else {
        format!(
            "{}/{}",
            realm.trim_end_matches('/'),
            href.trim_start_matches('/')
        )
    }
}

/// 统一的链接处理：内部链接定位消息或打开会话，上传文件走文件流程，其它用系统浏览器。
pub fn open_link(chat: Chat, message_id: i64, href: String) {
    let session = chat.session();
    if let Some(path) = normalize_upload_path(&href) {
        let source = FileSource {
            link: upload_link_of(&path, None),
            message_id,
            key: chat.current_key(),
        };
        chat.spawn(async move { open_file(chat.overlays, &session, &source).await });
        return;
    }
    let url = absolute_url(session.realm_url(), &href);
    let Some(parsed) = links::parse(&url, session.realm_url()) else {
        chat.spawn(async move { widgets::open_link(chat.overlays, &session, &url).await });
        return;
    };
    let self_id = session.self_id();
    chat.spawn(async move {
        if parsed.near_message_id.is_some() {
            match session.locate_link(&url).await {
                Ok(Some(found)) => {
                    if found.key == chat.current_key() {
                        chat.jump_to(found.message_id, true, crate::ui::ScrollAlign::Center)
                            .await;
                    } else {
                        windows::open_chat(chat.account, found.key, Some(found.message_id));
                    }
                }
                Ok(None) => {}
                Err(error) => chat.overlays.report_error("定位消息失败", error),
            }
            return;
        }
        match (parsed.stream_id, parsed.topic, parsed.dm_user_ids) {
            (Some(stream), Some(topic), _) => windows::open_chat(
                chat.account,
                ConversationKey::topic(stream, topic),
                None,
            ),
            (Some(stream_id), None, _) => send_command(AppCommand::ShowChannel {
                account: chat.account,
                stream_id,
            }),
            (_, _, Some(ids)) => {
                windows::open_chat(chat.account, ConversationKey::dm(ids, self_id), None);
            }
            _ => {}
        }
    });
}

/// 整个气泡中的图片组成相册。
pub fn open_image(chat: Chat, msg: i64, index: usize) {
    let Some(message) = chat.message(msg) else {
        return;
    };
    let album: Vec<String> = extract_inline_images(&message.content_html)
        .iter()
        .map(|image| image.original())
        .collect();
    if !album.is_empty() {
        windows::open_image_viewer(chat.account, album, index.min(usize::MAX));
    }
}

fn mention_markup(user: Option<&str>, text: &str) -> String {
    let name = text.trim_start_matches('@');
    match user {
        Some(id) if id != "*" => format!("@**{name}|{id}** "),
        _ => format!("@**{name}** "),
    }
}

pub fn insert_mention(chat: Chat, user: Option<&str>, text: &str) {
    let mut insert = chat.composer.insert;
    insert.set(Some(mention_markup(user, text)));
}

pub fn table_menu(chat: Chat, msg: i64, index: usize, x: f64, y: f64) {
    let Some(message) = chat.message(msg) else {
        return;
    };
    let entry = |label: &str, padded: bool| {
        let html = message.content_html.clone();
        MenuEntry::item(label, move || {
            if let Some(source) = table_markdown(&html, index, padded) {
                copy_with_markdown(chat, &source, &source);
            }
        })
    };
    chat.overlays.show_menu(
        x,
        y,
        vec![
            entry("复制表格", false).icon(IconName::Copy),
            entry("格式化表格源码", true).icon(IconName::SourceCode),
        ],
    );
}

fn copy_with_markdown(chat: Chat, text: &str, markdown: &str) {
    clipboard::remember(text, markdown);
    chat.overlays.copy_text(text);
}

/// 一条消息的文字与 Markdown 原文。
async fn copy_message(chat: Chat, message: Arc<ChatMessage>) {
    let text = to_plain_text(&message.content_html);
    let markdown = match &message.state {
        MessageState::Sending(echo) | MessageState::Failed(echo) => echo.raw_content.clone(),
        _ => chat
            .session()
            .raw_content(message.id)
            .await
            .unwrap_or_else(|_| text.clone()),
    };
    copy_with_markdown(chat, &text, &markdown);
}

async fn image_file(chat: Chat, msg: i64, index: usize) -> Option<std::path::PathBuf> {
    let message = chat.message(msg)?;
    let image = extract_inline_images(&message.content_html)
        .into_iter()
        .nth(index)?;
    chat.overlays
        .attempt("下载图片失败", chat.session().image_file(&image.original()))
        .await
}

fn copy_image(chat: Chat, msg: i64, index: usize) {
    chat.spawn(async move {
        let Some(path) = image_file(chat, msg, index).await else {
            return;
        };
        let decoded = image::open(&path).map(|img| {
            let rgba = img.to_rgba8();
            ClipboardImage {
                width: rgba.width() as usize,
                height: rgba.height() as usize,
                rgba: rgba.into_raw(),
            }
        });
        match decoded {
            Ok(image) => match platform::copy_image(&image) {
                Ok(()) => chat.overlays.toast("已复制"),
                Err(error) => chat.overlays.report_error("复制失败", error),
            },
            Err(error) => chat.overlays.report_error("复制失败", error),
        }
    });
}

fn save_image(chat: Chat, msg: i64, index: usize) {
    chat.spawn(async move {
        let Some(path) = image_file(chat, msg, index).await else {
            return;
        };
        let name = path
            .file_name()
            .map_or_else(|| "图片.png".to_owned(), |n| n.to_string_lossy().into_owned());
        chat.overlays
            .save_as(&name, |target| async move {
                std::fs::copy(&path, target).map(|_| ())
            })
            .await;
    });
}

fn image_folder(chat: Chat, msg: i64, index: usize) {
    chat.spawn(async move {
        if let Some(path) = image_file(chat, msg, index).await
            && let Some(parent) = path.parent()
        {
            chat.overlays.open_file(parent);
        }
    });
}

pub fn show_menu(
    chat: Chat,
    msg: Option<i64>,
    sender: bool,
    x: f64,
    y: f64,
    image: Option<usize>,
    selection: Option<(String, String)>,
) {
    if *chat.multi.peek() {
        return;
    }
    let items = match msg.and_then(|id| chat.message(id)) {
        Some(message) if sender => sender_menu(chat, &message, x, y),
        Some(message) => message_menu(chat, message, image, selection),
        None => blank_menu(chat),
    };
    chat.overlays.show_menu(x, y, items);
}

fn blank_menu(chat: Chat) -> Vec<MenuEntry> {
    let source = *chat.source_mode.peek();
    vec![
        MenuEntry::item("源代码模式", move || {
            let mut mode = chat.source_mode;
            mode.set(!source);
        })
        .checked(source),
    ]
}

fn sender_menu(chat: Chat, message: &ChatMessage, x: f64, y: f64) -> Vec<MenuEntry> {
    let session = chat.session();
    let user = message.sender_id;
    let name = session.display_name(user);
    let muted = session.is_user_muted(user);
    let mut items = vec![
        MenuEntry::item("发送消息", {
            let self_id = session.self_id();
            move || windows::open_chat(chat.account, ConversationKey::dm([user], self_id), None)
        }),
        MenuEntry::item("@TA", move || {
            let mut insert = chat.composer.insert;
            insert.set(Some(format!("@**{name}|{user}** ")));
        }),
        MenuEntry::item("查看资料", move || {
            open_user_card(chat.overlays, anchor_at(x, y), user);
        }),
    ];
    if user != session.self_id() {
        items.push(MenuEntry::item(
            if muted { "取消屏蔽" } else { "屏蔽此人" },
            move || toggle_muted(chat, user, !muted),
        ));
    }
    items
}

/// 屏蔽前确认，取消屏蔽直接执行。
pub fn toggle_muted(chat: Chat, user: i64, mute: bool) {
    let session = chat.session();
    chat.spawn(async move {
        if mute {
            chat.overlays
                .confirm_then(
                    crate::ui::Confirm::new("屏蔽后将不再显示对方的消息").danger("屏蔽"),
                    "屏蔽失败",
                    || session.set_user_muted(user, true),
                )
                .await;
        } else {
            chat.overlays
                .attempt("取消屏蔽失败", session.set_user_muted(user, false))
                .await;
        }
    });
}

fn reaction_row(chat: Chat, id: i64) -> MenuEntry {
    let session = chat.session();
    let emojis: Vec<EmojiEntry> = session
        .quick_reactions()
        .into_iter()
        .take(QUICK_REACTIONS)
        .collect();
    MenuEntry::custom(move |close: Callback<()>| {
        let buttons = emojis.iter().cloned().map(|entry| {
            let text = emoji_text(&entry);
            let title = entry.name.clone();
            rsx! {
                button {
                    key: "{title}",
                    class: "reaction-pick",
                    r#type: "button",
                    title: "{title}",
                    onclick: move |_| {
                        close.call(());
                        toggle_reaction(chat, id, entry.clone());
                    },
                    "{text}"
                }
            }
        });
        rsx! {
            div { class: "reaction-row",
                {buttons}
                button {
                    class: "reaction-pick more",
                    r#type: "button",
                    title: "更多表情",
                    onclick: move |e: MouseEvent| {
                        close.call(());
                        let at = e.client_coordinates();
                        pick_reaction(chat, id, at.x, at.y);
                    },
                    "⋯"
                }
            }
        }
    })
}

fn emoji_text(entry: &EmojiEntry) -> String {
    entry
        .text
        .clone()
        .or_else(|| emoji::unicode_from_code(&entry.code))
        .unwrap_or_else(|| format!(":{}:", entry.name))
}

fn toggle_reaction(chat: Chat, id: i64, entry: EmojiEntry) {
    chat.spawn(async move {
        let session = chat.session();
        session.add_recent_emoji(&entry.name);
        chat.overlays
            .attempt("操作失败", session.toggle_reaction(id, &entry))
            .await;
    });
}

pub fn toggle_reaction_by_name(chat: Chat, id: i64, name: &str) {
    let entry = chat.session().emoji_catalog().get(name).cloned();
    match entry {
        Some(entry) => toggle_reaction(chat, id, entry),
        None => chat.overlays.report_error("操作失败", "找不到这个表情"),
    }
}

fn pick_reaction(chat: Chat, id: i64, x: f64, y: f64) {
    open_emoji_picker(
        chat.overlays,
        anchor_at(x, y),
        chat.session(),
        Callback::new(move |name: String| toggle_reaction_by_name(chat, id, &name)),
    );
}

fn plugin_entry(
    chat: Chat,
    plugin: &Arc<NativePlugin>,
    menu: &PluginMenu,
    message: &Arc<ChatMessage>,
    session: &Arc<Session>,
) -> MenuEntry {
    if menu.is_group() {
        return MenuEntry::submenu(
            menu.name.clone(),
            menu.children
                .iter()
                .map(|child| plugin_entry(chat, plugin, child, message, session))
                .collect(),
        );
    }
    let (plugin, message, session) = (plugin.clone(), message.clone(), session.clone());
    let id = menu.id;
    MenuEntry::item(menu.name.clone(), move || {
        plugins().invoke_message_menu(&plugin, id, &message, &session);
    })
}

fn reminder_at(chat: Chat, id: i64, deliver_at: i64) {
    chat.spawn(async move {
        let result = chat.session().create_reminder(id, deliver_at).await;
        match result {
            Ok(_) => {
                let text = time::from_unix(deliver_at)
                    .map_or_else(String::new, |t| t.format("%Y/%m/%d %H:%M").to_string());
                chat.overlays.toast(format!("将在 {text} 提醒你"));
            }
            Err(error) => chat.overlays.report_error("设置提醒失败", error),
        }
    });
}

/// 本地时间的明天 9:00（Unix 秒）。
fn tomorrow_nine() -> Option<i64> {
    let tomorrow = chrono::Local::now().date_naive().succ_opt()?;
    let at = tomorrow.and_hms_opt(9, 0, 0)?;
    at.and_local_timezone(chrono::Local)
        .earliest()
        .map(|t| t.timestamp())
}

fn reminder_menu(chat: Chat, id: i64) -> MenuEntry {
    MenuEntry::submenu(
        "提醒我",
        vec![
            MenuEntry::item("1 小时后", move || {
                reminder_at(chat, id, chrono::Utc::now().timestamp() + HOUR);
            }),
            MenuEntry::item("明天 9:00", move || {
                if let Some(at) = tomorrow_nine() {
                    reminder_at(chat, id, at);
                }
            }),
            MenuEntry::item("自定义…", move || {
                chat.spawn(async move {
                    if let Some(at) = dialogs::pick_reminder_time(chat.overlays).await {
                        reminder_at(chat, id, at);
                    }
                });
            }),
        ],
    )
    .icon(IconName::Time)
}

fn message_menu(
    chat: Chat,
    message: Arc<ChatMessage>,
    image: Option<usize>,
    selection: Option<(String, String)>,
) -> Vec<MenuEntry> {
    let session = chat.session();
    let id = message.id;
    let mut items = Vec::new();
    if id <= 0 {
        items.push(copy_entry(chat, message, image, selection));
        return items;
    }
    let actions = session.message_actions(id);
    items.push(reaction_row(chat, id));
    items.push(MenuEntry::Separator);
    items.push(copy_entry(chat, message.clone(), image, selection));
    if let Some(index) = image {
        items.push(MenuEntry::item("另存为", move || save_image(chat, id, index)).icon(IconName::Download));
        items.push(MenuEntry::item("打开文件夹", move || image_folder(chat, id, index)).icon(IconName::Folder));
    }
    let starred = message.flags.starred;
    items.push(
        MenuEntry::item(if starred { "取消收藏" } else { "收藏" }, move || {
            set_starred(chat, vec![id], !starred);
        })
        .icon(if starred {
            IconName::StarFilled
        } else {
            IconName::Star
        }),
    );
    items.push(
        MenuEntry::item("引用", move || {
            chat.spawn(async move {
                if let Some(markdown) = chat
                    .overlays
                    .attempt("引用失败", chat.session().quote_message(id))
                    .await
                {
                    let mut insert = chat.composer.insert;
                    insert.set(Some(markdown));
                }
            });
        })
        .icon(IconName::Quote),
    );
    items.push(
        MenuEntry::item("转发", move || super::dialogs::forward(chat, vec![id], false))
            .icon(IconName::Share),
    );
    items.push(MenuEntry::item("多选", move || chat.enter_multi(id)).icon(IconName::CheckCircle));
    if actions.can_edit {
        items.push(
            MenuEntry::item("编辑", move || {
                let mut editing = chat.composer.editing;
                editing.set(Some(id));
            })
            .icon(IconName::Edit),
        );
    }
    items.push(MenuEntry::Separator);

    let mut more = Vec::new();
    for plugin in plugins().plugins() {
        for menu in plugin.active_message_menus() {
            more.push(plugin_entry(chat, &plugin, menu, &message, &session));
        }
    }
    if !more.is_empty() {
        more.push(MenuEntry::Separator);
    }
    {
        let link_session = session.clone();
        more.push(MenuEntry::item("复制链接", move || {
            let link = link_session.message_link(&chat.current_key(), id);
            chat.overlays.copy_text(&link);
        }));
    }
    if actions.can_show_edit_history {
        more.push(MenuEntry::item("编辑历史", move || {
            dialogs::show_history(chat, id);
        }));
    }
    let key = chat.current_key();
    let shows_receipts = match &key {
        ConversationKey::Topic(_) => true,
        ConversationKey::Dm(d) => d.is_group_dm(),
    };
    if actions.can_show_read_receipts && shows_receipts {
        more.push(MenuEntry::item("已读成员", move || {
            dialogs::show_receipts(chat, id);
        }));
    }
    more.push(MenuEntry::item("从这里标为未读", move || {
        chat.spawn(async move {
            let mut suspended = chat.auto_read_suspended;
            *suspended.write() = true;
            let key = chat.current_key();
            chat.overlays
                .attempt("操作失败", chat.session().mark_unread_from(&key, id))
                .await;
        });
    }));
    more.push(reminder_menu(chat, id));
    items.push(MenuEntry::submenu("更多", more).icon(IconName::More));
    if actions.can_delete {
        items.push(
            MenuEntry::item("删除", move || {
                chat.spawn(async move {
                    let session = chat.session();
                    chat.overlays
                        .delete("这条消息", None, || session.delete(DeleteTarget::Message(id)))
                        .await;
                });
            })
            .icon(IconName::Delete)
            .danger(),
        );
    }
    items
}

fn copy_entry(
    chat: Chat,
    message: Arc<ChatMessage>,
    image: Option<usize>,
    selection: Option<(String, String)>,
) -> MenuEntry {
    MenuEntry::item("复制", move || {
        if let Some(index) = image {
            copy_image(chat, message.id, index);
        } else if let Some((text, html)) = &selection {
            copy_with_markdown(chat, text, &html_to_markdown(html));
        } else {
            chat.spawn(copy_message(chat, message.clone()));
        }
    })
    .icon(IconName::Copy)
}

fn set_starred(chat: Chat, ids: Vec<i64>, starred: bool) {
    chat.spawn(async move {
        let done = chat
            .overlays
            .attempt("操作失败", chat.session().set_starred(&ids, starred))
            .await;
        if done.is_some() {
            chat.overlays
                .toast(if starred { "收藏成功" } else { "已取消收藏" });
        }
    });
}

pub fn failed_menu(chat: Chat, local_id: String, x: f64, y: f64) {
    let retry = local_id.clone();
    chat.overlays.show_menu(
        x,
        y,
        vec![
            MenuEntry::item("重新发送", move || {
                let retry = retry.clone();
                chat.spawn(async move {
                    chat.overlays
                        .attempt("发送失败", chat.session().retry_send(&retry))
                        .await;
                });
            })
            .icon(IconName::Refresh),
            MenuEntry::item("放弃发送", move || chat.session().discard_failed(&local_id))
                .icon(IconName::Delete)
                .danger(),
        ],
    );
}

pub fn selected_ids(chat: Chat) -> Vec<i64> {
    chat.selected.peek().iter().copied().collect()
}

pub fn star_selected(chat: Chat) {
    set_starred(chat, selected_ids(chat), true);
    chat.exit_multi();
}

/// 多选复制：每条“名字: 时间”加内容，之间空一行。
pub fn copy_selected(chat: Chat) {
    let session = chat.session();
    let use_24h = session.settings().twenty_four_hour_time;
    let now = chrono::Local::now().naive_local();
    let text = selected_ids(chat)
        .iter()
        .filter_map(|id| chat.message(*id))
        .map(|m| {
            let when = time::from_unix(m.timestamp)
                .map_or_else(String::new, |t| time::precise_time(t, now, use_24h));
            format!(
                "{}: {when}\n{}",
                session.display_name(m.sender_id),
                to_plain_text(&m.content_html)
            )
        })
        .collect::<Vec<_>>()
        .join("\n\n");
    chat.overlays.copy_text(&text);
    chat.exit_multi();
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn relative_links_join_realm() {
        assert_eq!(
            absolute_url("https://z.test/", "/user_uploads/a"),
            "https://z.test/user_uploads/a"
        );
        assert_eq!(absolute_url("https://z.test", "http://x/y"), "http://x/y");
        assert_eq!(absolute_url("https://z.test", "mailto:a@b"), "mailto:a@b");
    }

    #[test]
    fn mention_markup_keeps_id() {
        assert_eq!(mention_markup(Some("9"), "@张三"), "@**张三|9** ");
        assert_eq!(mention_markup(Some("*"), "@全体成员"), "@**全体成员** ");
        assert_eq!(mention_markup(None, "@组"), "@**组** ");
    }
}
