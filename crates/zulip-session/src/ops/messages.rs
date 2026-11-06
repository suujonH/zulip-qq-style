//! 聊天：时间线加载、定位、已读、发送、编辑、回应、星标、转发。

use std::sync::Arc;
use std::time::Duration;

use chrono::{Local, TimeZone};
use zulip_api::{
    ApiError, FlagsNarrowUpdate, MessageDto, MessageQuery, MessageUpdate, NarrowTerm,
    OutgoingMessage,
};
use zulip_core::ConversationKey;
use zulip_core::emoji::EmojiEntry;
use zulip_core::links;
use zulip_core::model::{ChatMessage, MentionKind, MessageRevision, MessageState, UploadedFile};
use zulip_core::text::{forward, mention};
use zulip_core::widget::{self, Widget};
use zulip_store::MessageVersionRepo;

use crate::error::{Result, SessionError};
use crate::event::SessionEvent;
use crate::hooks::{HookEvent, OutgoingKind};
use crate::session::Session;
use crate::state::messages::{ENOUGH_CACHED, PageKind};
use crate::state::unread::UnreadSnapshot;
use crate::state::{ScrollPosition, State};

const PAGE_SIZE: u32 = 50;
const AROUND_BEFORE: u32 = 20;
const AROUND_AFTER: u32 = 50;
const FLAG_BATCH: usize = 1000;
const NARROW_FLAG_BATCH: u32 = 5000;
const ECHO_FALLBACK: Duration = Duration::from_secs(10);
/// 订阅者或参与者超过这个数时，@全体成员 需要权限。
const WILDCARD_FREE_LIMIT: usize = 15;

/// 时间线快照。消息是共享的不可变数据，克隆很便宜。
#[derive(Clone, Debug, Default, PartialEq)]
pub struct TimelineSnapshot {
    pub messages: Vec<Arc<ChatMessage>>,
    pub found_oldest: bool,
    pub found_newest: bool,
    pub version: u64,
}

/// 打开会话时的信息：未读定位和滚动恢复。
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct OpenedConversation {
    pub unread: UnreadSnapshot,
    pub first_unread_id: Option<i64>,
    /// 提及我的未读，最新的在前（“@我”横幅逐条跳转）。
    pub mention_queue: Vec<(i64, MentionKind)>,
    pub scroll: Option<ScrollPosition>,
}

/// 一条消息上可以执行的操作。
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct MessageActions {
    pub can_edit: bool,
    pub can_delete: bool,
    pub can_show_edit_history: bool,
    pub can_show_read_receipts: bool,
}

/// 消息定位的结果：所在会话和消息 id，消息已加载到时间线。
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct LocatedMessage {
    pub key: ConversationKey,
    pub message_id: i64,
}

/// 小部件操作。
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum WidgetAction {
    Vote { option_key: String, vote: bool },
    AddPollOption { option: String },
    AddTodoTask { task: String, desc: String },
    StrikeTodoTask { task_key: String },
}

fn outgoing(key: &ConversationKey, topic: &str, content: &str, self_id: i64) -> OutgoingMessage {
    match key {
        ConversationKey::Topic(t) => OutgoingMessage {
            stream_id: Some(t.stream_id),
            topic: Some(topic.to_owned()),
            user_ids: Vec::new(),
            content: content.to_owned(),
            queue_id: None,
            local_id: None,
        },
        ConversationKey::Dm(d) => OutgoingMessage {
            stream_id: None,
            topic: None,
            user_ids: if d.is_self_dm() {
                vec![self_id]
            } else {
                d.other_user_ids().to_vec()
            },
            content: content.to_owned(),
            queue_id: None,
            local_id: None,
        },
    }
}

fn wildcard_notice(error: &ApiError) -> Option<(bool, &'static str)> {
    match error.code() {
        Some("STREAM_WILDCARD_MENTION_NOT_ALLOWED") => {
            Some((false, "没有权限使用 @全体成员，已去掉后发送"))
        }
        Some("TOPIC_WILDCARD_MENTION_NOT_ALLOWED") => {
            Some((true, "没有权限使用 @话题参与者，已去掉后发送"))
        }
        _ => None,
    }
}

impl Session {
    pub fn timeline(&self, key: &ConversationKey) -> TimelineSnapshot {
        let state = self.state.read();
        state
            .messages
            .timeline(key)
            .map(|t| TimelineSnapshot {
                messages: t
                    .messages
                    .iter()
                    .filter(|m| !state.messages.is_hidden(m))
                    .cloned()
                    .collect(),
                found_oldest: t.found_oldest,
                found_newest: t.found_newest,
                version: t.version,
            })
            .unwrap_or_default()
    }

    pub fn message(&self, id: i64) -> Option<Arc<ChatMessage>> {
        self.state.read().messages.get(id).cloned()
    }

    /// 窗口打开会话：放入列表、不被缓存淘汰，返回未读定位信息。多个窗口可以同时打开同一会话。
    pub fn open_conversation(&self, key: &ConversationKey) -> OpenedConversation {
        let mut state = self.state.write();
        state.set_open(key, true);
        let title = state.topic_name(key);
        let listed = state.conversations.ensure_in_list(key, &title);
        let unread = state.unread.snapshot(key);
        let scroll = state.view.scroll(key);
        drop(state);
        if listed {
            self.emit(SessionEvent::ConversationsChanged);
        }
        self.set_current_conversation(key);
        OpenedConversation {
            first_unread_id: unread.ids.first().copied(),
            mention_queue: unread.mentions.iter().rev().copied().collect(),
            unread,
            scroll,
        }
    }

    pub fn close_conversation(&self, key: &ConversationKey) {
        let mut state = self.state.write();
        state.set_open(key, false);
        let closed = !state.messages.is_open(key);
        drop(state);
        let mut current = self.current.lock();
        if closed && current.as_ref() == Some(key) {
            *current = None;
        }
    }

    /// 用户最后切换到的会话（打开，或它所在的窗口回到前台）；已关闭时为 `None`。
    pub fn current_conversation(&self) -> Option<ConversationKey> {
        self.current.lock().clone()
    }

    fn set_current_conversation(&self, key: &ConversationKey) {
        let mut current = self.current.lock();
        if current.as_ref() != Some(key) {
            *current = Some(key.clone());
            drop(current);
            self.notify_hooks(HookEvent::ConversationOpened(key.clone()));
        }
    }

    /// 界面报告：这个会话在前台窗口中并且看着最新消息。此时新消息自动已读、不提醒。
    pub fn set_actively_viewing(&self, key: &ConversationKey, active: bool) {
        self.state.write().view.set_actively_viewing(key, active);
        if active {
            self.set_current_conversation(key);
        }
    }

    pub fn save_scroll_position(&self, key: &ConversationKey, position: Option<ScrollPosition>) {
        self.state.write().view.save_scroll(key, position);
    }

    async fn load_page(
        &self,
        key: &ConversationKey,
        anchor: String,
        before: u32,
        after: u32,
        include_anchor: bool,
        kind: PageKind,
    ) -> Result<()> {
        let narrow = self.narrow_of(key);
        let mut query = MessageQuery::new(narrow, anchor, before, after);
        query.include_anchor = include_anchor;
        let result = self.client.get_messages(&query).await?;
        {
            let mut state = self.state.write();
            let page: Vec<ChatMessage> = result
                .messages
                .iter()
                .map(|dto| state.prepare_message(dto))
                .collect();
            state
                .messages
                .merge_page(key, page, kind, result.found_oldest, result.found_newest);
        }
        self.emit(SessionEvent::TimelineChanged(key.clone()));
        Ok(())
    }

    /// 加载最新一页。缓存已足够时不发请求；同一会话的并发调用只请求一次。
    pub async fn load_newest(&self, key: &ConversationKey) -> Result<()> {
        let gate = self.gate(format!("timeline:{key}"));
        let _guard = gate.lock().await;
        let enough = self
            .state
            .read()
            .messages
            .timeline(key)
            .is_some_and(|t| t.has_enough_cached());
        if enough {
            return Ok(());
        }
        self.load_page(
            key,
            "newest".to_owned(),
            PAGE_SIZE.max(ENOUGH_CACHED as u32),
            0,
            true,
            PageKind::Newest,
        )
        .await
    }

    /// 向上翻页。已到最早时什么也不做。
    pub async fn load_older(&self, key: &ConversationKey) -> Result<()> {
        let gate = self.gate(format!("timeline:{key}"));
        let _guard = gate.lock().await;
        let anchor = {
            let state = self.state.read();
            let Some(timeline) = state.messages.timeline(key) else {
                return Ok(());
            };
            if timeline.found_oldest {
                return Ok(());
            }
            timeline.messages.iter().map(|m| m.id).find(|id| *id > 0)
        };
        let Some(anchor) = anchor else {
            return Ok(());
        };
        self.load_page(
            key,
            anchor.to_string(),
            PAGE_SIZE,
            0,
            false,
            PageKind::Older,
        )
        .await
    }

    /// 向下翻页。已到最新时什么也不做。
    pub async fn load_newer(&self, key: &ConversationKey) -> Result<()> {
        let gate = self.gate(format!("timeline:{key}"));
        let _guard = gate.lock().await;
        let anchor = {
            let state = self.state.read();
            let Some(timeline) = state.messages.timeline(key) else {
                return Ok(());
            };
            if timeline.found_newest {
                return Ok(());
            }
            timeline
                .messages
                .iter()
                .rev()
                .map(|m| m.id)
                .find(|id| *id > 0)
        };
        let Some(anchor) = anchor else {
            return Ok(());
        };
        self.load_page(
            key,
            anchor.to_string(),
            0,
            PAGE_SIZE,
            false,
            PageKind::Newer,
        )
        .await
    }

    /// 定位到消息：搜索、收藏、提醒、链接、未读和 @ 跳转都走这里。
    /// `key` 未知时先查询消息所在的会话。消息已在窗口中时不发分页请求。
    pub async fn locate_message(
        &self,
        key: Option<&ConversationKey>,
        message_id: i64,
    ) -> Result<LocatedMessage> {
        let key = match key {
            Some(key) => key.clone(),
            None => match self.message(message_id) {
                Some(message) => message.key.clone(),
                None => {
                    let dto = self.client.get_message(message_id, true).await?;
                    self.state.read().prepare_message(&dto).key
                }
            },
        };
        let cached = self.message(message_id).is_some_and(|m| m.key == key);
        if !cached {
            let gate = self.gate(format!("timeline:{key}"));
            let _guard = gate.lock().await;
            self.load_page(
                &key,
                message_id.to_string(),
                AROUND_BEFORE,
                AROUND_AFTER,
                true,
                PageKind::Around,
            )
            .await?;
        }
        Ok(LocatedMessage { key, message_id })
    }

    /// 打开本组织的消息链接；不是消息链接时为 `Ok(None)`。
    pub async fn locate_link(&self, url: &str) -> Result<Option<LocatedMessage>> {
        let realm = self.client.realm_url().to_string();
        let self_id = self.state.read().users.self_id;
        let Some(link) = links::parse(url, realm.trim_end_matches('/')) else {
            return Ok(None);
        };
        let Some(id) = link.near_message_id else {
            return Ok(None);
        };
        let key = match (link.stream_id, link.topic, link.dm_user_ids) {
            (Some(stream), Some(topic), _) => Some(ConversationKey::topic(stream, topic)),
            (_, _, Some(ids)) => Some(ConversationKey::dm(ids, self_id)),
            _ => None,
        };
        self.locate_message(key.as_ref(), id).await.map(Some)
    }

    /// 标记已读。先改本地，再分批发送。
    pub async fn mark_read(self: &Arc<Self>, ids: &[i64]) -> Result<()> {
        let ids: Vec<i64> = ids.iter().copied().filter(|id| *id > 0).collect();
        if ids.is_empty() {
            return Ok(());
        }
        let keys = self.state.write().unread.remove_ids(&ids);
        if !keys.is_empty() {
            self.emit(SessionEvent::UnreadChanged(keys));
        }
        for batch in ids.chunks(FLAG_BATCH) {
            self.client.update_flags(batch, "add", "read").await?;
        }
        Ok(())
    }

    /// 整个会话标记已读。不依赖本地缓存的未读。
    pub async fn mark_conversation_read(self: &Arc<Self>, key: &ConversationKey) -> Result<()> {
        let keys = {
            let mut state = self.state.write();
            let ids = state.unread.ids(key);
            state.unread.remove_ids(&ids)
        };
        if !keys.is_empty() {
            self.emit(SessionEvent::UnreadChanged(keys));
        }
        let mut narrow = self.narrow_of(key);
        narrow.push(NarrowTerm::is("unread"));
        let mut anchor = "oldest".to_owned();
        let mut include_anchor = false;
        loop {
            let result = self
                .client
                .update_flags_for_narrow(&FlagsNarrowUpdate {
                    narrow: narrow.clone(),
                    anchor: anchor.clone(),
                    include_anchor,
                    num_before: 0,
                    num_after: NARROW_FLAG_BATCH,
                    op: "add".to_owned(),
                    flag: "read".to_owned(),
                })
                .await?;
            if result.found_newest {
                return Ok(());
            }
            let Some(last) = result.last_processed_id else {
                return Ok(());
            };
            anchor = last.to_string();
            include_anchor = false;
        }
    }

    /// 从这条消息开始的全部消息标记为未读。
    pub async fn mark_unread_from(&self, key: &ConversationKey, message_id: i64) -> Result<()> {
        let narrow = self.narrow_of(key);
        let mut anchor = message_id.to_string();
        let mut include_anchor = true;
        loop {
            let result = self
                .client
                .update_flags_for_narrow(&FlagsNarrowUpdate {
                    narrow: narrow.clone(),
                    anchor: anchor.clone(),
                    include_anchor,
                    num_before: 0,
                    num_after: NARROW_FLAG_BATCH,
                    op: "remove".to_owned(),
                    flag: "read".to_owned(),
                })
                .await?;
            if result.found_newest {
                return Ok(());
            }
            let Some(last) = result.last_processed_id else {
                return Ok(());
            };
            anchor = last.to_string();
            include_anchor = false;
        }
    }

    /// 发送消息：立即出现本地回显（`MessageAdded`，负 id），返回服务器的消息 id。
    /// 失败时回显标记为失败并返回错误，可以用 [`retry_send`](Self::retry_send) 重发。
    pub async fn send_message(
        self: &Arc<Self>,
        key: &ConversationKey,
        content: &str,
    ) -> Result<i64> {
        self.send_as(key, content, OutgoingKind::Text).await
    }

    /// 所有发送的统一出口，先经过钩子的发送前过滤（拦截时错误内容可以直接显示给用户）。
    /// 其余同 [`send_message`](Self::send_message)。
    pub async fn send_as(
        self: &Arc<Self>,
        key: &ConversationKey,
        content: &str,
        kind: OutgoingKind,
    ) -> Result<i64> {
        let filtered = match &self.hooks {
            Some(hooks) => Some(
                hooks
                    .filter_outgoing(self, key, content, kind)
                    .await
                    .map_err(SessionError::invalid)?,
            ),
            None => None,
        };
        let content = filtered.as_deref().unwrap_or(content).trim_end();
        if content.trim().is_empty() {
            return Err(SessionError::invalid("不能发送空消息"));
        }
        let (id, local_id) = {
            let mut state = self.state.write();
            let max = state.realm.max_message_length;
            if max > 0 && content.chars().count() > max as usize {
                return Err(SessionError::invalid("消息过长"));
            }
            let self_id = state.users.self_id;
            let mut message =
                ChatMessage::new(0, key.clone(), self_id, chrono::Utc::now().timestamp());
            if let Some(me) = state.users.get(self_id) {
                message.sender_name.clone_from(&me.full_name);
                message.sender_email.clone_from(&me.email);
                message.avatar_url.clone_from(&me.avatar_url);
            }
            if let ConversationKey::Topic(t) = key {
                message.stream_id = Some(t.stream_id);
                message.topic = state.topic_name(key);
            }
            state.messages.add_pending(message, content)
        };
        self.emit(SessionEvent::MessageAdded {
            key: key.clone(),
            id,
        });
        self.post_pending(&local_id).await
    }

    /// 重发失败的消息。
    pub async fn retry_send(self: &Arc<Self>, local_id: &str) -> Result<i64> {
        let changed = {
            let mut state = self.state.write();
            let failed = state
                .messages
                .find_pending(local_id)
                .is_some_and(|m| matches!(m.state, MessageState::Failed(_)));
            if !failed {
                return Err(SessionError::invalid("这条消息已不能重发"));
            }
            state.messages.set_pending_failed(local_id, false)
        };
        if let Some((key, id)) = changed {
            self.emit(SessionEvent::MessageUpdated { key, id });
        }
        self.post_pending(local_id).await
    }

    /// 放弃发送失败的消息。
    pub fn discard_failed(&self, local_id: &str) {
        let removed = {
            let mut state = self.state.write();
            let failed = state
                .messages
                .find_pending(local_id)
                .is_some_and(|m| matches!(m.state, MessageState::Failed(_)));
            if failed {
                state.messages.remove_pending(local_id)
            } else {
                None
            }
        };
        if let Some((key, id)) = removed {
            self.emit(SessionEvent::MessageRemoved { key, id });
        }
    }

    async fn post_pending(self: &Arc<Self>, local_id: &str) -> Result<i64> {
        let (key, content, mut message) = {
            let state = self.state.read();
            let Some(pending) = state.messages.find_pending(local_id) else {
                return Err(SessionError::invalid("这条消息已不能重发"));
            };
            let Some(echo) = pending.local_echo() else {
                return Err(SessionError::invalid("这条消息已不能重发"));
            };
            let message = outgoing(
                &pending.key,
                &pending.topic,
                &echo.raw_content,
                state.users.self_id,
            );
            (pending.key.clone(), echo.raw_content.clone(), message)
        };
        let queue_id = self.queue.lock().queue_id.clone();
        message.queue_id = Some(queue_id);
        message.local_id = Some(local_id.to_owned());
        let mut result = self.client.send_message(&message).await;
        if let Err(error) = &result
            && let Some((topic_only, notice)) = wildcard_notice(error)
        {
            message.content = mention::remove_wildcards(&content, topic_only);
            result = self.client.send_message(&message).await;
            if result.is_ok() {
                self.emit(SessionEvent::Toast(notice.to_owned()));
            }
        }
        match result {
            Ok(sent) => {
                self.schedule_echo_fallback(local_id.to_owned(), sent.id);
                if self.draft(&key).is_some()
                    && let Err(error) = self.save_draft(&key, "").await
                {
                    log::warn!("删除已发送的草稿失败：{error}");
                }
                Ok(sent.id)
            }
            Err(error) => {
                let changed = self
                    .state
                    .write()
                    .messages
                    .set_pending_failed(local_id, true);
                if let Some((key, id)) = changed {
                    self.emit(SessionEvent::MessageUpdated { key, id });
                }
                Err(error.into())
            }
        }
    }

    /// 事件队列可能在发送后失效，回显丢失：过一会儿还没有回显就主动取。
    fn schedule_echo_fallback(self: &Arc<Self>, local_id: String, message_id: i64) {
        let this = self.clone();
        tokio::spawn(async move {
            tokio::select! {
                () = this.cancel.cancelled() => return,
                () = tokio::time::sleep(ECHO_FALLBACK) => {}
            }
            if this.state.read().messages.find_pending(&local_id).is_none() {
                return;
            }
            match this.client.get_message(message_id, true).await {
                Ok(dto) => this.accept_fetched(&dto, &local_id),
                Err(error) => log::warn!("取回显消息失败：{error}"),
            }
        });
    }

    pub async fn schedule_message(
        &self,
        key: &ConversationKey,
        content: &str,
        deliver_at: i64,
    ) -> Result<i64> {
        let message = {
            let state = self.state.read();
            outgoing(key, &state.topic_name(key), content, state.users.self_id)
        };
        Ok(self
            .client
            .create_scheduled_message(&message, deliver_at)
            .await?)
    }

    pub async fn create_reminder(&self, message_id: i64, deliver_at: i64) -> Result<i64> {
        Ok(self.client.create_reminder(message_id, deliver_at).await?)
    }

    /// 消息的 Markdown 原文。
    pub async fn raw_content(&self, message_id: i64) -> Result<String> {
        Ok(self.client.get_message(message_id, false).await?.content)
    }

    pub async fn render_preview(&self, content: &str) -> Result<String> {
        Ok(self.client.render_message(content).await?)
    }

    pub async fn edit_message(&self, message_id: i64, content: &str) -> Result<()> {
        let update = MessageUpdate {
            content: Some(content.to_owned()),
            ..MessageUpdate::default()
        };
        self.client.update_message(message_id, &update).await?;
        Ok(())
    }

    pub async fn message_history(&self, message_id: i64) -> Result<Vec<MessageRevision>> {
        Ok(self.client.get_message_history(message_id).await?)
    }

    /// 本机保存的编辑前的各个版本（服务器渲染的 HTML），从最早到最近。消息已被删除时也能取到。
    pub async fn previous_contents(&self, message_id: i64) -> Result<Vec<String>> {
        let account_id = self.account_id;
        Ok(self
            .db
            .call(move |db| MessageVersionRepo::new(db, account_id).load(message_id))
            .await?)
    }

    pub async fn read_receipts(&self, message_id: i64) -> Result<Vec<i64>> {
        Ok(self.client.get_read_receipts(message_id).await?)
    }

    /// 一对一私聊中对方是否已读我的最后一条消息。不适用时为 `None`。
    pub async fn peer_has_read(&self, key: &ConversationKey) -> Result<Option<bool>> {
        let ConversationKey::Dm(d) = key else {
            return Ok(None);
        };
        if d.is_group_dm() || d.is_self_dm() {
            return Ok(None);
        }
        let last_own = {
            let state = self.state.read();
            if !state.realm.enable_read_receipts {
                return Ok(None);
            }
            let self_id = state.users.self_id;
            state.messages.timeline(key).and_then(|t| {
                t.messages
                    .iter()
                    .rev()
                    .find(|m| m.id > 0 && m.sender_id == self_id)
                    .map(|m| m.id)
            })
        };
        let Some(id) = last_own else {
            return Ok(None);
        };
        let readers = self.client.get_read_receipts(id).await?;
        Ok(Some(d.other_user_ids().iter().any(|u| readers.contains(u))))
    }

    /// 切换自己的表情回应。结果由事件更新，不乐观修改。
    pub async fn toggle_reaction(&self, message_id: i64, emoji: &EmojiEntry) -> Result<()> {
        let kind = emoji.kind.reaction_type();
        let mine = {
            let state = self.state.read();
            let self_id = state.users.self_id;
            state.messages.get(message_id).is_some_and(|m| {
                m.reactions.iter().any(|r| {
                    r.user_id == self_id && r.emoji_code == emoji.code && r.reaction_type == kind
                })
            })
        };
        if mine {
            self.client
                .remove_reaction(message_id, &emoji.name, &emoji.code, kind)
                .await?;
        } else {
            self.client
                .add_reaction(message_id, &emoji.name, &emoji.code, kind)
                .await?;
            self.add_recent_emoji(&emoji.name);
        }
        Ok(())
    }

    pub async fn set_starred(&self, ids: &[i64], starred: bool) -> Result<()> {
        let op = if starred { "add" } else { "remove" };
        for batch in ids.chunks(FLAG_BATCH) {
            self.client.update_flags(batch, op, "starred").await?;
        }
        Ok(())
    }

    /// 投票、待办等小部件的操作。
    pub async fn widget_action(&self, message_id: i64, action: WidgetAction) -> Result<()> {
        let content = {
            let state = self.state.read();
            let self_id = state.users.self_id;
            let message = state
                .messages
                .get(message_id)
                .ok_or_else(|| SessionError::invalid("消息不存在"))?;
            let next = widget::next_own_index(&message.submessages, self_id);
            match (
                &action,
                widget::parse(&message.submessages, message.sender_id),
            ) {
                (WidgetAction::Vote { option_key, vote }, _) => {
                    widget::vote_event(option_key, *vote)
                }
                (WidgetAction::AddPollOption { option }, Some(Widget::Poll(poll))) => {
                    if poll.options.iter().any(|o| &o.text == option) {
                        return Err(SessionError::invalid("选项已存在"));
                    }
                    widget::new_option_event(next, option)
                }
                (WidgetAction::AddTodoTask { task, desc }, Some(Widget::Todo(todo))) => {
                    if todo.tasks.iter().any(|t| &t.text == task) {
                        return Err(SessionError::invalid("任务已存在"));
                    }
                    widget::new_task_event(next, task, desc)
                }
                (WidgetAction::StrikeTodoTask { task_key }, _) => widget::strike_event(task_key),
                _ => return Err(SessionError::invalid("这条消息不支持这个操作")),
            }
        };
        self.client
            .send_submessage(message_id, "widget", &content)
            .await?;
        Ok(())
    }

    /// 合并转发到多个会话：多条消息拼成“聊天记录”，超长时拆成多条。
    pub async fn forward_merged(
        self: &Arc<Self>,
        ids: &[i64],
        targets: &[ConversationKey],
    ) -> Result<()> {
        let mut items = Vec::new();
        let mut max_chars = None;
        for id in ids {
            let raw = self.raw_content(*id).await?;
            let state = self.state.read();
            let max = state.realm.max_message_length;
            max_chars = (max > 0).then_some(max as usize);
            let Some(message) = state.messages.get(*id) else {
                continue;
            };
            let time = Local
                .timestamp_opt(message.timestamp, 0)
                .single()
                .map(|t| t.naive_local())
                .unwrap_or_default();
            items.push(forward::ForwardedMessage {
                sender_name: state.display_name(message.sender_id),
                time,
                raw,
            });
        }
        for text in forward::merge(&items, max_chars) {
            for target in targets {
                self.send_as(target, &text, OutgoingKind::Forward).await?;
            }
        }
        Ok(())
    }

    /// 逐条转发到多个会话。
    pub async fn forward_each(
        self: &Arc<Self>,
        ids: &[i64],
        targets: &[ConversationKey],
    ) -> Result<()> {
        for id in ids {
            let raw = self.raw_content(*id).await?;
            for target in targets {
                self.send_as(target, &raw, OutgoingKind::Forward).await?;
            }
        }
        Ok(())
    }

    /// 消息链接。
    pub fn message_link(&self, key: &ConversationKey, message_id: i64) -> String {
        let state = self.state.read();
        let realm = state.realm.url.clone();
        let (channel, topic) = match key {
            ConversationKey::Topic(t) => (
                state
                    .channels
                    .get(t.stream_id)
                    .map(|c| c.name.clone())
                    .unwrap_or_default(),
                state.topic_name(key),
            ),
            _ => (String::new(), String::new()),
        };
        links::message_link(
            &realm,
            key,
            state.users.self_id,
            message_id,
            &channel,
            &topic,
        )
    }

    /// 回复引用的 Markdown。
    pub async fn quote_message(&self, message_id: i64) -> Result<String> {
        let raw = self.raw_content(message_id).await?;
        let message = self
            .message(message_id)
            .ok_or_else(|| SessionError::invalid("消息不存在"))?;
        let link = self.message_link(&message.key, message_id);
        Ok(crate::compose::quote_markdown(
            &message.sender_name,
            message.sender_id,
            &link,
            &raw,
        ))
    }

    pub fn message_actions(&self, message_id: i64) -> MessageActions {
        let state = self.state.read();
        let Some(m) = state.messages.get(message_id) else {
            return MessageActions::default();
        };
        actions_of(&state, m, chrono::Utc::now().timestamp())
    }

    /// 这个会话中是否可以 @全体成员。人少或私聊总是可以。
    pub fn is_wildcard_allowed(&self, key: &ConversationKey) -> bool {
        let state = self.state.read();
        let ConversationKey::Topic(t) = key else {
            return true;
        };
        let count = state
            .channels
            .get(t.stream_id)
            .map_or(0, |c| c.subscriber_count.max(0) as usize);
        count <= WILDCARD_FREE_LIMIT || state.permissions.can_mention_everyone()
    }

    /// 聊天标题：话题带订阅人数，群聊带总人数。
    pub fn chat_title(&self, key: &ConversationKey) -> String {
        let state = self.state.read();
        let title = state.display_title(key, false);
        match key {
            ConversationKey::Topic(t) => match state.channels.get(t.stream_id) {
                Some(c) if c.subscriber_count > 0 => format!("{title} ({})", c.subscriber_count),
                _ => title,
            },
            ConversationKey::Dm(d) if d.is_group_dm() => {
                format!("{title} ({})", d.other_user_ids().len() + 1)
            }
            _ => title,
        }
    }

    /// 上传附件。`progress` 收到已发送的字节数。
    pub async fn upload_file(
        &self,
        path: &std::path::Path,
        progress: impl Fn(u64) + Send + Sync + 'static,
    ) -> Result<UploadedFile> {
        let file = tokio::fs::File::open(path).await?;
        let len = file.metadata().await?.len();
        let name = path
            .file_name()
            .and_then(|n| n.to_str())
            .unwrap_or("file")
            .to_owned();
        let content_type = mime_guess::from_path(path)
            .first_or_octet_stream()
            .essence_str()
            .to_owned();
        Ok(self
            .client
            .upload_file(file, len, &name, &content_type, progress)
            .await?)
    }

    /// 更换自己的头像（PNG 字节，来自 `AvatarImage::render_png`）。
    pub async fn upload_avatar(&self, png: Vec<u8>) -> Result<()> {
        self.client
            .upload_avatar(png, "avatar.png", "image/png")
            .await?;
        Ok(())
    }

    /// 删除一条消息。统一入口见 `Session::delete`。
    pub(crate) async fn delete_message(&self, message_id: i64) -> Result<()> {
        self.client.delete_message(message_id).await?;
        Ok(())
    }

    pub fn typing_text(&self, key: &ConversationKey) -> Option<String> {
        let state = self.state.read();
        let names: Vec<String> = state
            .typing
            .users(key)
            .into_iter()
            .map(|id| state.display_name(id))
            .collect();
        crate::state::misc::typing_text(key, &names)
    }

    /// 输入框内容变化时调用：按服务器要求的频率发送“正在输入”，停止输入后自动发送停止。
    pub fn on_compose_input(self: &Arc<Self>, key: &ConversationKey, has_text: bool) {
        if !has_text {
            self.stop_typing();
            return;
        }
        let (enabled, wait, stopped) = {
            let state = self.state.read();
            let enabled = match key {
                ConversationKey::Topic(_) => state.settings.send_stream_typing_notifications,
                ConversationKey::Dm(d) => {
                    !d.is_self_dm() && state.settings.send_private_typing_notifications
                }
            };
            (
                enabled,
                Duration::from_millis(state.realm.typing_started_wait_ms.max(0) as u64),
                Duration::from_millis(state.realm.typing_stopped_wait_ms.max(1000) as u64),
            )
        };
        if !enabled {
            return;
        }
        let (send_start, stop_previous, generation) = {
            let mut sender = self.typing_sender.lock();
            sender.generation += 1;
            let now = std::time::Instant::now();
            let (send_start, stop_previous) = match &sender.current {
                Some((current, at)) if current == key => (now.duration_since(*at) >= wait, None),
                Some((current, _)) => (true, Some(current.clone())),
                None => (true, None),
            };
            if send_start {
                sender.current = Some((key.clone(), now));
            }
            (send_start, stop_previous, sender.generation)
        };
        if let Some(previous) = stop_previous {
            self.spawn_typing("stop", previous);
        }
        if send_start {
            self.spawn_typing("start", key.clone());
        }
        let this = self.clone();
        tokio::spawn(async move {
            tokio::time::sleep(stopped).await;
            if this.typing_sender.lock().generation == generation {
                this.stop_typing();
            }
        });
    }

    /// 发送或清空输入框后调用。
    pub fn stop_typing(self: &Arc<Self>) {
        let current = {
            let mut sender = self.typing_sender.lock();
            sender.generation += 1;
            sender.current.take()
        };
        if let Some((key, _)) = current {
            self.spawn_typing("stop", key);
        }
    }

    fn spawn_typing(self: &Arc<Self>, op: &'static str, key: ConversationKey) {
        let this = self.clone();
        tokio::spawn(async move {
            let (stream, topic, users) = {
                let state = this.state.read();
                match &key {
                    ConversationKey::Topic(t) => {
                        (Some(t.stream_id), Some(state.topic_name(&key)), Vec::new())
                    }
                    ConversationKey::Dm(d) => (None, None, d.other_user_ids().to_vec()),
                }
            };
            if let Err(error) = this
                .client
                .send_typing(op, stream, topic.as_deref(), &users)
                .await
            {
                log::info!("发送输入状态失败：{error}");
            }
        });
    }

    /// 一页消息 DTO 转为领域模型，不写入缓存。
    pub(crate) fn prepare_all(&self, dtos: &[MessageDto]) -> Vec<ChatMessage> {
        let state = self.state.read();
        dtos.iter().map(|dto| state.prepare_message(dto)).collect()
    }
}

pub(crate) fn actions_of(state: &State, m: &ChatMessage, now: i64) -> MessageActions {
    if m.id <= 0 || m.is_deleted() {
        return MessageActions::default();
    }
    let self_id = state.users.self_id;
    let has_widget = !m.submessages.is_empty();
    let channel = m.stream_id.and_then(|id| state.channels.get(id));
    let my_bot = state
        .users
        .get(m.sender_id)
        .is_some_and(|u| u.is_bot && u.bot_owner_id == Some(self_id));
    MessageActions {
        can_edit: !has_widget
            && state
                .permissions
                .can_edit_message(m.sender_id, m.timestamp, now),
        can_delete: state.permissions.can_delete_message(
            channel.map(|c| &c.permissions),
            channel.is_some_and(|c| c.is_archived),
            m.sender_id == self_id || my_bot,
            m.timestamp,
            now,
        ),
        can_show_edit_history: m.last_edit_timestamp.is_some()
            && state.realm.edit_history_visibility_policy != "none",
        can_show_read_receipts: state.realm.enable_read_receipts,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::state::test_support::state;

    #[test]
    fn outgoing_targets() {
        let topic = outgoing(&ConversationKey::topic(3, "t"), "T", "x", 1);
        assert_eq!(
            (topic.stream_id, topic.topic.as_deref()),
            (Some(3), Some("T"))
        );
        let own = outgoing(&ConversationKey::dm([1], 1), "", "x", 1);
        assert_eq!(own.user_ids, [1]);
        let dm = outgoing(&ConversationKey::dm([2, 3], 1), "", "x", 1);
        assert_eq!(dm.user_ids, [2, 3]);
    }

    #[test]
    fn actions_follow_ownership_and_widgets() {
        let s = state();
        let key = ConversationKey::topic(10, "t");
        let mut own = ChatMessage::new(5, key.clone(), 1, 100);
        own.stream_id = Some(10);
        let actions = actions_of(&s, &own, 100);
        assert!(actions.can_edit);
        own.submessages
            .push(zulip_core::model::Submessage::default());
        assert!(!actions_of(&s, &own, 100).can_edit);

        let other = ChatMessage::new(6, key.clone(), 2, 100);
        assert!(!actions_of(&s, &other, 100).can_edit);

        let pending = ChatMessage::new(-1, key.clone(), 1, 100);
        assert_eq!(actions_of(&s, &pending, 100), MessageActions::default());

        let mut edited = ChatMessage::new(7, key, 2, 100);
        edited.last_edit_timestamp = Some(101);
        assert!(actions_of(&s, &edited, 100).can_show_edit_history);
    }
}
