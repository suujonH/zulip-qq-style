//! 示例插件。私聊收到 ping 时回复设置中的内容；含 echo:block 的消息被拦截，
//! 发送时 echo:rewrite 后面的内容替换整条消息，echo:slow 演示回调超时。
//! 通知型事件写日志；右键菜单设置状态栏提示；工具栏按钮替换输入框内容，输入框内容为
//! `echo:call 操作 JSON参数` 时调用对应的宿主函数并把结果写入日志（见 [`call`]）。

use std::sync::Mutex;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::Duration;

use zulip_plugin_sdk::abi::{LOG_INFO, LOG_WARNING};
use zulip_plugin_sdk::serde_json::{Value, json};
use zulip_plugin_sdk::{
    ChannelsChanged, ComposerClick, ConnectionChanged, ContextMenuClick, ContextTarget,
    Conversation, ConversationOpened, Event, Message, MessageEdited, MessagesDeleted, Outgoing,
    OutgoingKind, PanelRequest, Plugin, PresenceChanged, ReactionChanged, ReadStateChanged, Result,
    SettingsButton, TopicMoved, UserChanged, add_reaction, all_channels, api_request,
    conversations, current_conversation, delete_topic, download_attachment, edit_history,
    export_plugin, fill_composer, log, mark_unread, message, move_topic, open_conversation,
    presence, remove_reaction, reply_panel, schedule_message, search_messages, send_direct_message,
    set_annotation, set_outgoing_content, set_status_text, set_topic_resolved, settings,
    settings_text, show_notification, show_toast, str_of, topic_visibility, user_groups,
};

const BLOCK: &str = "echo:block";
const REWRITE: &str = "echo:rewrite ";
const SLOW: &str = "echo:slow";
const SLOW_FOR: Duration = Duration::from_millis(2500);
const CALL: &str = "echo:call ";

#[derive(Default)]
struct Echo {
    enabled: AtomicBool,
    // 面板中的称呼：用户 id 与内容，只保存在内存中。
    nicknames: Mutex<Vec<(i64, String)>>,
}

impl Echo {
    fn nickname(&self, user_id: i64) -> String {
        let nicknames = self.nicknames.lock().unwrap_or_else(|e| e.into_inner());
        nicknames
            .iter()
            .find(|(id, _)| *id == user_id)
            .map_or_else(String::new, |(_, text)| text.clone())
    }

    fn set_nickname(&self, user_id: i64, text: &str) {
        let mut nicknames = self.nicknames.lock().unwrap_or_else(|e| e.into_inner());
        nicknames.retain(|(id, _)| *id != user_id);
        if !text.is_empty() {
            nicknames.push((user_id, text.to_owned()));
        }
    }

    fn panel_fields(&self, user_id: i64, info: &str) -> Value {
        json!({"fields": [{
            "key": "nickname", "label": "称呼", "type": "select", "value": self.nickname(user_id),
            "options": [
                {"value": "", "label": "无"},
                {"value": "friend", "label": "朋友"},
                {"value": "colleague", "label": "同事"},
            ],
        },
        {"key": "note", "label": "备注", "type": "text", "value": "", "placeholder": "写点什么"},
        {"key": "ping", "label": "打招呼", "type": "button"},
        {"key": "info", "label": "说明", "type": "info", "value": info}]})
    }
}

fn int(args: &Value, key: &str) -> i64 {
    args[key].as_i64().unwrap_or_default()
}

/// 调用一个宿主函数，返回写入日志的结果。
fn call(op: &str, args: &Value) -> Result<String> {
    let id = int(args, "id");
    let stream = int(args, "stream");
    let topic = str_of(args, "topic");
    let conv = || {
        Conversation::from_json(&args["conv"]).unwrap_or(Conversation::Direct {
            user_ids: Vec::new(),
        })
    };
    Ok(match op {
        "message" => {
            let m = message(id)?;
            format!(
                "回应 {} 上传 {} 编辑 {}",
                m.reactions.len(),
                m.uploads.first().map_or("", |u| u.file_name.as_str()),
                m.last_edit_timestamp.is_some()
            )
        }
        "search" => {
            let page = search_messages(args)?;
            format!("{} 条，下一页 {:?}", page.messages.len(), page.next_cursor)
        }
        "conversations" => conversations()?.to_string(),
        "current" => current_conversation()?.map_or("无".to_owned(), |c| c.to_json().to_string()),
        "presence" => presence(id)?.to_string(),
        "groups" => user_groups()?.to_string(),
        "all_channels" => {
            let channels = all_channels()?;
            let unsubscribed = channels.iter().filter(|c| !c.is_subscribed).count();
            format!("{} 个，未订阅 {unsubscribed}", channels.len())
        }
        "visibility" => topic_visibility(stream, topic)?.to_string(),
        "history" => edit_history(id)?.to_string(),
        "react" => add_reaction(id, str_of(args, "emoji")).map(|()| "ok".to_owned())?,
        "unreact" => remove_reaction(id, str_of(args, "emoji")).map(|()| "ok".to_owned())?,
        "mark_unread" => mark_unread(id).map(|()| "ok".to_owned())?,
        "move_topic" => move_topic(
            stream,
            topic,
            int(args, "new_stream"),
            str_of(args, "new_topic"),
        )
        .map(|()| "ok".to_owned())?,
        "resolve" => set_topic_resolved(stream, topic, args["resolved"].as_bool().unwrap_or(true))
            .map(|()| "ok".to_owned())?,
        "delete_topic" => delete_topic(stream, topic).map(|()| "ok".to_owned())?,
        "open" => open_conversation(&conv(), (id > 0).then_some(id)).map(|()| "ok".to_owned())?,
        "schedule" => schedule_message(&conv(), str_of(args, "text"), int(args, "at"))?.to_string(),
        "toast" => show_toast(str_of(args, "text")).map(|()| "ok".to_owned())?,
        "notify" => show_notification(str_of(args, "title"), str_of(args, "text"))
            .map(|()| "ok".to_owned())?,
        "download" => download_attachment(str_of(args, "path"))?,
        "api" => api_request(
            str_of(args, "method"),
            str_of(args, "path"),
            &args["params"],
        )?
        .to_string(),
        _ => "未知操作".to_owned(),
    })
}

impl Plugin for Echo {
    fn on_enable(&self) {
        self.enabled.store(true, Ordering::Release);
        log(LOG_INFO, "已开启");
    }

    fn on_disable(&self) {
        self.enabled.store(false, Ordering::Release);
        log(LOG_INFO, "已停用");
    }

    fn on_account_login(&self, account_id: i64) {
        log(LOG_INFO, &format!("账号 {account_id} 已登录"));
    }

    fn on_account_logout(&self, account_id: i64) {
        log(LOG_INFO, &format!("账号 {account_id} 已退出"));
    }

    fn on_message(&self, message: &Message) -> Event {
        if message.is_outgoing() || message.text.trim() != "ping" {
            return Event::Ignore;
        }
        if let Conversation::Direct { user_ids } = &message.conv
            && user_ids.len() == 1
        {
            let reply = settings();
            let text = match str_of(&reply, "reply") {
                "" => "pong",
                text => text,
            };
            if let Err(error) = send_direct_message(user_ids, text) {
                log(LOG_WARNING, &format!("回复失败：{error}"));
            }
        }
        Event::Ignore
    }

    fn filter_incoming(&self, message: &Message) -> Event {
        if message.text.contains(BLOCK) {
            Event::Block
        } else {
            Event::Ignore
        }
    }

    fn filter_outgoing(&self, outgoing: &Outgoing) -> Event {
        if outgoing.content.contains(BLOCK) {
            return Event::Block;
        }
        if outgoing.kind != OutgoingKind::Text {
            log(LOG_INFO, &format!("发送种类 {:?}", outgoing.kind));
        }
        if outgoing.content == SLOW {
            std::thread::sleep(SLOW_FOR);
        }
        if let Some(rest) = outgoing.content.strip_prefix(REWRITE)
            && let Err(error) = set_outgoing_content(rest)
        {
            log(LOG_WARNING, &format!("改写失败：{error}"));
        }
        Event::Ignore
    }

    fn on_menu(&self, menu_id: i32) {
        log(LOG_INFO, &format!("点击了菜单 {menu_id}"));
    }

    fn on_message_menu(&self, _menu_id: i32, message: &Message) {
        let annotation = json!({
            "text": format!("**回声**：{}", message.text),
            "actions": [{"label": "填入", "fill": message.text, "style": "primary"}],
        });
        if let Err(error) = set_annotation(message.id, &annotation) {
            log(LOG_WARNING, &format!("附加失败：{error}"));
        }
    }

    fn on_context_menu(&self, menu_id: i32, click: &ContextMenuClick) {
        let target = match &click.target {
            Some(ContextTarget::Conversation(conv)) => conv.describe(),
            Some(ContextTarget::User { name, .. }) => format!("用户 {name}"),
            Some(ContextTarget::Channel { name, .. }) => format!("频道 {name}"),
            None => return,
        };
        log(LOG_INFO, &format!("右键菜单 {menu_id}：{target}"));
        if let Err(error) = set_status_text(&format!("回声：{target}")) {
            log(LOG_WARNING, &format!("状态栏提示失败：{error}"));
        }
    }

    fn on_composer_button(&self, _button_id: i32, click: &ComposerClick) {
        if let Some(rest) = click.text.strip_prefix(CALL) {
            let (op, args) = rest.split_once(' ').unwrap_or((rest, "{}"));
            let args = zulip_plugin_sdk::serde_json::from_str(args).unwrap_or(Value::Null);
            match call(op, &args) {
                Ok(result) => log(LOG_INFO, &format!("调用 {op}：{result}")),
                Err(error) => log(LOG_WARNING, &format!("调用 {op} 失败：{error}")),
            }
            return;
        }
        let replaced = format!("回声：{}", click.text);
        if let Err(error) = fill_composer(&replaced, true) {
            log(LOG_WARNING, &format!("填入失败：{error}"));
        }
    }

    fn on_message_edited(&self, event: &MessageEdited) {
        log(
            LOG_INFO,
            &format!("编辑 {}：{}", event.message_id, event.text),
        );
    }

    fn on_message_deleted(&self, event: &MessagesDeleted) {
        log(LOG_INFO, &format!("删除 {:?}", event.message_ids));
    }

    fn on_reaction_changed(&self, event: &ReactionChanged) {
        log(
            LOG_INFO,
            &format!(
                "回应 {} {} {}",
                event.message_id, event.reaction.emoji_name, event.added
            ),
        );
    }

    fn on_read_state_changed(&self, event: &ReadStateChanged) {
        log(
            LOG_INFO,
            &format!("已读 {:?} {}", event.message_ids, event.read),
        );
    }

    fn on_topic_moved(&self, event: &TopicMoved) {
        let describe = |conv: &Option<Conversation>| match conv {
            Some(Conversation::Channel {
                stream_id, topic, ..
            }) => format!("{stream_id}/{topic}"),
            _ => String::new(),
        };
        log(
            LOG_INFO,
            &format!("移动 {} -> {}", describe(&event.old), describe(&event.new)),
        );
    }

    fn on_channel_changed(&self, event: &ChannelsChanged) {
        log(
            LOG_INFO,
            &format!("频道 {:?} {}", event.stream_ids, event.op),
        );
    }

    fn on_user_changed(&self, event: &UserChanged) {
        log(LOG_INFO, &format!("用户 {}", event.user_id));
    }

    fn on_presence_changed(&self, event: &PresenceChanged) {
        log(LOG_INFO, &format!("在线 {:?}", event.user_ids));
    }

    fn on_conversation_opened(&self, event: &ConversationOpened) {
        if let Some(conv) = &event.conv {
            log(LOG_INFO, &format!("切换到 {}", conv.describe()));
        }
    }

    fn on_connection_changed(&self, event: &ConnectionChanged) {
        log(LOG_INFO, &format!("连接 {}", event.state));
    }

    fn on_settings_button(&self, button: &SettingsButton) {
        let text = format!("回复内容：{}", str_of(&button.values, "reply"));
        if let Err(error) = settings_text(&button.key, button.row, &text) {
            log(LOG_WARNING, &format!("回复设置窗口失败：{error}"));
        }
    }

    fn on_panel(&self, request: &PanelRequest) {
        let reply = match request.action.as_str() {
            "get" => self.panel_fields(request.user_id, ""),
            "set" => {
                if request.key == "nickname" {
                    self.set_nickname(request.user_id, request.value.as_str().unwrap_or_default());
                }
                self.panel_fields(request.user_id, "")
            }
            "click" => self.panel_fields(request.user_id, &format!("点击了 {}", request.key)),
            "remove" => {
                self.set_nickname(request.user_id, "");
                return;
            }
            _ => {
                let nicknames = self.nicknames.lock().unwrap_or_else(|e| e.into_inner());
                let entries: Vec<Value> = nicknames
                    .iter()
                    .map(|(id, text)| {
                        json!({
                            "target": {"account": request.account_id, "user": id, "name": id.to_string()},
                            "text": text,
                        })
                    })
                    .collect();
                json!({ "entries": entries })
            }
        };
        if let Err(error) = reply_panel(&reply) {
            log(LOG_WARNING, &format!("回复面板失败：{error}"));
        }
    }
}

export_plugin!(Echo::default(), "回声示例");
