//! 更多面板：成员、公告、备注、置顶、消息设置、关注、屏蔽此人、话题操作。

use super::actions;
use super::sidebar::{MemberRow, build_members, filter_members, topic_stream, use_channel_members};
use super::store::Chat;
use crate::ui::{
    Button, ButtonKind, DialogFrame, Icon, IconButton, IconName, ListRow, Picker, PickerItem,
    RowDensity, Select, SelectOption, SettingCard, SettingRow, TextInput, TextPrompt, Toggle,
};
use crate::widgets::{SessionRef, cached_avatar, topic_action_items};
use crate::windows;
use dioxus::prelude::*;
use std::sync::Arc;
use zulip_core::model::{Channel, PinItem, PinScope, RemarkTarget};
use zulip_core::push::{DndLevel, TopicVisibility};
use zulip_core::text::time;
use zulip_core::{ConversationKey, links};
use zulip_session::{DeleteTarget, RenameTarget, Session};

const MEMBER_PREVIEW: usize = 10;
const REMARK_MAX: usize = 50;

fn level_value(level: DndLevel) -> &'static str {
    match level {
        DndLevel::Notify => "notify",
        DndLevel::Silent => "silent",
        DndLevel::Assistant => "assistant",
        DndLevel::Blocked => "blocked",
    }
}

fn level_of(value: &str) -> Option<DndLevel> {
    match value {
        "notify" => Some(DndLevel::Notify),
        "silent" => Some(DndLevel::Silent),
        "assistant" => Some(DndLevel::Assistant),
        "blocked" => Some(DndLevel::Blocked),
        _ => None,
    }
}

fn remark_target(key: &ConversationKey) -> RemarkTarget {
    match key {
        ConversationKey::Dm(d) if d.other_user_ids().len() == 1 => {
            RemarkTarget::User(d.other_user_ids()[0])
        }
        key => RemarkTarget::Conversation(key.clone()),
    }
}

fn edit_remark(chat: Chat, target: RemarkTarget, original: String) {
    chat.spawn(async move {
        let session = chat.session();
        let current = session.remark(&target);
        let prompt = TextPrompt::remark("备注", &original, current.as_deref()).max_len(REMARK_MAX);
        if let Some(text) = chat.overlays.prompt_text(prompt).await {
            chat.overlays
                .attempt(
                    "设置备注失败",
                    session.rename(RenameTarget::Remark(target), &text),
                )
                .await;
        }
    });
}

fn change_members(chat: Chat, channel: Channel, ids: Vec<i64>, add: bool) {
    chat.spawn(async move {
        let session = chat.session();
        let candidates: Vec<PickerItem> = if add {
            session
                .users()
                .into_iter()
                .filter(|u| u.is_active && !ids.contains(&u.id))
                .map(|u| picker_item(&session, u.id, u.avatar_url.as_deref()))
                .collect()
        } else {
            ids.iter()
                .filter(|id| **id != session.self_id())
                .map(|id| {
                    let avatar = session.user(*id).and_then(|u| u.avatar_url);
                    picker_item(&session, *id, avatar.as_deref())
                })
                .collect()
        };
        let (title, confirm) = if add {
            ("邀请成员", "邀请")
        } else {
            ("移出成员", "移出")
        };
        let picker = Picker::multi(title, candidates, Vec::new()).confirm_text(confirm);
        let Some(picked) = chat.overlays.pick(picker).await else {
            return;
        };
        let users: Vec<i64> = picked.iter().map(|id| *id as i64).collect();
        if users.is_empty() {
            return;
        }
        if add {
            chat.overlays
                .attempt("邀请失败", session.add_subscribers(&channel.name, &users))
                .await;
        } else {
            chat.overlays
                .attempt("移出失败", session.remove_subscribers(&channel.name, &users))
                .await;
        }
    });
}

fn picker_item(session: &Arc<Session>, id: i64, avatar: Option<&str>) -> PickerItem {
    let item = PickerItem::new(id as u64, session.display_name(id));
    match avatar.and_then(|url| cached_avatar(session, url)) {
        Some(local) => item.avatar(local),
        None => item,
    }
}

fn show_all_members(chat: Chat, channel: Option<Channel>, ids: Vec<i64>) {
    let session = chat.session();
    let overlays = chat.overlays;
    overlays.open_dialog(true, move |handle| {
        rsx! {
            AllMembersDialog {
                session: SessionRef(session.clone()),
                channel: channel.clone(),
                ids: ids.clone(),
                on_close: move |()| handle.close(),
            }
        }
    });
}

#[component]
fn AllMembersDialog(
    session: SessionRef,
    channel: Option<Channel>,
    ids: Vec<i64>,
    on_close: EventHandler<()>,
) -> Element {
    let mut query = use_signal(String::new);
    let overlays = crate::ui::use_overlays();
    let all = build_members(&session.0, channel.as_ref(), &ids);
    let shown = filter_members(&all, &query());
    rsx! {
        DialogFrame { title: "频道成员 ({all.len()})", width: 360.0, on_close,
            div { class: "members-dialog",
                TextInput {
                    value: query(),
                    placeholder: "搜索成员",
                    autofocus: true,
                    clearable: true,
                    oninput: move |v| query.set(v),
                }
                div { class: "history-list scroll",
                    for member in shown {
                        {
                            let id = member.id;
                            rsx! {
                                ListRow {
                                    key: "{id}",
                                    density: RowDensity::Compact,
                                    leading: rsx! { crate::widgets::RemoteAvatar { text: member.name.clone(), url: member.avatar.clone(), size: 24.0 } },
                                    title: member.name.clone(),
                                    subtitle: member.badge.map(str::to_owned),
                                    onclick: move |e: MouseEvent| {
                                        let at = e.client_coordinates();
                                        crate::widgets::open_user_card(
                                            overlays,
                                            crate::ui::Rect { x: at.x, y: at.y, w: 4.0, h: 4.0 },
                                            id,
                                        );
                                    },
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}

#[component]
fn PanelLink(
    #[props(into)] label: String,
    #[props(into)] value: Option<String>,
    #[props(default)] danger: bool,
    onclick: EventHandler<MouseEvent>,
) -> Element {
    rsx! {
        div {
            class: if danger { "panel-link danger" } else { "panel-link" },
            onclick: move |e| onclick.call(e),
            span { class: "panel-link-label", "{label}" }
            if let Some(value) = value {
                span { class: "panel-link-value", "{value}" }
            }
            Icon { name: IconName::ChevronRight, size: 12.0 }
        }
    }
}

#[component]
pub fn ChatMorePanel() -> Element {
    let chat = use_context::<Chat>();
    let mut tick = use_signal(|| 0u32);
    let _ = tick();
    let key = chat.key.read().clone();
    let session = chat.session();
    let account = chat.account;
    let overlays = chat.overlays;
    let is_topic = matches!(key, ConversationKey::Topic(_));
    let (is_single, is_group_dm) = match &key {
        ConversationKey::Dm(d) => (d.other_user_ids().len() == 1, d.is_group_dm()),
        ConversationKey::Topic(_) => (false, false),
    };
    let peer = match &key {
        ConversationKey::Dm(d) if is_single => Some(d.other_user_ids()[0]),
        _ => None,
    };
    let ids = use_channel_members(chat);
    let channel = topic_stream(&key).and_then(|id| session.channel(id));
    let dm_members: Vec<i64> = match &key {
        ConversationKey::Dm(d) => {
            let mut list = d.other_user_ids().to_vec();
            list.push(session.self_id());
            list
        }
        ConversationKey::Topic(_) => Vec::new(),
    };
    let member_ids = if is_topic { ids.read().clone() } else { dm_members };
    let members = build_members(&session, channel.as_ref(), &member_ids);
    let member_summary = format!(
        "查看{}名{}",
        members.len(),
        if is_topic { "频道成员" } else { "成员" }
    );

    let pin_item = PinItem::Conversation(key.clone());
    let pinned = session.is_pinned(PinScope::Messages, &pin_item);
    let level = session.conversation_level(&key);
    let remark_for = remark_target(&key);
    let remark = session.remark(&remark_for);
    let remark_label = if is_topic { "话题备注" } else { "备注" };
    let original_title = session.title(&key);
    let scheduled = session.scheduled_for(&key);
    let use_24h = session.settings().twenty_four_hour_time;
    let now = chrono::Local::now().naive_local();
    let permissions = session.permissions();
    let can_add = channel
        .as_ref()
        .is_some_and(|c| permissions.can_add_subscribers(&c.permissions));
    let can_remove = channel
        .as_ref()
        .is_some_and(|c| permissions.can_remove_subscribers(&c.permissions));
    let followed = if let ConversationKey::Topic(t) = &key {
        session.topic_visibility(t.stream_id, &session.topic_name(&key)) == TopicVisibility::Followed
    } else {
        false
    };

    let pin_session = session.clone();
    let pin_value = pin_item.clone();
    let level_session = session.clone();
    let level_key = key.clone();
    let quiet_session = session.clone();
    let quiet_key = key.clone();
    let follow_key = key.clone();
    let history_key = key.clone();
    let share_key = key.clone();
    let share_session = session.clone();
    let action_key = key.clone();
    let action_session = session.clone();
    let all_channel = channel.clone();
    let all_ids = member_ids.clone();
    let invite_channel = channel.clone();
    let invite_ids = member_ids.clone();
    let kick_channel = channel.clone();
    let kick_ids = member_ids.clone();

    rsx! {
        div {
            class: "more-backdrop",
            onclick: move |_| {
                let mut open = chat.more_open;
                open.set(false);
            },
        }
        div { class: "more-panel scroll",
            div { class: "more-head",
                span { "聊天设置" }
                IconButton {
                    icon: IconName::Close,
                    size: 24.0,
                    icon_size: 12.0,
                    onclick: move |_| {
                        let mut open = chat.more_open;
                        open.set(false);
                    },
                }
            }
            SettingCard {
                div { class: "panel-members-head",
                    span { if is_topic { "频道成员" } else { "聊天成员" } }
                    div {
                        class: "panel-link-value clickable",
                        onclick: move |_| show_all_members(chat, all_channel.clone(), all_ids.clone()),
                        "{member_summary}"
                        Icon { name: IconName::ChevronRight, size: 12.0 }
                    }
                }
                div { class: "panel-members",
                    for member in members.iter().take(MEMBER_PREVIEW).cloned() {
                        div { key: "{member.id}", class: "panel-member",
                            MemberRow { member }
                        }
                    }
                }
                if can_add || can_remove {
                    div { class: "panel-member-actions",
                        if can_add {
                            Button {
                                kind: ButtonKind::Secondary,
                                onclick: move |_| {
                                    if let Some(channel) = invite_channel.clone() {
                                        change_members(chat, channel, invite_ids.clone(), true);
                                    }
                                },
                                "邀请"
                            }
                        }
                        if can_remove {
                            Button {
                                kind: ButtonKind::Secondary,
                                onclick: move |_| {
                                    if let Some(channel) = kick_channel.clone() {
                                        change_members(chat, channel, kick_ids.clone(), false);
                                    }
                                },
                                "移出"
                            }
                        }
                    }
                }
            }
            if is_topic {
                SettingCard {
                    div { class: "panel-notice",
                        div { class: "panel-notice-title", "频道公告" }
                        match channel.as_ref().map(|c| c.rendered_description.clone()).filter(|d| !d.trim().is_empty()) {
                            Some(html) => rsx! { div { class: "selectable panel-notice-body", dangerous_inner_html: "{html}" } },
                            None => rsx! { div { class: "text-sub", "暂无公告" } },
                        }
                    }
                }
            }
            SettingCard {
                PanelLink {
                    label: remark_label,
                    value: remark.unwrap_or_else(|| "未设置".to_owned()),
                    onclick: move |_| {
                        edit_remark(chat, remark_for.clone(), original_title.clone());
                        tick += 1;
                    },
                }
                PanelLink {
                    label: "查找聊天记录",
                    onclick: move |_| windows::open_history(account, history_key.clone(), None),
                }
                PanelLink {
                    label: "文件传输列表",
                    onclick: move |_| windows::open_files(account),
                }
                if is_topic {
                    PanelLink {
                        label: "分享",
                        onclick: move |_| {
                            if let ConversationKey::Topic(t) = &share_key {
                                let channel_name = share_session.channel(t.stream_id).map_or_else(String::new, |c| c.name);
                                let link = links::topic_link(
                                    share_session.realm_url(),
                                    t.stream_id,
                                    &channel_name,
                                    &share_session.topic_name(&share_key),
                                );
                                overlays.copy_text(&link);
                            }
                        },
                    }
                }
            }
            if !scheduled.is_empty() {
                SettingCard {
                    div { class: "panel-notice",
                        div { class: "panel-notice-title", "定时消息 ({scheduled.len()})" }
                        for item in scheduled {
                            {
                                let id = item.id;
                                let when = time::from_unix(item.deliver_at)
                                    .map_or_else(String::new, |t| time::precise_time(t, now, use_24h));
                                rsx! {
                                    ListRow {
                                        key: "{id}",
                                        density: RowDensity::Compact,
                                        title: item.content.clone(),
                                        subtitle: when,
                                        trailing: rsx! {
                                            IconButton {
                                                icon: IconName::Delete,
                                                size: 22.0,
                                                icon_size: 13.0,
                                                onclick: move |_| {
                                                    chat.spawn(async move {
                                                        let session = chat.session();
                                                        overlays
                                                            .delete("这条定时消息", None, || {
                                                                session.delete(DeleteTarget::ScheduledMessage(id))
                                                            })
                                                            .await;
                                                        tick += 1;
                                                    });
                                                },
                                            }
                                        },
                                    }
                                }
                            }
                        }
                    }
                }
            }
            SettingCard {
                SettingRow { label: "设为置顶",
                    Toggle {
                        checked: pinned,
                        onchange: move |on: bool| {
                            pin_session.set_pinned(PinScope::Messages, &pin_value, on);
                            tick += 1;
                        },
                    }
                }
                if is_topic || is_group_dm {
                    SettingRow { label: "消息设置",
                        Select {
                            options: vec![
                                SelectOption::new("notify", "允许消息提醒"),
                                SelectOption::new("silent", "接收消息但不提醒"),
                                SelectOption::new("assistant", "收进话题助手且不提醒"),
                                SelectOption::new("blocked", "屏蔽消息"),
                            ],
                            value: level_value(level).to_owned(),
                            onchange: move |value: String| {
                                if let Some(level) = level_of(&value) {
                                    level_session.set_conversation_level(&level_key, level);
                                    tick += 1;
                                }
                            },
                        }
                    }
                } else {
                    SettingRow { label: "消息免打扰",
                        Toggle {
                            checked: level != DndLevel::Notify,
                            onchange: move |on: bool| {
                                quiet_session.set_conversation_level(
                                    &quiet_key,
                                    if on { DndLevel::Silent } else { DndLevel::Notify },
                                );
                                tick += 1;
                            },
                        }
                    }
                }
                if is_topic {
                    SettingRow { label: "关注",
                        Toggle {
                            checked: followed,
                            onchange: move |on: bool| {
                                let key = follow_key.clone();
                                chat.spawn(async move {
                                    let visibility = if on { TopicVisibility::Followed } else { TopicVisibility::Inherit };
                                    overlays
                                        .attempt("操作失败", chat.session().set_topic_visibility(&key, visibility))
                                        .await;
                                    tick += 1;
                                });
                            },
                        }
                    }
                }
                if let Some(user) = peer {
                    SettingRow { label: "屏蔽此人",
                        Toggle {
                            checked: session.is_user_muted(user),
                            onchange: move |on: bool| {
                                actions::toggle_muted(chat, user, on);
                                tick += 1;
                            },
                        }
                    }
                }
            }
            if is_topic {
                SettingCard {
                    PanelLink {
                        label: "话题操作",
                        onclick: move |e: MouseEvent| {
                            let at = e.client_coordinates();
                            overlays.show_menu(
                                at.x,
                                at.y,
                                topic_action_items(&action_session, overlays, &action_key, false),
                            );
                        },
                    }
                }
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn level_values_round_trip() {
        for level in [
            DndLevel::Notify,
            DndLevel::Silent,
            DndLevel::Assistant,
            DndLevel::Blocked,
        ] {
            assert_eq!(level_of(level_value(level)), Some(level));
        }
        assert_eq!(level_of("x"), None);
    }
}
