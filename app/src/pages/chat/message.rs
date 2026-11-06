//! 消息行：气泡、内容、表情回应、插件注解、状态。

use super::actions;
use super::content::{RenderOptions, Rendered, Segment, render};
use super::events::set_image;
use super::store::Chat;
use super::widget_card::WidgetCard;
use crate::services::plugins;
use crate::ui::{Checkbox, Icon, IconButton, IconName, Rect};
use crate::widgets::{
    FileItemCard, RemoteAvatar, cached_avatar, downloaded_image, format_size, local_image,
    open_user_card,
};
use crate::windows;
use dioxus::dioxus_core::Task;
use dioxus::prelude::*;
use std::cell::RefCell;
use std::collections::HashMap;
use std::collections::hash_map::DefaultHasher;
use std::hash::{Hash, Hasher};
use std::rc::Rc;
use std::time::Duration;
use zulip_core::ConversationKey;
use zulip_core::html::{UploadLink, is_attachment_only};
use zulip_core::model::{ChatMessage, MessageState, Reaction};
use zulip_core::notice;
use zulip_core::text::time;
use zulip_core::{emoji, widget};
use zulip_plugin_host::{ActionOutcome, ActionView, AnnotationCard};
use zulip_session::Session;

const AVATAR_SIZE: f64 = 32.0;
const DOUBLE_CLICK: Duration = Duration::from_millis(220);
const RENDER_CACHE_MAX: usize = 4096;

thread_local! {
    // 列表滚动时行组件反复创建，解析过的内容不用再解析
    static RENDER_CACHE: RefCell<HashMap<u64, Rc<Rendered>>> = RefCell::default();
    static FILE_SIZES: RefCell<HashMap<String, u64>> = RefCell::default();
}

fn rendered_html(session: &Session, html: &str, me: Option<&str>) -> Rc<Rendered> {
    let mut hasher = DefaultHasher::new();
    (session.account_id(), html, me).hash(&mut hasher);
    let key = hasher.finish();
    let cached = RENDER_CACHE.with_borrow(|c| c.get(&key).cloned());
    if let Some(cached) = cached
        && !cached
            .pending
            .iter()
            .any(|url| downloaded_image(session, url).is_some())
    {
        return cached;
    }
    let result = Rc::new(render(
        html,
        &RenderOptions {
            me,
            local_image: &|url| downloaded_image(session, url),
        },
    ));
    RENDER_CACHE.with_borrow_mut(|c| {
        if c.len() >= RENDER_CACHE_MAX {
            c.clear();
        }
        c.insert(key, result.clone());
    });
    result
}

/// 同一表情的回应合并。
#[derive(Clone, Debug, PartialEq)]
pub struct ReactionGroup {
    pub name: String,
    pub code: String,
    pub kind: String,
    pub users: Vec<i64>,
}

pub fn group_reactions(reactions: &[Reaction]) -> Vec<ReactionGroup> {
    let mut groups: Vec<ReactionGroup> = Vec::new();
    for r in reactions {
        match groups
            .iter_mut()
            .find(|g| g.code == r.emoji_code && g.kind == r.reaction_type)
        {
            Some(group) => group.users.push(r.user_id),
            None => groups.push(ReactionGroup {
                name: r.emoji_name.clone(),
                code: r.emoji_code.clone(),
                kind: r.reaction_type.clone(),
                users: vec![r.user_id],
            }),
        }
    }
    groups
}

#[component]
pub fn NoticeRow(id: i64) -> Element {
    let chat = use_context::<Chat>();
    let message = use_memo(move || chat.messages.read().get(&id).cloned());
    let Some(m) = message() else {
        return rsx! {};
    };
    let html = notice::localize(&m.content_html);
    let session = chat.session();
    let rendered = rendered_html(&session, &html, None);
    rsx! {
        div { class: "chat-notice",
            "data-msg": "{id}",
            div { class: "msg-content",
                for (i, segment) in rendered.segments.iter().enumerate() {
                    if let Segment::Html(html) = segment {
                        div { key: "{i}", class: "msg-html selectable", dangerous_inner_html: "{html}" }
                    }
                }
            }
        }
    }
}

#[component]
pub fn DeletedRow(id: i64) -> Element {
    let chat = use_context::<Chat>();
    let message = use_memo(move || chat.messages.read().get(&id).cloned());
    let shown = use_memo(move || chat.revealed.read().contains(&id));
    let Some(m) = message() else {
        return rsx! {};
    };
    let name = chat.session().display_name(m.sender_id);
    let link = if shown() { "收起" } else { "查看" };
    rsx! {
        div { class: "chat-notice",
            span { "{name}的一条消息已被删除 " }
            span {
                class: "chat-notice-link",
                onclick: move |_| {
                    let mut revealed = chat.revealed;
                    if !revealed.write().remove(&id) {
                        revealed.write().insert(id);
                    }
                },
                "{link}"
            }
        }
        if shown() {
            MessageRow { id, force_show: true }
        }
    }
}

/// 名字、头像、是否屏蔽。资料变化时才变。
#[derive(Clone, PartialEq)]
struct SenderView {
    name: String,
    avatar: Option<String>,
    muted: bool,
    online: bool,
}

#[component]
pub fn MessageRow(id: i64, #[props(default)] force_show: bool) -> Element {
    let chat = use_context::<Chat>();
    let message = use_memo(move || chat.messages.read().get(&id).cloned());
    let selected = use_memo(move || chat.selected.read().contains(&id));
    let flashing = use_memo(move || *chat.flash.read() == Some(id));
    let revealed = use_memo(move || chat.revealed.read().contains(&id));
    let is_last_own = use_memo(move || *chat.last_own.read() == Some(id));
    let sender = use_memo(move || {
        let _ = (chat.users_tick)();
        let session = chat.session();
        let sender_id = message.peek().as_ref().map_or(0, |m| m.sender_id);
        let user = session.user(sender_id);
        SenderView {
            name: session.display_name(sender_id),
            avatar: user.and_then(|u| u.avatar_url).or_else(|| {
                message.peek().as_ref().and_then(|m| m.avatar_url.clone())
            }),
            muted: session.is_user_muted(sender_id),
            online: false,
        }
    });
    let mut click_task = use_signal(|| None::<Task>);
    let multi = (chat.multi)();
    let Some(m) = message() else {
        return rsx! {};
    };
    let session = chat.session();
    let own = m.sender_id == session.self_id();
    let SenderView {
        name,
        avatar,
        muted,
        online,
    } = sender();
    let key = chat.key.read().clone();
    let show_name = match &key {
        ConversationKey::Topic(_) => true,
        ConversationKey::Dm(d) => d.is_group_dm(),
    };
    let use_24h = session.settings().twenty_four_hour_time;
    let now = chrono::Local::now().naive_local();
    let sent_at = time::from_unix(m.timestamp);
    let precise = sent_at.map_or_else(String::new, |t| time::precise_time(t, now, use_24h));
    let head = format!("{name}: {precise}");
    let peer_read = if is_last_own() && own {
        (chat.peer_read)()
    } else {
        None
    };
    let mut class = String::from("msg-row");
    if own {
        class.push_str(" own");
    }
    if flashing() {
        class.push_str(" flash");
    }
    if multi && selected() {
        class.push_str(" checked");
    }
    let sender_id = m.sender_id;
    let hidden = muted && !revealed() && !force_show;
    let avatar_name = name.clone();
    rsx! {
        div {
            class,
            "data-msg": "{id}",
            "data-head": "{head}",
            onclick: move |_| {
                if *chat.multi.peek() {
                    chat.toggle_selected(id);
                }
            },
            if multi {
                div { class: "msg-check", onclick: |e: MouseEvent| e.stop_propagation(),
                    Checkbox { checked: selected(), onchange: move |_| chat.toggle_selected(id) }
                }
            }
            div { class: "msg-avatar",
                RemoteAvatar {
                    text: avatar_name,
                    url: avatar,
                    size: AVATAR_SIZE,
                    online,
                    onclick: move |e: MouseEvent| {
                        if *chat.multi.peek() {
                            return;
                        }
                        e.stop_propagation();
                        if let Some(task) = click_task.take() {
                            task.cancel();
                            let self_id = chat.session().self_id();
                            windows::open_chat(chat.account, ConversationKey::dm([sender_id], self_id), None);
                            return;
                        }
                        let at = e.client_coordinates();
                        let task = spawn(async move {
                            tokio::time::sleep(DOUBLE_CLICK).await;
                            click_task.set(None);
                            let rect = Rect { x: at.x, y: at.y, w: AVATAR_SIZE, h: AVATAR_SIZE };
                            open_user_card(chat.overlays, rect, sender_id);
                        });
                        click_task.set(Some(task));
                    },
                }
            }
            div { class: "msg-main",
                if show_name {
                    div { class: "msg-name", "{name}" }
                }
                div { class: "msg-line",
                    if hidden {
                        div {
                            class: "msg-muted",
                            onclick: move |e| {
                                e.stop_propagation();
                                let mut revealed = chat.revealed;
                                revealed.write().insert(id);
                            },
                            "已屏蔽的消息（点击查看）"
                        }
                    } else {
                        MessageBubble { message: m.clone(), own }
                    }
                    SendState { message: m.clone() }
                    span { class: "msg-time", "{precise}" }
                }
                if !hidden {
                    Reactions { message: m.clone() }
                    Annotations { id }
                }
                if let Some(read) = peer_read {
                    div { class: if read { "msg-read" } else { "msg-read unread" },
                        if read { "已读" } else { "未读" }
                    }
                }
            }
        }
    }
}

#[component]
fn SendState(message: std::sync::Arc<ChatMessage>) -> Element {
    let chat = use_context::<Chat>();
    match &message.state {
        MessageState::Sending(_) => rsx! {
            span { class: "msg-state", Icon { name: IconName::Loading, size: 14.0, class: "spin" } }
        },
        MessageState::Failed(echo) => {
            let local_id = echo.local_id.clone();
            rsx! {
                span { class: "msg-state failed",
                    title: "发送失败",
                    onclick: move |e: MouseEvent| {
                        e.stop_propagation();
                        let at = e.client_coordinates();
                        actions::failed_menu(chat, local_id.clone(), at.x, at.y);
                    },
                    Icon { name: IconName::ErrorCircle, size: 16.0 }
                }
            }
        }
        _ => rsx! {},
    }
}

#[component]
fn MessageBubble(message: std::sync::Arc<ChatMessage>, own: bool) -> Element {
    let chat = use_context::<Chat>();
    let id = message.id;
    let edit_time = message.last_edit_timestamp;
    let source_mode = (chat.source_mode)();
    let _ = (chat.plugin_tick)();
    let annotations = plugins().message_annotations(chat.account, id);
    let plugin_rendered = annotations.rendered.clone();

    let mut versions = use_signal(Vec::<String>::new);
    let mut version = use_signal(|| None::<usize>);
    use_effect(use_reactive!(|edit_time| {
        if edit_time.is_some() {
            spawn(async move {
                if let Ok(list) = chat.session().previous_contents(id).await {
                    version.set(None);
                    versions.set(list);
                }
            });
        }
    }));

    let list = versions.read();
    let shown_version = version().and_then(|i| list.get(i).cloned());
    let total = list.len();
    let current = version().unwrap_or(total.saturating_sub(1));
    drop(list);

    let html = shown_version.unwrap_or_else(|| match &plugin_rendered {
        Some(r) if !r.original_shown => r.html.clone(),
        _ => message.content_html.clone(),
    });
    let session = chat.session();
    let me = message
        .is_me_message
        .then(|| session.display_name(message.sender_id));
    let parsed_widget = widget::parse(&message.submessages, message.sender_id);
    let bare = parsed_widget.is_none() && !source_mode && is_attachment_only(&html);

    let source = if source_mode {
        chat.sources.read().get(&(id, edit_time)).cloned()
    } else {
        None
    };
    use_effect(move || {
        if (chat.source_mode)() && !chat.sources.peek().contains_key(&(id, edit_time)) {
            chat.spawn(async move {
                if let Ok(raw) = chat.session().raw_content(id).await {
                    let mut sources = chat.sources;
                    sources.write().insert((id, edit_time), raw);
                }
            });
        }
    });

    let mut class = String::from("bubble");
    class.push_str(if own { " own" } else { " other" });
    if bare {
        class.push_str(" bare");
    }
    if parsed_widget.is_some() {
        class.push_str(" widget");
    }
    rsx! {
        div { class,
            if let Some(raw) = source {
                pre { class: "msg-source selectable", "{raw}" }
            } else if let Some(card) = parsed_widget {
                WidgetCard { id, widget: card }
            } else {
                MessageContent { id, html, me }
            }
            if total > 1 {
                div { class: "msg-versions",
                    IconButton {
                        icon: IconName::ChevronLeft,
                        size: 18.0,
                        icon_size: 10.0,
                        disabled: current == 0,
                        onclick: move |e: MouseEvent| {
                            e.stop_propagation();
                            version.set(Some(current.saturating_sub(1)));
                        },
                    }
                    span { "{current + 1}/{total}" }
                    IconButton {
                        icon: IconName::ChevronRight,
                        size: 18.0,
                        icon_size: 10.0,
                        disabled: current + 1 >= total,
                        onclick: move |e: MouseEvent| {
                            e.stop_propagation();
                            version.set(Some(current + 1));
                        },
                    }
                }
            } else if edit_time.is_some() {
                div { class: "msg-versions", "已编辑" }
            }
            if let Some(r) = plugin_rendered {
                div {
                    class: "msg-render-toggle",
                    onclick: {
                        let shown = r.original_shown;
                        move |e: MouseEvent| {
                            e.stop_propagation();
                            plugins().set_original_shown(chat.account, id, !shown);
                            let mut tick = chat.plugin_tick;
                            tick += 1;
                        }
                    },
                    if r.original_shown {
                        "显示“{r.plugin_name}”的内容"
                    } else {
                        "显示原文"
                    }
                }
            }
        }
    }
}

/// 消息 HTML：`dangerous_inner_html` 只在内容变化时设置，所以点开的折叠块、已加载的图片、选中的文字都不会被重新渲染冲掉。
#[component]
pub fn MessageContent(id: i64, html: String, me: Option<String>) -> Element {
    let chat = use_context::<Chat>();
    let session = chat.session();
    let rendered = use_memo(use_reactive!(|html, me| rendered_html(
        &session,
        &html,
        me.as_deref()
    )));
    use_effect(move || {
        let current = rendered();
        for url in current.pending.iter().cloned() {
            chat.spawn(async move {
                let local = local_image(chat.session(), url.clone()).await;
                set_image(id, &url, local.as_deref());
            });
        }
    });
    let current = rendered();
    rsx! {
        div { class: "msg-content",
            for (i, segment) in current.segments.iter().enumerate() {
                match segment {
                    Segment::Html(html) => rsx! {
                        div { key: "{i}", class: "msg-html selectable", dangerous_inner_html: "{html}" }
                    },
                    Segment::File(link) => rsx! {
                        Attachment { key: "{i}", id, link: link.clone() }
                    },
                }
            }
        }
    }
}

#[component]
fn Attachment(id: i64, link: UploadLink) -> Element {
    let chat = use_context::<Chat>();
    let path = link.path.clone();
    let mut size = use_signal(|| FILE_SIZES.with_borrow(|c| c.get(&path).copied()));
    let path = link.path.clone();
    use_hook(move || {
        if size.peek().is_none() {
            spawn(async move {
                if let Ok(Some(bytes)) = chat.session().file_size(&path).await {
                    FILE_SIZES.with_borrow_mut(|c| c.insert(path.clone(), bytes));
                    size.set(Some(bytes));
                }
            });
        }
    });
    rsx! {
        div { class: "msg-attachment", onclick: |e: MouseEvent| e.stop_propagation(),
            FileItemCard {
                link,
                message_id: id,
                conversation: chat.key.read().clone(),
                detail: size().map(format_size),
            }
        }
    }
}

#[component]
fn Reactions(message: std::sync::Arc<ChatMessage>) -> Element {
    let chat = use_context::<Chat>();
    if message.reactions.is_empty() {
        return rsx! {};
    }
    let session = chat.session();
    let self_id = session.self_id();
    let groups = group_reactions(&message.reactions);
    let id = message.id;
    rsx! {
        div { class: "msg-reactions",
            for group in groups {
                ReactionChip {
                    key: "{group.kind}:{group.code}",
                    id,
                    mine: group.users.contains(&self_id),
                    group: group.clone(),
                }
            }
        }
    }
}

#[component]
fn ReactionChip(id: i64, group: ReactionGroup, mine: bool) -> Element {
    let chat = use_context::<Chat>();
    let session = chat.session();
    let catalog = session.emoji_catalog();
    let entry = catalog.get(&group.name).cloned();
    let image = entry
        .as_ref()
        .and_then(|e| e.image_url.as_deref())
        .and_then(|url| cached_avatar(&session, url));
    let unicode = emoji::unicode_from_code(&group.code);
    let count = group.users.len();
    let tip = group
        .users
        .iter()
        .map(|u| session.display_name(*u))
        .collect::<Vec<_>>()
        .join("、");
    let name = group.name.clone();
    rsx! {
        div {
            class: if mine { "reaction-chip mine" } else { "reaction-chip" },
            title: "{tip}",
            onclick: move |e: MouseEvent| {
                e.stop_propagation();
                actions::toggle_reaction_by_name(chat, id, &name);
            },
            if let Some(src) = image {
                img { class: "emoji-img", src }
            } else if let Some(text) = unicode {
                span { "{text}" }
            } else {
                span { ":{group.name}:" }
            }
            span { class: "reaction-count", "{count}" }
        }
    }
}

#[component]
fn Annotations(id: i64) -> Element {
    let chat = use_context::<Chat>();
    let _ = (chat.plugin_tick)();
    let cards = plugins().message_annotations(chat.account, id).cards;
    rsx! {
        for card in cards {
            AnnotationView { key: "{card.plugin_id}", id, card: CardProp(card) }
        }
    }
}

#[derive(Clone)]
struct CardProp(AnnotationCard);

impl PartialEq for CardProp {
    fn eq(&self, _: &Self) -> bool {
        false
    }
}

#[component]
fn AnnotationView(id: i64, card: CardProp) -> Element {
    let chat = use_context::<Chat>();
    let card = card.0;
    let collapsed = card.is_collapsed;
    let plugin_id = card.plugin_id.clone();
    let chevron = if collapsed {
        IconName::ChevronRight
    } else {
        IconName::ChevronDown
    };
    rsx! {
        div { class: "msg-annotation",
            div { class: "msg-annotation-head",
                span { class: "msg-annotation-name", "{card.plugin_name}" }
                span {
                    title: "{card.toggle_tooltip}",
                    IconButton {
                        icon: chevron,
                        size: 18.0,
                        icon_size: 10.0,
                        onclick: move |e: MouseEvent| {
                            e.stop_propagation();
                            plugins().set_annotation_collapsed(chat.account, id, &plugin_id, !collapsed);
                            let mut tick = chat.plugin_tick;
                            tick += 1;
                        },
                    }
                }
            }
            if !collapsed {
                div { class: "msg-annotation-text selectable", "{card.text}" }
                if !card.actions.is_empty() {
                    div { class: "msg-annotation-actions",
                        for (i, action) in card.actions.iter().cloned().enumerate() {
                            AnnotationAction { key: "{i}", action: ActionProp(action) }
                        }
                    }
                }
            }
        }
    }
}

#[derive(Clone)]
struct ActionProp(ActionView);

impl PartialEq for ActionProp {
    fn eq(&self, _: &Self) -> bool {
        false
    }
}

#[component]
fn AnnotationAction(action: ActionProp) -> Element {
    let chat = use_context::<Chat>();
    let view = action.0;
    let label = view.label.clone();
    let tip = view.tooltip.clone();
    let can_run = view.can_run;
    rsx! {
        button {
            class: "btn btn-flat msg-annotation-action",
            r#type: "button",
            title: "{tip}",
            disabled: !can_run,
            onclick: move |e: MouseEvent| {
                e.stop_propagation();
                match plugins().run_action(&view) {
                    Ok(ActionOutcome::Fill(text)) => {
                        let mut insert = chat.composer.insert;
                        insert.set(Some(text));
                    }
                    Ok(ActionOutcome::Done) => {}
                    Err(error) => chat.overlays.report_error("操作失败", error),
                }
            },
            "{label}"
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn reaction(name: &str, code: &str, user: i64) -> Reaction {
        Reaction {
            emoji_name: name.into(),
            emoji_code: code.into(),
            reaction_type: "unicode_emoji".into(),
            user_id: user,
            ..Reaction::default()
        }
    }

    #[test]
    fn reactions_group_by_emoji_in_first_seen_order() {
        let groups = group_reactions(&[
            reaction("a", "1", 1),
            reaction("b", "2", 2),
            reaction("a", "1", 3),
        ]);
        assert_eq!(groups.len(), 2);
        assert_eq!(groups[0].users, vec![1, 3]);
        assert_eq!(groups[1].name, "b");
    }
}
