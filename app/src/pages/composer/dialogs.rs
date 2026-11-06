//! 输入框的对话框：选择时间、插入表格、投票。

use super::core::poll_message;
use crate::ui::*;
use crate::widgets::{local_timestamp, parse_datetime};
use chrono::{Duration, Local, Timelike};
use dioxus::prelude::*;
use zulip_core::markdown::table::{MAX_COLUMNS, MAX_ROWS, MIN_COLUMNS, MIN_ROWS};

const POLL_OPTIONS_INITIAL: usize = 2;

/// 选择将来的时间；取消时为 `None`。
pub async fn ask_time(overlays: Overlays, title: &'static str) -> Option<i64> {
    overlays
        .ask_dialog(move |answer| rsx! { TimeDialog { title, answer } })
        .await
}

pub async fn ask_table(overlays: Overlays) -> Option<(usize, usize)> {
    overlays
        .ask_dialog(move |answer| rsx! { TableDialog { answer } })
        .await
}

/// 投票消息的文字。
pub async fn ask_poll(overlays: Overlays) -> Option<String> {
    overlays
        .ask_dialog(move |answer| rsx! { PollDialog { answer } })
        .await
}

// 默认下一个整点，`datetime-local` 的格式
fn default_time() -> String {
    let now = Local::now() + Duration::hours(1);
    now.with_minute(0)
        .unwrap_or(now)
        .format("%Y-%m-%dT%H:%M")
        .to_string()
}

#[component]
fn TimeDialog(title: &'static str, answer: Answer<i64>) -> Element {
    let mut text = use_signal(default_time);
    let at = parse_datetime(&text.read()).and_then(local_timestamp);
    let future = at.is_some_and(|at| at > Local::now().timestamp());
    let close = answer.clone();
    let cancel = answer.clone();
    rsx! {
        DialogFrame {
            title,
            width: 320.0,
            on_close: move |_| close.cancel(),
            footer: rsx! {
                DialogButtons {
                    disabled: !future,
                    on_confirm: move |_| {
                        if let Some(at) = at {
                            answer.send(at);
                        }
                    },
                    on_cancel: move |_| cancel.cancel(),
                }
            },
            div { class: "dialog-form",
                div { class: "input",
                    input {
                        r#type: "datetime-local",
                        value: "{text}",
                        oninput: move |e| text.set(e.value()),
                    }
                }
                if !future {
                    div { class: "composer-dialog-error", "请选择将来的时间" }
                }
            }
        }
    }
}

fn parse_in(text: &str, min: usize, max: usize) -> Option<usize> {
    text.trim().parse().ok().filter(|n| (min..=max).contains(n))
}

#[component]
fn TableDialog(answer: Answer<(usize, usize)>) -> Element {
    let mut columns = use_signal(|| "3".to_owned());
    let mut rows = use_signal(|| "3".to_owned());
    let size = parse_in(&columns.read(), MIN_COLUMNS, MAX_COLUMNS).zip(parse_in(
        &rows.read(),
        MIN_ROWS,
        MAX_ROWS,
    ));
    let close = answer.clone();
    let cancel = answer.clone();
    rsx! {
        DialogFrame {
            title: "插入表格",
            width: 300.0,
            on_close: move |_| close.cancel(),
            footer: rsx! {
                DialogButtons {
                    disabled: size.is_none(),
                    on_confirm: move |_| {
                        if let Some(size) = size {
                            answer.send(size);
                        }
                    },
                    on_cancel: move |_| cancel.cancel(),
                }
            },
            div { class: "dialog-form",
                label { class: "dialog-label", "列数（{MIN_COLUMNS} 到 {MAX_COLUMNS}）" }
                TextInput {
                    value: columns(),
                    autofocus: true,
                    invalid: parse_in(&columns.read(), MIN_COLUMNS, MAX_COLUMNS).is_none(),
                    oninput: move |v| columns.set(v),
                }
                label { class: "dialog-label", "行数（{MIN_ROWS} 到 {MAX_ROWS}）" }
                TextInput {
                    value: rows(),
                    invalid: parse_in(&rows.read(), MIN_ROWS, MAX_ROWS).is_none(),
                    oninput: move |v| rows.set(v),
                }
            }
        }
    }
}

#[component]
fn PollDialog(answer: Answer<String>) -> Element {
    let mut question = use_signal(String::new);
    let mut options = use_signal(|| vec![String::new(); POLL_OPTIONS_INITIAL]);
    let filled = options
        .read()
        .iter()
        .filter(|o| !o.trim().is_empty())
        .count();
    let valid = !question.read().trim().is_empty() && filled >= POLL_OPTIONS_INITIAL;
    let close = answer.clone();
    let cancel = answer.clone();
    let count = options.read().len();
    rsx! {
        DialogFrame {
            title: "投票",
            width: 400.0,
            on_close: move |_| close.cancel(),
            footer: rsx! {
                DialogButtons {
                    confirm_text: "发送",
                    disabled: !valid,
                    on_confirm: move |_| answer.send(poll_message(&question.read(), &options.read())),
                    on_cancel: move |_| cancel.cancel(),
                }
            },
            div { class: "dialog-form",
                label { class: "dialog-label", "问题" }
                TextInput {
                    value: question(),
                    placeholder: "填写问题",
                    autofocus: true,
                    oninput: move |v| question.set(v),
                }
                label { class: "dialog-label", "选项" }
                for i in 0..count {
                    div { key: "{i}", class: "composer-dialog-row",
                        TextInput {
                            value: options.read()[i].clone(),
                            placeholder: "选项 {i + 1}",
                            oninput: move |v| options.write()[i] = v,
                        }
                        if count > POLL_OPTIONS_INITIAL {
                            IconButton {
                                icon: IconName::Close,
                                onclick: move |_| {
                                    options.write().remove(i);
                                },
                            }
                        }
                    }
                }
                div { class: "composer-dialog-row",
                    Button {
                        kind: ButtonKind::Secondary,
                        icon: IconName::Add,
                        onclick: move |_| options.write().push(String::new()),
                        "添加选项"
                    }
                }
            }
        }
    }
}
