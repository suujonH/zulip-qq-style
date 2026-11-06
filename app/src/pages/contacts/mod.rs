//! 联系人页：系统通知、消息提醒入口，好友分组和频道 / 话题。

use crate::pages::main_window::{RightPane, use_main_ui};
use crate::state::{use_app_state, use_local_setting, use_session_data};
use crate::ui::*;
use crate::widgets::{
    RemoteAvatar, channel_section_menu, key_id, level_menu_items, open_user_card, text_id,
    topic_more_submenu,
};
use crate::windows;
use dioxus::dioxus_core::spawn_forever;
use dioxus::prelude::*;
use std::collections::HashSet;
use std::rc::Rc;
use std::sync::Arc;
use zulip_core::ConversationKey;
use zulip_core::model::{PinItem, PinScope, PresenceState};
use zulip_session::{
    ChannelSection, DeleteTarget, FriendItem, FriendSection, FriendSectionKind, PinnedTopicRow,
    RenameTarget, Session, SessionEvent,
};

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
enum ContactsTab {
    #[default]
    Friends,
    Channels,
}

fn contacts_changed(event: &SessionEvent) -> bool {
    matches!(
        event,
        SessionEvent::ContactsChanged
            | SessionEvent::UserChanged(_)
            | SessionEvent::UsersChanged
            | SessionEvent::PresenceChanged(_)
            | SessionEvent::MutedUsersChanged
            | SessionEvent::ChannelChanged(_)
            | SessionEvent::ChannelsChanged
            | SessionEvent::TopicsChanged(_)
            | SessionEvent::UserTopicChanged { .. }
            | SessionEvent::ConversationChanged(_)
            | SessionEvent::ConversationsChanged
    )
}

/// 列表展开后的一行。
#[derive(Clone, PartialEq)]
enum Item {
    FriendHeader(FriendSectionKind, String, String),
    Friend(FriendItem, FriendSectionKind),
    PinnedHeader(usize),
    PinnedTopic(PinnedTopicRow),
    ChannelHeader(ChannelSection),
    Loading(i64),
    Topic(i64, String, ConversationKey),
}

impl Item {
    fn key(&self) -> u64 {
        text_id(&match self {
            Self::FriendHeader(kind, ..) => format!("fh:{kind:?}"),
            Self::Friend(item, kind) => format!("f:{kind:?}:{}", item.user_id),
            Self::PinnedHeader(_) => "ph".to_owned(),
            Self::PinnedTopic(row) => format!("pt:{}", key_id(&row.key)),
            Self::ChannelHeader(section) => format!("ch:{}", section.stream_id),
            Self::Loading(stream) => format!("l:{stream}"),
            Self::Topic(stream, name, _) => format!("t:{stream}:{name}"),
        })
    }
}

#[derive(Clone, Default, PartialEq)]
struct ContactsData {
    friends: Vec<FriendSection>,
    channels: Vec<ChannelSection>,
    pinned: Vec<PinnedTopicRow>,
    notices: usize,
    reminders: usize,
}

fn read_contacts(session: &Arc<Session>) -> ContactsData {
    ContactsData {
        friends: session.friend_sections(),
        channels: session.channel_sections(),
        pinned: session.pinned_topic_rows(),
        notices: session.unread_notice_count(),
        reminders: session.unread_reminder_count(),
    }
}

fn friend_items(data: &ContactsData, collapsed: &HashSet<String>) -> Vec<Item> {
    let mut items = Vec::new();
    for section in &data.friends {
        let id = format!("{:?}", section.kind);
        items.push(Item::FriendHeader(
            section.kind,
            section.title.clone(),
            section.count_text.clone(),
        ));
        if !collapsed.contains(&id) {
            items.extend(
                section
                    .items
                    .iter()
                    .map(|friend| Item::Friend(friend.clone(), section.kind)),
            );
        }
    }
    items
}

fn channel_items(data: &ContactsData, expanded: &HashSet<i64>) -> Vec<Item> {
    let mut items = Vec::new();
    if !data.pinned.is_empty() {
        items.push(Item::PinnedHeader(data.pinned.len()));
        items.extend(data.pinned.iter().cloned().map(Item::PinnedTopic));
    }
    for section in &data.channels {
        let stream = section.stream_id;
        items.push(Item::ChannelHeader(section.clone()));
        if expanded.contains(&stream) {
            match &section.topics {
                None => items.push(Item::Loading(stream)),
                Some(topics) => items.extend(topics.iter().map(|topic| {
                    Item::Topic(
                        stream,
                        topic.name.clone(),
                        ConversationKey::topic(stream, &topic.name),
                    )
                })),
            }
        }
    }
    items
}

/// 行组件读的上下文。
#[derive(Clone, Copy)]
struct ContactsContext {
    items: Signal<Rc<Vec<Item>>>,
    collapsed: Signal<HashSet<String>>,
    expanded: Signal<HashSet<i64>>,
}

#[component]
pub fn ContactsPage() -> Element {
    let state = use_app_state();
    let mut ui = use_main_ui();
    let data = use_session_data(contacts_changed, read_contacts);
    let mut tab = use_signal(ContactsTab::default);
    let collapsed = use_signal(HashSet::<String>::new);
    let mut expanded = use_signal(HashSet::<i64>::new);
    let mut items = use_signal(|| Rc::new(Vec::<Item>::new()));
    use_context_provider(|| ContactsContext {
        items,
        collapsed,
        expanded,
    });
    let list = use_virtual_list(VirtualListConfig {
        estimate: 56.0,
        ..Default::default()
    });

    use_effect(move || {
        let next = match tab() {
            ContactsTab::Friends => friend_items(&data.read(), &collapsed.read()),
            ContactsTab::Channels => channel_items(&data.read(), &expanded.read()),
        };
        items.set(Rc::new(next));
    });

    // 其它页面要求显示某个频道
    use_effect(move || {
        let requested = (ui.reveal_channel)();
        if let Some(stream) = requested {
            tab.set(ContactsTab::Channels);
            expanded.write().insert(stream);
            load_topics(state.session.peek().clone(), stream);
            ui.reveal_channel.set(None);
        }
    });

    let current = items.read().clone();
    let keys: Rc<Vec<u64>> = Rc::new(current.iter().map(Item::key).collect());
    let account = *state.account.read();
    let ContactsData {
        notices, reminders, ..
    } = data.read().clone();
    let tab_value = match tab() {
        ContactsTab::Friends => "friends",
        ContactsTab::Channels => "channels",
    };
    rsx! {
        div { class: "contacts-page",
            div { class: "contacts-entries",
                ListRow {
                    density: RowDensity::Compact,
                    leading: rsx! { Icon { name: IconName::UserSetting, size: 18.0 } },
                    title: "好友管理器",
                    onclick: move |_| {
                        if let Some(account) = account {
                            windows::open_friend_manager(account);
                        }
                    },
                }
                ListRow {
                    density: RowDensity::Compact,
                    leading: rsx! { Icon { name: IconName::Notification, size: 18.0 } },
                    title: "系统通知",
                    trailing: rsx! { Badge { dot: notices > 0 } },
                    onclick: move |_| {
                        if let Some(account) = account {
                            windows::open_system_notices(account);
                        }
                    },
                }
                ListRow {
                    density: RowDensity::Compact,
                    leading: rsx! { Icon { name: IconName::Time, size: 18.0 } },
                    title: "消息提醒",
                    trailing: rsx! { Badge { dot: reminders > 0 } },
                    onclick: move |_| {
                        if let Some(account) = account {
                            windows::open_reminders(account);
                        }
                    },
                }
            }
            div { class: "contacts-tabs",
                Tabs {
                    items: vec![TabItem::new("friends", "好友"), TabItem::new("channels", "频道")],
                    value: tab_value,
                    onchange: move |value: String| {
                        tab.set(if value == "channels" { ContactsTab::Channels } else { ContactsTab::Friends });
                    },
                }
            }
            div { class: "contacts-list",
                VirtualList {
                    handle: list,
                    keys,
                    render: move |i: usize| rsx! {
                        ContactRow { index: i }
                    },
                }
            }
        }
    }
}

fn load_topics(session: Option<Arc<Session>>, stream: i64) {
    let Some(session) = session else { return };
    if session.topics(stream).is_some() {
        return;
    }
    spawn(async move {
        if let Err(error) = session.load_topics(stream).await {
            log::warn!("加载话题失败：{error}");
        }
    });
}

#[component]
fn ContactRow(index: usize) -> Element {
    let context = use_context::<ContactsContext>();
    let items = context.items.read();
    let Some(item) = items.get(index).cloned() else {
        return rsx! {};
    };
    drop(items);
    match item {
        Item::FriendHeader(kind, title, count) => rsx! { FriendHeader { kind, title, count } },
        Item::Friend(friend, _) => rsx! { FriendRow { friend } },
        Item::PinnedHeader(count) => rsx! {
            div { class: "contacts-section",
                span { class: "contacts-section-title", "置顶话题" }
                span { class: "text-caption", "{count}" }
            }
        },
        Item::PinnedTopic(row) => {
            rsx! { TopicRow { key_value: row.key, title: row.title, indent: false } }
        }
        Item::ChannelHeader(section) => rsx! { ChannelHeader { section } },
        Item::Loading(_) => rsx! { div { class: "contacts-loading text-sub", "加载中…" } },
        Item::Topic(_, name, key) => {
            rsx! { TopicRow { key_value: key, title: name, indent: true } }
        }
    }
}

fn add_group(session: Arc<Session>, overlays: Overlays) {
    spawn_forever(async move {
        let prompt = TextPrompt::new("添加分组", "填写分组名称", "");
        if let Some(name) = overlays.prompt_text(prompt).await
            && let Err(error) = session.add_friend_group(&name).await
        {
            overlays.report_error("添加失败", error);
        }
    });
}

fn friend_section_menu(
    session: &Arc<Session>,
    overlays: Overlays,
    kind: FriendSectionKind,
    title: &str,
) -> Vec<MenuEntry> {
    let add = {
        let session = session.clone();
        MenuEntry::item("添加分组", move || add_group(session.clone(), overlays))
    };
    match kind {
        FriendSectionKind::All => vec![add],
        FriendSectionKind::Group(id) => {
            let rename = {
                let session = session.clone();
                let title = title.to_owned();
                MenuEntry::item("重命名分组", move || {
                    let session = session.clone();
                    let title = title.clone();
                    spawn_forever(async move {
                        let prompt = TextPrompt::new("重命名分组", "填写分组名称", title);
                        if let Some(name) = overlays.prompt_text(prompt).await {
                            overlays
                                .attempt(
                                    "重命名失败",
                                    session.rename(RenameTarget::FriendGroup(id), &name),
                                )
                                .await;
                        }
                    });
                })
            };
            let delete = {
                let session = session.clone();
                let target = format!("分组“{title}”");
                MenuEntry::item("删除分组", move || {
                    let session = session.clone();
                    let target = target.clone();
                    spawn_forever(async move {
                        overlays
                            .delete(
                                &target,
                                Some("删除后，该分组中的好友将移至我的好友"),
                                || session.delete(DeleteTarget::FriendGroup(id)),
                            )
                            .await;
                    });
                })
                .danger()
            };
            vec![add, rename, delete]
        }
        FriendSectionKind::Pinned | FriendSectionKind::Bots => Vec::new(),
    }
}

#[component]
fn FriendHeader(kind: FriendSectionKind, title: String, count: String) -> Element {
    let state = use_app_state();
    let overlays = use_overlays();
    let context = use_context::<ContactsContext>();
    let mut collapsed = context.collapsed;
    let id = format!("{kind:?}");
    let open = !collapsed.read().contains(&id);
    let menu_title = title.clone();
    rsx! {
        ContextMenuArea {
            items: Callback::new(move |()| {
                state.session.peek().as_ref().map_or_else(Vec::new, |s| {
                    friend_section_menu(s, overlays, kind, &menu_title)
                })
            }),
            div {
                class: "contacts-section clickable",
                onclick: move |_| {
                    let mut set = collapsed.write();
                    if !set.remove(&id) {
                        set.insert(id.clone());
                    }
                },
                span { class: if open { "contacts-chevron open" } else { "contacts-chevron" },
                    Icon { name: IconName::ChevronRight, size: 12.0 }
                }
                span { class: "contacts-section-title", "{title}" }
                span { class: "text-caption", "{count}" }
            }
        }
    }
}

#[component]
fn FriendRow(friend: FriendItem) -> Element {
    let state = use_app_state();
    let overlays = use_overlays();
    let mut ui = use_main_ui();
    let classic = use_local_setting(|s| s.classic_mode());
    let user_id = friend.user_id;
    let selected = *ui.right.read() == RightPane::User(user_id);
    rsx! {
        ListRow {
            density: RowDensity::Normal,
            leading: rsx! {
                RemoteAvatar {
                    text: friend.display_name.clone(),
                    url: friend.avatar_url.clone(),
                    online: friend.presence == PresenceState::Online,
                }
            },
            title: friend.name_with_original.clone(),
            subtitle: friend.status_text.clone(),
            selected,
            pinned: friend.pinned,
            onclick: move |e: MouseEvent| {
                if classic() {
                    let point = e.client_coordinates();
                    open_user_card(overlays, Rect::point(point.x, point.y), user_id);
                } else {
                    ui.right.set(RightPane::User(user_id));
                }
            },
            ondoubleclick: move |_| {
                let session = state.session.peek().clone();
                if let Some(session) = session {
                    windows::open_chat(
                        session.account_id(),
                        ConversationKey::dm([user_id], session.self_id()),
                        None,
                    );
                }
            },
        }
    }
}

#[component]
fn ChannelHeader(section: ChannelSection) -> Element {
    let state = use_app_state();
    let overlays = use_overlays();
    let context = use_context::<ContactsContext>();
    let mut expanded = context.expanded;
    let stream = section.stream_id;
    let open = expanded.read().contains(&stream);
    let class = if section.dimmed {
        "contacts-section clickable dimmed"
    } else {
        "contacts-section clickable"
    };
    rsx! {
        ContextMenuArea {
            items: Callback::new(move |()| {
                state.session.peek().as_ref().map_or_else(Vec::new, |s| {
                    channel_section_menu(s, overlays, stream)
                })
            }),
            div {
                class,
                onclick: move |_| {
                    let opening = !expanded.peek().contains(&stream);
                    if opening {
                        expanded.write().insert(stream);
                        load_topics(state.session.peek().clone(), stream);
                    } else {
                        expanded.write().remove(&stream);
                    }
                },
                span { class: if open { "contacts-chevron open" } else { "contacts-chevron" },
                    Icon { name: IconName::ChevronRight, size: 12.0 }
                }
                span { class: "contacts-color", style: "background:{section.color}" }
                if section.locked {
                    Icon { name: IconName::Lock, size: 12.0 }
                }
                span { class: "contacts-section-title", "{section.name_with_original}" }
            }
        }
    }
}

#[component]
fn TopicRow(key_value: ConversationKey, title: String, indent: bool) -> Element {
    let state = use_app_state();
    let overlays = use_overlays();
    let mut ui = use_main_ui();
    let classic = use_local_setting(|s| s.classic_mode());
    let selected = *ui.right.read() == RightPane::Topic(key_value.clone());
    let click_key = key_value.clone();
    let open_key = key_value.clone();
    let menu_key = key_value.clone();
    rsx! {
        div { class: if indent { "contacts-topic indent" } else { "contacts-topic" },
            ListRow {
                density: RowDensity::Compact,
                title,
                selected,
                onclick: move |e: MouseEvent| {
                    if classic() {
                        let point = e.client_coordinates();
                        crate::widgets::open_topic_card(overlays, Rect::point(point.x, point.y), click_key.clone());
                    } else {
                        ui.right.set(RightPane::Topic(click_key.clone()));
                    }
                },
                ondoubleclick: move |_| {
                    if let Some(account) = *state.account.peek() {
                        windows::open_chat(account, open_key.clone(), None);
                    }
                },
                menu: move |_| {
                    let Some(session) = state.session.peek().clone() else {
                        return Vec::new();
                    };
                    topic_menu(&session, overlays, &menu_key)
                },
            }
        }
    }
}

fn topic_menu(session: &Arc<Session>, overlays: Overlays, key: &ConversationKey) -> Vec<MenuEntry> {
    let account = session.account_id();
    let send = {
        let key = key.clone();
        MenuEntry::item("发消息", move || {
            windows::open_chat(account, key.clone(), None);
        })
    };
    let pin = {
        let session = session.clone();
        let key = key.clone();
        let item = PinItem::Conversation(key.clone());
        let pinned = session.is_pinned(PinScope::Contacts, &item);
        MenuEntry::item(if pinned { "取消置顶" } else { "置顶" }, move || {
            session.set_pinned(PinScope::Contacts, &item, !pinned);
        })
    };
    vec![
        send,
        MenuEntry::Separator,
        pin,
        level_menu_items(session, key),
        MenuEntry::Separator,
        topic_more_submenu(session, overlays, key, false),
    ]
}
