//! 转发、编辑历史、已读成员、提醒时间。

use super::content::{RenderOptions, Segment, render};
use super::store::Chat;
use crate::ui::{Answer, DialogButtons, DialogFrame, ListRow, Overlays, RowDensity};
use crate::widgets::{self, RemoteAvatar, SessionRef, local_timestamp, open_user_card, parse_datetime};
use dioxus::prelude::*;
use std::sync::Arc;
use zulip_core::model::MessageRevision;
use zulip_core::text::time;
use zulip_session::Session;

/// 选择目标后转发。`merged` 为 true 时合并成一条。
pub fn forward(chat: Chat, ids: Vec<i64>, merged: bool) {
    chat.spawn(async move {
        let session = chat.session();
        if merged {
            let sender = session.clone();
            widgets::forward(chat.overlays, &session, move |keys| async move {
                sender.forward_merged(&ids, &keys).await
            })
            .await;
        } else {
            widgets::forward_messages(chat.overlays, &session, ids).await;
        }
        chat.exit_multi();
    });
}

fn static_html(html: &str) -> String {
    render(
        html,
        &RenderOptions {
            me: None,
            local_image: &|_| None,
        },
    )
    .segments
    .into_iter()
    .filter_map(|s| match s {
        Segment::Html(h) => Some(h),
        Segment::File(link) => Some(format!("<p>[文件] {}</p>", link.file_name)),
    })
    .collect()
}

/// 一次修改的说明：谁、做了什么。
fn revision_title(session: &Session, revision: &MessageRevision, first: bool) -> String {
    let editor = revision
        .user_id
        .map_or_else(|| "系统".to_owned(), |id| session.display_name(id));
    let moved = revision.prev_topic.is_some() || revision.prev_stream.is_some();
    let action = if first {
        "发送"
    } else if moved && revision.prev_content.is_none() {
        "移动"
    } else {
        "编辑"
    };
    format!("{editor} {action}")
}

fn revision_moves(session: &Session, revision: &MessageRevision) -> Vec<String> {
    let mut lines = Vec::new();
    if let (Some(from), Some(to)) = (revision.prev_stream, revision.stream) {
        lines.push(format!(
            "频道：#{} → #{}",
            session.channel_display_name(from),
            session.channel_display_name(to)
        ));
    }
    if let Some(from) = &revision.prev_topic {
        lines.push(format!("话题：{from} → {}", revision.topic));
    }
    lines
}

pub fn show_history(chat: Chat, id: i64) {
    let session = chat.session();
    let overlays = chat.overlays;
    overlays.open_dialog(true, move |handle| {
        rsx! {
            HistoryDialog { session: SessionRef(session.clone()), id, on_close: move |()| handle.close() }
        }
    });
}

#[component]
fn HistoryDialog(session: SessionRef, id: i64, on_close: EventHandler<()>) -> Element {
    let session = session.0;
    let loader = session.clone();
    let revisions = use_resource(move || {
        let session = loader.clone();
        async move { session.message_history(id).await }
    });
    let use_24h = session.settings().twenty_four_hour_time;
    let now = chrono::Local::now().naive_local();
    rsx! {
        DialogFrame { title: "编辑历史", width: 480.0, on_close,
            div { class: "history-list scroll",
                match &*revisions.read() {
                    None => rsx! { div { class: "dialog-empty", "加载中…" } },
                    Some(Err(error)) => rsx! { div { class: "dialog-empty", "加载失败：{error}" } },
                    Some(Ok(list)) if list.is_empty() => rsx! { div { class: "dialog-empty", "没有可以查看的编辑历史" } },
                    Some(Ok(list)) => rsx! {
                        for (i, revision) in list.iter().enumerate().rev() {
                            {
                                let title = revision_title(&session, revision, i == 0);
                                let when = time::from_unix(revision.timestamp)
                                    .map_or_else(String::new, |t| time::precise_time(t, now, use_24h));
                                let moves = revision_moves(&session, revision);
                                let html = static_html(
                                    revision.content_html_diff.as_deref().unwrap_or(&revision.rendered_content),
                                );
                                rsx! {
                                    div { key: "{i}", class: "history-entry",
                                        div { class: "history-head",
                                            span { "{title}" }
                                            span { class: "text-caption", "{when}" }
                                        }
                                        for line in moves {
                                            div { class: "text-sub", "{line}" }
                                        }
                                        div { class: "msg-html selectable history-body", dangerous_inner_html: "{html}" }
                                    }
                                }
                            }
                        }
                    },
                }
            }
        }
    }
}

pub fn show_receipts(chat: Chat, id: i64) {
    let session = chat.session();
    let overlays = chat.overlays;
    let sender = chat.message(id).map_or(0, |m| m.sender_id);
    overlays.open_dialog(true, move |handle| {
        rsx! {
            ReceiptsDialog { session: SessionRef(session.clone()), id, sender, on_close: move |()| handle.close() }
        }
    });
}

#[component]
fn ReceiptsDialog(
    session: SessionRef,
    id: i64,
    sender: i64,
    on_close: EventHandler<()>,
) -> Element {
    let session: Arc<Session> = session.0;
    let loader = session.clone();
    let overlays = crate::ui::use_overlays();
    let readers = use_resource(move || {
        let session = loader.clone();
        async move {
            let self_id = session.self_id();
            session.read_receipts(id).await.map(|ids| {
                ids.into_iter()
                    .filter(|u| *u != self_id && *u != sender)
                    .collect::<Vec<i64>>()
            })
        }
    });
    let current = readers.read();
    let title = match &*current {
        Some(Ok(list)) => format!("已读成员 ({})", list.len()),
        _ => "已读成员".to_owned(),
    };
    rsx! {
        DialogFrame { title, width: 360.0, on_close,
            div { class: "history-list scroll",
                match &*current {
                    None => rsx! { div { class: "dialog-empty", "加载中…" } },
                    Some(Err(error)) => rsx! { div { class: "dialog-empty", "加载失败：{error}" } },
                    Some(Ok(list)) if list.is_empty() => rsx! { div { class: "dialog-empty", "暂时没有成员已读" } },
                    Some(Ok(list)) => rsx! {
                        for user in list.iter().copied() {
                            {
                                let name = session.display_name(user);
                                let avatar = session.user(user).and_then(|u| u.avatar_url);
                                rsx! {
                                    ListRow {
                                        key: "{user}",
                                        density: RowDensity::Compact,
                                        leading: rsx! { RemoteAvatar { text: name.clone(), url: avatar, size: 28.0 } },
                                        title: name.clone(),
                                        onclick: move |e: MouseEvent| {
                                            let at = e.client_coordinates();
                                            open_user_card(
                                                overlays,
                                                crate::ui::Rect { x: at.x, y: at.y, w: 4.0, h: 4.0 },
                                                user,
                                            );
                                        },
                                    }
                                }
                            }
                        }
                    },
                }
            }
        }
    }
}

/// 选择自定义提醒时间。
pub async fn pick_reminder_time(overlays: Overlays) -> Option<i64> {
    overlays
        .ask_dialog(|answer: Answer<i64>| rsx! { ReminderDialog { answer } })
        .await
}

#[component]
fn ReminderDialog(answer: Answer<i64>) -> Element {
    let mut value = use_signal(String::new);
    let parsed = parse_datetime(&value()).and_then(local_timestamp);
    let valid = parsed.is_some_and(|at| at > chrono::Utc::now().timestamp());
    let close = answer.clone();
    rsx! {
        DialogFrame {
            title: "提醒我",
            width: 320.0,
            footer: rsx! {
                DialogButtons {
                    disabled: !valid,
                    on_confirm: move |()| {
                        if let Some(at) = parsed {
                            answer.send(at);
                        }
                    },
                    on_cancel: move |()| close.cancel(),
                }
            },
            input {
                class: "reminder-input",
                r#type: "datetime-local",
                value: "{value}",
                oninput: move |e| value.set(e.value()),
            }
        }
    }
}
