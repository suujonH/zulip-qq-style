//! `chat.js` 发来的消息内容事件。

use super::actions;
use super::clipboard;
use super::content::html_to_markdown;
use super::store::Chat;
use crate::services::plugins;
use dioxus::dioxus_core::current_scope_id;
use dioxus::prelude::*;
use serde::Deserialize;
use zulip_plugin_host::{Modifiers, MouseButton, mouse_gesture};

const SCRIPT: &str = include_str!("chat.js");

#[derive(Deserialize)]
#[serde(tag = "t", rename_all = "lowercase")]
enum ChatEvent {
    Link {
        msg: Option<i64>,
        href: String,
    },
    Image {
        msg: i64,
        index: usize,
    },
    Mention {
        user: Option<String>,
        text: String,
    },
    Table {
        msg: i64,
        index: usize,
        x: f64,
        y: f64,
    },
    Menu {
        msg: Option<i64>,
        sender: bool,
        x: f64,
        y: f64,
        image: Option<usize>,
        text: Option<String>,
        html: Option<String>,
    },
    Gesture {
        msg: i64,
        button: u8,
        clicks: u32,
        ctrl: bool,
        alt: bool,
        shift: bool,
        meta: bool,
    },
    Copy {
        text: String,
        html: String,
    },
    Esc,
}

fn on_gesture(mut chat: Chat, msg: i64, button: u8, clicks: u32, modifiers: Modifiers) {
    let button = match button {
        0 => MouseButton::Left,
        1 => MouseButton::Middle,
        2 => MouseButton::Right,
        _ => return,
    };
    let Some(gesture) = mouse_gesture(button, clicks, modifiers) else {
        return;
    };
    let host = plugins();
    if !host.has_mouse_shortcut(&gesture) {
        return;
    }
    let Some(message) = chat.message(msg) else {
        return;
    };
    let session = chat.session();
    if host.invoke_mouse_shortcut(&gesture, &message, &session) && button == MouseButton::Right {
        *chat.suppress_menu.write() = true;
    }
}

fn handle(mut chat: Chat, event: ChatEvent) {
    match event {
        ChatEvent::Link { msg, href } => actions::open_link(chat, msg.unwrap_or(0), href),
        ChatEvent::Image { msg, index } => actions::open_image(chat, msg, index),
        ChatEvent::Mention { user, text } => actions::insert_mention(chat, user.as_deref(), &text),
        ChatEvent::Table { msg, index, x, y } => actions::table_menu(chat, msg, index, x, y),
        ChatEvent::Menu {
            msg,
            sender,
            x,
            y,
            image,
            text,
            html,
        } => {
            if std::mem::take(&mut *chat.suppress_menu.write()) {
                return;
            }
            let selection = text.zip(html);
            actions::show_menu(chat, msg, sender, x, y, image, selection);
        }
        ChatEvent::Gesture {
            msg,
            button,
            clicks,
            ctrl,
            alt,
            shift,
            meta,
        } => on_gesture(
            chat,
            msg,
            button,
            clicks,
            Modifiers {
                ctrl,
                alt,
                shift,
                meta,
            },
        ),
        ChatEvent::Copy { text, html } => {
            clipboard::remember(&text, &html_to_markdown(&html));
        }
        ChatEvent::Esc => {
            if *chat.multi.peek() {
                chat.exit_multi();
            }
        }
    }
}

/// 挂接事件委托，返回聊天页根元素的 id。
pub fn use_chat_events(chat: Chat) -> String {
    let id = use_hook(|| format!("chat-pane-{}", current_scope_id().0));
    let attached = id.clone();
    use_future(move || {
        let attached = attached.clone();
        async move {
            let mut eval = document::eval(&format!(
                "{SCRIPT}\nawait window.zulipChat.attach({attached:?}, (m) => dioxus.send(m));"
            ));
            while let Ok(event) = eval.recv::<ChatEvent>().await {
                handle(chat, event);
            }
        }
    });
    let detached = id.clone();
    use_drop(move || {
        document::eval(&format!("window.zulipChat.detach({detached:?})"));
    });
    id
}

/// 让已挂载的消息显示下载好的图片；内容不重新设置，文字选择不会丢。
pub fn set_image(msg: i64, url: &str, local: Option<&str>) {
    let url = serde_json::to_string(url).unwrap_or_default();
    match local.map(serde_json::to_string) {
        Some(Ok(local)) => {
            document::eval(&format!("window.zulipChat.setImage({msg},{url},{local})"));
        }
        _ => {
            document::eval(&format!("window.zulipChat.failImage({msg},{url})"));
        }
    }
}
