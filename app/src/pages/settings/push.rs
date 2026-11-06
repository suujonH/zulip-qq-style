//! 强化推送：手机推送、邮件、@全体、话题自动关注，以及各频道、话题的推送设置。所有修改立即提交到 Zulip。

use super::Store;
use super::channels::{ChannelBlock, MOCK_TEXT, shows};
use super::field::*;
use crate::ui::*;
use dioxus::dioxus_core::spawn_forever;
use dioxus::prelude::*;
use serde_json::json;
use std::sync::Arc;
use zulip_core::ConversationKey;
use zulip_core::model::{Channel, UserSettings};
use zulip_core::push::{PushRules, TopicVisibility};
use zulip_session::Session;

const BATCHING: [(i32, &str); 8] = [
    (120, "2 分钟"),
    (300, "5 分钟"),
    (600, "10 分钟"),
    (1800, "30 分钟"),
    (3600, "1 小时"),
    (21600, "6 小时"),
    (86400, "1 天"),
    (604800, "1 周"),
];
const REALM_NAME: [(i32, &str); 3] = [(1, "自动"), (2, "总是"), (3, "从不")];
const AUTO_POLICY: [(i32, &str); 4] = [
    (1, "我参与的话题"),
    (2, "我发过消息的话题"),
    (3, "我发起的话题"),
    (4, "从不"),
];

fn options(items: &[(i32, &str)]) -> Vec<(i32, String)> {
    items.iter().map(|(v, t)| (*v, (*t).to_owned())).collect()
}

/// 邮件合并等待时间：当前值不在固定选项中时（在网页端设置）末尾加“自定义”。
fn batching_options(current: i32) -> Vec<(i32, String)> {
    let mut list = options(&BATCHING);
    if !BATCHING.iter().any(|(v, _)| *v == current) {
        list.push((current, format!("自定义（{current} 秒）")));
    }
    list
}

fn email_rules(s: &UserSettings) -> PushRules {
    PushRules {
        dm_and_mention: s.enable_offline_email_notifications,
        stream: s.enable_stream_email_notifications,
        followed_topic: s.enable_followed_topic_email_notifications,
        wildcard_mentions: s.wildcard_mentions_notify,
        followed_topic_wildcard_mentions: s.enable_followed_topic_wildcard_mentions_notify,
    }
}

fn phone_rules(s: &UserSettings) -> PushRules {
    PushRules {
        dm_and_mention: s.enable_offline_push_notifications,
        stream: s.enable_stream_push_notifications,
        followed_topic: s.enable_followed_topic_push_notifications,
        wildcard_mentions: s.wildcard_mentions_notify,
        followed_topic_wildcard_mentions: s.enable_followed_topic_wildcard_mentions_notify,
    }
}

/// 话题（或没有单独设置的话题）会触发的手机推送和邮件，按服务器的规则计算。
fn effects(
    session: &Session,
    channel: &Channel,
    topic: TopicVisibility,
    phone: bool,
) -> Vec<(IconName, String)> {
    let settings = session.settings();
    let sub = channel.subscription.clone().unwrap_or_default();
    let line = |icon, label: &str, rules: PushRules, all: Option<bool>| {
        let triggers = rules.evaluate(sub.is_muted, all, sub.wildcard_mentions_notify, topic);
        (icon, format!("{label}：{triggers}"))
    };
    let mut lines = Vec::new();
    if phone {
        lines.push(line(
            IconName::Mobile,
            "手机推送",
            phone_rules(&settings),
            sub.push_notifications,
        ));
    }
    lines.push(line(
        IconName::Mail,
        "邮件",
        email_rules(&settings),
        sub.email_notifications,
    ));
    lines
}

#[component]
fn Effects(lines: Vec<(IconName, String)>) -> Element {
    rsx! {
        div { class: "settings-effects",
            for (icon, text) in lines {
                Tooltip { key: "{text}", text: text.clone(),
                    div { class: "settings-effect text-sub",
                        Icon { name: icon, size: 12.0 }
                        span { "{text}" }
                    }
                }
            }
        }
    }
}

/// 行尾的图标开关：高亮为开启。
#[component]
fn Flag(
    icon: IconName,
    #[props(into)] tip: String,
    on: bool,
    #[props(default)] disabled: bool,
    onclick: EventHandler<()>,
) -> Element {
    rsx! {
        Tooltip { text: tip,
            IconButton {
                icon,
                size: 28.0,
                disabled,
                class: if on { "settings-flag on" } else { "settings-flag" },
                onclick: move |_| onclick.call(()),
            }
        }
    }
}

/// 手机/邮件两列的一行。
#[component]
fn MatrixRow(
    #[props(into)] label: String,
    phone: Option<Bind<bool>>,
    email: Bind<bool>,
    /// 只有一个开关，同时决定手机推送和邮件。
    #[props(default)]
    single: bool,
) -> Element {
    rsx! {
        SettingRow { label, description: email.hint.clone(),
            div { class: "settings-cells",
                if let Some(phone) = phone {
                    div { class: "settings-cell",
                        Toggle { checked: phone.value, onchange: move |v| phone.set(v) }
                    }
                }
                div { class: if single { "settings-cell wide" } else { "settings-cell" },
                    Toggle { checked: email.value, onchange: move |v| email.set(v) }
                }
            }
        }
    }
}

/// 手机推送相关的设置：服务器没有开启推送时（开发者模式下显示）只模拟。
fn phone_bind(bind: Bind<bool>, available: bool, overlays: Overlays) -> Bind<bool> {
    if available {
        bind
    } else {
        Bind::new(bind.value, move |_| overlays.toast(MOCK_TEXT))
    }
}

async fn set_subscription(
    session: Arc<Session>,
    overlays: Overlays,
    stream_id: i64,
    property: &'static str,
    on: bool,
) {
    overlays
        .attempt(
            "设置失败",
            session.set_subscription_property(stream_id, property, json!(on)),
        )
        .await;
}

#[component]
fn ChannelFlags(stream_id: i64, push_available: bool) -> Element {
    let store = consume_context::<Store>();
    let _ = store.rev.read();
    let overlays = use_overlays();
    let session = store.session();
    let Some(channel) = session.channel(stream_id) else {
        return rsx! {};
    };
    let settings = session.settings();
    let sub = channel.subscription.clone().unwrap_or_default();
    let editable = !channel.is_archived;
    let push_all = sub
        .push_notifications
        .unwrap_or(settings.enable_stream_push_notifications);
    let email_all = sub
        .email_notifications
        .unwrap_or(settings.enable_stream_email_notifications);
    let wildcard = sub
        .wildcard_mentions_notify
        .unwrap_or(settings.wildcard_mentions_notify);
    let lines = effects(&session, &channel, TopicVisibility::Inherit, push_available);
    let flag = |property: &'static str, on: bool| {
        let session = session.clone();
        move |()| {
            spawn_forever(set_subscription(
                session.clone(),
                overlays,
                stream_id,
                property,
                !on,
            ));
        }
    };
    rsx! {
        Effects { lines }
        Flag { icon: IconName::NotificationOff, tip: "频道静音", on: sub.is_muted, disabled: !editable, onclick: flag("is_muted", sub.is_muted) }
        if push_available {
            Flag { icon: IconName::Mobile, tip: "手机推送所有消息", on: push_all, disabled: !editable, onclick: flag("push_notifications", push_all) }
        }
        Flag { icon: IconName::Mail, tip: "邮件发送所有消息", on: email_all, disabled: !editable, onclick: flag("email_notifications", email_all) }
        Flag { icon: IconName::Mention, tip: "@全体成员时推送和发邮件", on: wildcard, disabled: !editable, onclick: flag("wildcard_mentions_notify", wildcard) }
    }
}

/// 话题的推送设置：跟随频道、静音（取消静音）、关注，同时只有一个高亮。
#[component]
fn TopicFlags(stream_id: i64, topic: String, push_available: bool) -> Element {
    let store = consume_context::<Store>();
    let _ = store.rev.read();
    let overlays = use_overlays();
    let session = store.session();
    let Some(channel) = session.channel(stream_id) else {
        return rsx! {};
    };
    let policy = session.topic_visibility(stream_id, &topic);
    let editable = !channel.is_archived;
    let channel_muted = channel.is_muted();
    let mute_set = matches!(policy, TopicVisibility::Muted | TopicVisibility::Unmuted);
    let unmute_option =
        policy == TopicVisibility::Unmuted || (policy != TopicVisibility::Muted && channel_muted);
    let lines = effects(&session, &channel, policy, push_available);
    let set = |visibility: TopicVisibility| {
        let session = session.clone();
        let key = ConversationKey::topic(stream_id, topic.clone());
        move |()| {
            let (session, key) = (session.clone(), key.clone());
            spawn_forever(async move {
                overlays
                    .attempt("设置失败", session.set_topic_visibility(&key, visibility))
                    .await;
            });
        }
    };
    let inherit = TopicVisibility::Inherit;
    let toggle_mute = if mute_set {
        inherit
    } else if channel_muted {
        TopicVisibility::Unmuted
    } else {
        TopicVisibility::Muted
    };
    let toggle_follow = if policy == TopicVisibility::Followed {
        inherit
    } else {
        TopicVisibility::Followed
    };
    rsx! {
        Effects { lines }
        Flag { icon: IconName::Link, tip: "跟随频道", on: policy == inherit, disabled: !editable, onclick: set(inherit) }
        Flag {
            icon: if unmute_option { IconName::Notification } else { IconName::NotificationOff },
            tip: if unmute_option { "取消静音（频道静音时照常通知）" } else { "静音" },
            on: mute_set,
            disabled: !editable,
            onclick: set(toggle_mute),
        }
        Flag { icon: IconName::Star, tip: "关注", on: policy == TopicVisibility::Followed, disabled: !editable, onclick: set(toggle_follow) }
    }
}

#[component]
pub fn EnhancedPush() -> Element {
    let store = consume_context::<Store>();
    let _ = store.rev.read();
    let overlays = use_overlays();
    let session = store.session();
    let push_enabled = session.realm().push_notifications_enabled;
    let push_available = shows(push_enabled);
    let phone = |name: &'static [&'static str]| -> Option<Bind<bool>> {
        push_available.then(|| phone_bind(field(Source::Zulip(name)), push_enabled, overlays))
    };
    let batching = field::<i32>(Source::Zulip(&[
        "email_notifications_batching_period_seconds",
    ]));
    let batching_list = batching_options(batching.value);
    let mut channels = session.subscribed_channels();
    channels.sort_by_key(|c| c.name.to_lowercase());
    let (archived, active): (Vec<_>, Vec<_>) = channels.into_iter().partition(|c| c.is_archived);
    let block = |stream_id: i64| {
        let session = session.clone();
        rsx! {
            ChannelBlock {
                key: "{stream_id}",
                stream_id,
                trailing: rsx! { ChannelFlags { stream_id, push_available } },
                topic: move |topic: String| {
                    let title = session.topic_title_with_original(&ConversationKey::topic(stream_id, topic.clone()));
                    rsx! {
                        SettingRow { label: title,
                            TopicFlags { stream_id, topic: topic.clone(), push_available }
                        }
                    }
                },
            }
        }
    };
    rsx! {
        SettingsPage { title: "强化推送",
            Group { title: if push_available { "推送和邮件的内容" } else { "邮件的内容" },
                if push_available {
                    SettingRow { label: "",
                        div { class: "settings-cells text-sub",
                            div { class: "settings-cell", "手机" }
                            div { class: "settings-cell", "邮件" }
                        }
                    }
                }
                MatrixRow {
                    label: "私信和 @我",
                    phone: phone(&["enable_offline_push_notifications"]),
                    email: field(Source::Zulip(&["enable_offline_email_notifications"])),
                }
                MatrixRow {
                    label: "关注的话题中的所有消息",
                    phone: phone(&["enable_followed_topic_push_notifications"]),
                    email: field(Source::Zulip(&["enable_followed_topic_email_notifications"])),
                }
                MatrixRow {
                    label: "频道中的所有消息",
                    phone: phone(&["enable_stream_push_notifications"]),
                    email: field(Source::Zulip(&["enable_stream_email_notifications"])),
                }
                MatrixRow {
                    label: "@全体成员",
                    email: field(Source::Zulip(&["wildcard_mentions_notify"])),
                    single: push_available,
                }
                MatrixRow {
                    label: "关注的话题中 @全体成员",
                    email: field(Source::Zulip(&["enable_followed_topic_wildcard_mentions_notify"])),
                    single: push_available,
                }
            }
            if push_available {
                Group { title: "推送选项",
                    ToggleRow {
                        label: "在线时也推送",
                        bind: phone_bind(field(Source::Zulip(&["enable_online_push_notifications"])), push_enabled, overlays),
                    }
                }
            }
            Group { title: "邮件选项",
                ChoiceRow { label: "邮件合并等待时间", bind: batching, options: batching_list }
                ToggleRow { label: "邮件中包含消息内容", bind: field(Source::Zulip(&["message_content_in_email_notifications"])) }
                ChoiceRow {
                    label: "邮件标题中包含组织名称",
                    bind: field(Source::Zulip(&["realm_name_in_email_notifications_policy"])),
                    options: options(&REALM_NAME),
                }
                ToggleRow { label: "长时间未登录时发送摘要邮件", bind: field(Source::Zulip(&["enable_digest_emails"])) }
            }
            Group { title: "话题自动关注",
                ChoiceRow {
                    label: "自动关注话题",
                    bind: field(Source::Zulip(&["automatically_follow_topics_policy"])),
                    options: options(&AUTO_POLICY),
                }
                ToggleRow { label: "被 @ 时自动关注话题", bind: field(Source::Zulip(&["automatically_follow_topics_where_mentioned"])) }
                ChoiceRow {
                    label: "在静音频道中自动取消话题静音",
                    bind: field(Source::Zulip(&["automatically_unmute_topics_in_muted_streams_policy"])),
                    options: options(&AUTO_POLICY),
                }
            }
            div { class: "setting-section", "频道与话题" }
            SettingCard {
                for channel in active {
                    {block(channel.stream_id)}
                }
                if !archived.is_empty() {
                    Expander { title: format!("已归档的频道（{}）", archived.len()),
                        for channel in archived {
                            {block(channel.stream_id)}
                        }
                    }
                }
            }
        }
    }
}
