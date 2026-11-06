//! 宿主函数（插件通过函数表调用）。插件可以在任何线程上调用，函数同步阻塞。
//! 不能在程序的异步运行时线程上调用；错误全部转换为错误码并写入插件日志。

use std::ffi::c_char;
use std::future::Future;
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::path::Path;
use std::sync::{Arc, Weak};
use std::time::Duration;

use serde_json::{Value, json};
use zulip_api::{ApiError, MessageQuery, NarrowTerm, OutgoingMessage};
use zulip_core::model::{Channel, ChatMessage, PresenceState};
use zulip_core::{ConversationFolder, ConversationKey};
use zulip_plugin_sdk::abi::{
    self, API_VERSION, AccountCb, ChannelCb, Ctx, HostApi, MessageCb, RawChannel, RawConversation,
    RawTopic, StringCb, TopicCb, UserCb, UserData,
};
use zulip_session::convert::message_from_dto;
use zulip_session::topic::is_resolved_name;
use zulip_session::{DeleteTarget, MessagePager, Session, SessionError, TopicDestination};

use crate::annotation::PluginAnnotation;
use crate::event::PluginEvent;
use crate::host::PluginHost;
use crate::launch;
use crate::marshal::{
    Conversation, MessageSnapshot, Scope, account_to_raw, conversation_json, read_text, user_to_raw,
};
use crate::permission;
use crate::plugin::NativePlugin;
use crate::request;
use crate::resources::{self, ResourceSelection};
use crate::settings::SettingsFeedback;
use crate::thread;

pub(crate) const HOST_CATEGORY: &str = "宿主";

const ROLES: [i32; 6] = [0, 100, 200, 300, 400, 600];
const API_TIMEOUT: Duration = Duration::from_secs(60);
const MAX_NOTICE_LENGTH: usize = 1000;
const MAX_STATUS_LENGTH: usize = 200;

struct PluginCtx {
    plugin: Arc<NativePlugin>,
    host: Weak<PluginHost>,
}

/// 函数表和 ctx 一直有效（插件不卸载）。
pub(crate) fn create(plugin: Arc<NativePlugin>, host: Weak<PluginHost>) -> &'static HostApi {
    let ctx: &'static mut PluginCtx = Box::leak(Box::new(PluginCtx { plugin, host }));
    Box::leak(Box::new(HostApi {
        struct_size: size_of::<HostApi>() as u32,
        api_version: API_VERSION,
        ctx: std::ptr::from_mut(ctx).cast(),
        add_log,
        get_accounts,
        get_current_account,
        select_account,
        get_data_dir,
        get_settings,
        set_settings_feedback,
        send_channel_message,
        send_direct_message,
        get_message,
        get_messages,
        get_users,
        get_user,
        get_channels,
        get_topics,
        add_reaction,
        set_starred,
        mark_read,
        upload_file,
        set_outgoing_content,
        set_annotation,
        launch: launch_target,
        edit_message,
        delete_message,
        update_channel,
        update_user,
        publish_resources,
        request_resource,
        reply_resource,
        reply_panel,
        search_messages,
        get_conversations,
        get_current_conversation,
        get_presence,
        get_user_groups,
        get_all_channels,
        get_topic_visibility,
        get_edit_history,
        remove_reaction,
        mark_unread,
        move_topic,
        set_topic_resolved,
        delete_topic,
        open_conversation,
        fill_composer,
        schedule_message,
        show_toast,
        show_notification,
        set_status_text,
        download_attachment,
        api_request,
    }))
}

enum Fail {
    Code(i32),
    Api(Box<ApiError>),
    Session(Box<SessionError>),
}

impl From<ApiError> for Fail {
    fn from(error: ApiError) -> Self {
        Self::Api(Box::new(error))
    }
}

impl From<SessionError> for Fail {
    fn from(error: SessionError) -> Self {
        Self::Session(Box::new(error))
    }
}

fn invalid<T>() -> Result<T, Fail> {
    Err(Fail::Code(abi::ERR_INVALID_ARG))
}

type Body<'a> = dyn FnOnce(&Call<'_>) -> Result<i64, Fail> + 'a;

struct Call<'a> {
    plugin: &'a Arc<NativePlugin>,
    host: Arc<PluginHost>,
    session: Option<Arc<Session>>,
}

impl Call<'_> {
    fn session(&self) -> Result<&Arc<Session>, Fail> {
        self.session
            .as_ref()
            .ok_or(Fail::Code(abi::ERR_NOT_LOGGED_IN))
    }

    fn wait<F: Future>(&self, future: F) -> F::Output {
        self.host.runtime().block_on(future)
    }

    fn warn(&self, text: String) {
        self.plugin.add_log(abi::LOG_WARNING, HOST_CATEGORY, &text);
    }
}

// 统一的检查与错误处理：插件 → 权限 → 账号 → 函数体。
fn guard(ctx: Ctx, name: &str, permission: Option<&str>, body: Box<Body<'_>>) -> i64 {
    if ctx.is_null() {
        return i64::from(abi::ERR_INVALID_ARG);
    }
    // SAFETY: ctx 由 create 创建，一直有效；插件原样传回。
    let ctx = unsafe { &*ctx.cast_const().cast::<PluginCtx>() };
    let plugin = &ctx.plugin;
    let Some(host) = ctx.host.upgrade() else {
        return i64::from(abi::ERR_NOT_LOGGED_IN);
    };
    if let Some(permission) = permission
        && !plugin.has_permission(permission)
    {
        plugin.add_log(
            abi::LOG_WARNING,
            HOST_CATEGORY,
            &format!("{name}：plugin.json 未声明权限 {permission}"),
        );
        return i64::from(abi::ERR_PERMISSION);
    }
    let session = host.caller_session(plugin);
    let call = Call {
        plugin,
        host,
        session,
    };
    match catch_unwind(AssertUnwindSafe(|| body(&call))) {
        Ok(Ok(value)) => value,
        Ok(Err(fail)) => i64::from(report(plugin, name, fail)),
        Err(_) => {
            plugin.add_log(abi::LOG_ERROR, HOST_CATEGORY, &format!("{name}：内部错误"));
            i64::from(abi::ERR_UNKNOWN)
        }
    }
}

fn report(plugin: &NativePlugin, name: &str, fail: Fail) -> i32 {
    let error = |text: String| plugin.add_log(abi::LOG_ERROR, HOST_CATEGORY, &text);
    let api = |api_error: &ApiError| match api_error {
        ApiError::Network(_) | ApiError::Timeout(_) => {
            error(format!("{name}：网络错误：{api_error}"));
            abi::ERR_NETWORK
        }
        ApiError::Cancelled => abi::ERR_NOT_LOGGED_IN,
        ApiError::Api { code, message, .. } => {
            error(format!("{name}：服务器返回错误 {code}：{message}"));
            abi::ERR_SERVER
        }
        other => {
            error(format!("{name}：{other}"));
            abi::ERR_UNKNOWN
        }
    };
    match fail {
        Fail::Code(code) => code,
        Fail::Api(e) => api(&e),
        Fail::Session(e) => match *e {
            SessionError::Api(e) => api(&e),
            SessionError::Invalid(text) => {
                error(format!("{name}：{text}"));
                abi::ERR_INVALID_ARG
            }
            e => {
                error(format!("{name}：{e}"));
                abi::ERR_UNKNOWN
            }
        },
    }
}

fn run32(ctx: Ctx, name: &str, permission: Option<&str>, body: Box<Body<'_>>) -> i32 {
    guard(ctx, name, permission, body) as i32
}

fn required(cb: Option<StringCb>) -> Result<StringCb, Fail> {
    cb.ok_or(Fail::Code(abi::ERR_INVALID_ARG))
}

fn send_string(cb: StringCb, text: &str, user: UserData) {
    let mut scope = Scope::default();
    let pointer = scope.text(text);
    // SAFETY: 字符串在 scope 存活期间有效；回调由插件提供。
    unsafe { cb(pointer, user) };
}

unsafe extern "C" fn add_log(ctx: Ctx, level: i32, category: *const c_char, text: *const c_char) {
    // SAFETY: 插件传入以 0 结尾的字符串。
    let (category, text) = unsafe { (read_text(category), read_text(text)) };
    let _ = guard(
        ctx,
        "add_log",
        None,
        Box::new(move |call| {
            call.plugin.add_log(
                level,
                &category.unwrap_or_default(),
                &text.unwrap_or_default(),
            );
            Ok(0)
        }),
    );
}

unsafe extern "C" fn get_accounts(ctx: Ctx, cb: Option<AccountCb>, user: UserData) -> i32 {
    run32(
        ctx,
        "get_accounts",
        None,
        Box::new(move |call| {
            let cb = cb.ok_or(Fail::Code(abi::ERR_INVALID_ARG))?;
            let sessions = call.host.sessions();
            for session in &sessions {
                let mut scope = Scope::default();
                let raw = account_to_raw(session, &mut scope);
                // SAFETY: raw 在 scope 存活期间有效。
                unsafe { cb(&raw, user) };
            }
            Ok(sessions.len() as i64)
        }),
    )
}

unsafe extern "C" fn get_current_account(ctx: Ctx) -> i64 {
    guard(
        ctx,
        "get_current_account",
        None,
        Box::new(|call| Ok(call.session()?.account_id())),
    )
}

unsafe extern "C" fn select_account(ctx: Ctx, account_id: i64) -> i32 {
    run32(
        ctx,
        "select_account",
        None,
        Box::new(move |call| {
            let sessions = call.host.sessions();
            let Some(target) = sessions.iter().find(|s| s.account_id() == account_id) else {
                return Err(Fail::Code(abi::ERR_NOT_FOUND));
            };
            call.plugin.select_account(account_id);
            if call.host.is_plugin_thread() {
                thread::replace_call_session(target);
            }
            Ok(0)
        }),
    )
}

unsafe extern "C" fn get_data_dir(ctx: Ctx, cb: Option<StringCb>, user: UserData) -> i32 {
    run32(
        ctx,
        "get_data_dir",
        None,
        Box::new(move |call| {
            let cb = required(cb)?;
            let dir = call.host.plugin_data_dir(&call.plugin.id());
            std::fs::create_dir_all(&dir).map_err(|e| Fail::from(SessionError::from(e)))?;
            let mut text = dir.to_string_lossy().into_owned();
            if !text.ends_with('\\') {
                text.push('\\');
            }
            send_string(cb, &text, user);
            Ok(0)
        }),
    )
}

unsafe extern "C" fn get_settings(ctx: Ctx, cb: Option<StringCb>, user: UserData) -> i32 {
    run32(
        ctx,
        "get_settings",
        None,
        Box::new(move |call| {
            send_string(required(cb)?, &call.plugin.settings_json(), user);
            Ok(0)
        }),
    )
}

unsafe extern "C" fn set_settings_feedback(ctx: Ctx, json: *const c_char) -> i32 {
    // SAFETY: 插件传入以 0 结尾的字符串。
    let json = unsafe { read_text(json) };
    run32(
        ctx,
        "set_settings_feedback",
        None,
        Box::new(move |call| {
            let Some(feedback) = json.as_deref().and_then(SettingsFeedback::parse) else {
                return invalid();
            };
            call.host.emit(PluginEvent::SettingsFeedback {
                plugin_id: call.plugin.id(),
                feedback,
            });
            Ok(0)
        }),
    )
}

fn send(call: &Call<'_>, message: OutgoingMessage) -> Result<i64, Fail> {
    let session = call.session()?;
    Ok(call.wait(session.client().send_message(&message))?.id)
}

unsafe extern "C" fn send_channel_message(
    ctx: Ctx,
    stream_id: i64,
    topic: *const c_char,
    markdown: *const c_char,
) -> i64 {
    // SAFETY: 插件传入以 0 结尾的字符串。
    let (topic, content) = unsafe { (read_text(topic), read_text(markdown)) };
    guard(
        ctx,
        "send_channel_message",
        Some(permission::SEND_MESSAGE),
        Box::new(move |call| {
            let content = content.filter(|c| !c.trim().is_empty());
            let (Some(topic), Some(content), true) = (topic, content, stream_id > 0) else {
                return invalid();
            };
            send(
                call,
                OutgoingMessage {
                    stream_id: Some(stream_id),
                    topic: Some(topic),
                    content,
                    ..OutgoingMessage::default()
                },
            )
        }),
    )
}

unsafe extern "C" fn send_direct_message(
    ctx: Ctx,
    user_ids: *const i64,
    count: i32,
    markdown: *const c_char,
) -> i64 {
    // SAFETY: 插件传入 count 个数和以 0 结尾的字符串。
    let (ids, content) = unsafe {
        let ids = if user_ids.is_null() || count <= 0 {
            Vec::new()
        } else {
            std::slice::from_raw_parts(user_ids, count as usize).to_vec()
        };
        (ids, read_text(markdown))
    };
    guard(
        ctx,
        "send_direct_message",
        Some(permission::SEND_MESSAGE),
        Box::new(move |call| {
            let mut ids = ids;
            ids.sort_unstable();
            ids.dedup();
            let content = content.filter(|c| !c.trim().is_empty());
            let (Some(content), false, true) =
                (content, ids.is_empty(), ids.iter().all(|id| *id > 0))
            else {
                return invalid();
            };
            send(
                call,
                OutgoingMessage {
                    user_ids: ids,
                    content,
                    ..OutgoingMessage::default()
                },
            )
        }),
    )
}

fn send_message(cb: MessageCb, message: &ChatMessage, session: &Session, user: UserData) -> bool {
    let Some(snapshot) = MessageSnapshot::new(message, session, true) else {
        return false;
    };
    let mut scope = Scope::default();
    let raw = snapshot.to_raw(&mut scope);
    // SAFETY: raw 在 scope 存活期间有效。
    unsafe { cb(&raw, user) };
    true
}

unsafe extern "C" fn get_message(
    ctx: Ctx,
    message_id: i64,
    cb: Option<MessageCb>,
    user: UserData,
) -> i32 {
    run32(
        ctx,
        "get_message",
        Some(permission::READ_MESSAGE),
        Box::new(move |call| {
            let (Some(cb), true) = (cb, message_id > 0) else {
                return invalid();
            };
            let session = call.session()?;
            let dto = call.wait(session.client().get_message(message_id, true))?;
            let message = message_from_dto(&dto, session.self_id());
            if send_message(cb, &message, session, user) {
                Ok(0)
            } else {
                Err(Fail::Code(abi::ERR_NOT_FOUND))
            }
        }),
    )
}

unsafe extern "C" fn get_messages(
    ctx: Ctx,
    conv: *const RawConversation,
    anchor: i64,
    num_before: i32,
    num_after: i32,
    cb: Option<MessageCb>,
    user: UserData,
) -> i32 {
    // SAFETY: conv 为空或指向插件提供的有效结构。
    let conversation = unsafe { conv.as_ref().map(|c| Conversation::from_raw(c)) };
    run32(
        ctx,
        "get_messages",
        Some(permission::READ_MESSAGE),
        Box::new(move |call| {
            let Some(cb) = cb else {
                return invalid();
            };
            let session = call.session()?;
            let narrow = match conversation {
                None => Vec::new(),
                Some(None) => return invalid(),
                Some(Some(Conversation::Channel {
                    stream_id, topic, ..
                })) => vec![NarrowTerm::channel(stream_id), NarrowTerm::topic(&topic)],
                Some(Some(Conversation::Direct { user_ids })) if user_ids.is_empty() => {
                    vec![NarrowTerm::dm(&[session.self_id()])]
                }
                Some(Some(Conversation::Direct { user_ids })) => vec![NarrowTerm::dm(&user_ids)],
            };
            let anchor = match anchor {
                0 => "newest".to_owned(),
                -1 => "oldest".to_owned(),
                id if id > 0 => id.to_string(),
                _ => return invalid(),
            };
            let query = MessageQuery::new(
                narrow,
                anchor,
                num_before.clamp(0, 1000) as u32,
                num_after.clamp(0, 1000) as u32,
            );
            let result = call.wait(session.client().get_messages(&query))?;
            let mut count = 0;
            for dto in &result.messages {
                let message = message_from_dto(dto, session.self_id());
                if send_message(cb, &message, session, user) {
                    count += 1;
                }
            }
            Ok(count)
        }),
    )
}

unsafe extern "C" fn get_users(ctx: Ctx, cb: Option<UserCb>, user: UserData) -> i32 {
    run32(
        ctx,
        "get_users",
        Some(permission::READ_CONTACTS),
        Box::new(move |call| {
            let Some(cb) = cb else {
                return invalid();
            };
            let session = call.session()?;
            let users: Vec<_> = session
                .users()
                .into_iter()
                .filter(|u| u.is_active)
                .collect();
            for item in &users {
                let mut scope = Scope::default();
                let raw = user_to_raw(item, session, &mut scope);
                // SAFETY: raw 在 scope 存活期间有效。
                unsafe { cb(&raw, user) };
            }
            Ok(users.len() as i64)
        }),
    )
}

unsafe extern "C" fn get_user(ctx: Ctx, user_id: i64, cb: Option<UserCb>, user: UserData) -> i32 {
    run32(
        ctx,
        "get_user",
        Some(permission::READ_CONTACTS),
        Box::new(move |call| {
            let Some(cb) = cb else {
                return invalid();
            };
            let session = call.session()?;
            let found = session
                .user(user_id)
                .ok_or(Fail::Code(abi::ERR_NOT_FOUND))?;
            let mut scope = Scope::default();
            let raw = user_to_raw(&found, session, &mut scope);
            // SAFETY: raw 在 scope 存活期间有效。
            unsafe { cb(&raw, user) };
            Ok(0)
        }),
    )
}

fn channel_to_raw(channel: &Channel, scope: &mut Scope) -> RawChannel {
    RawChannel {
        struct_size: size_of::<RawChannel>() as u32,
        stream_id: channel.stream_id,
        name: scope.text(&channel.name),
        description: scope.text(&channel.description),
        is_private: i32::from(channel.invite_only),
        is_subscribed: i32::from(channel.is_subscribed()),
    }
}

unsafe extern "C" fn get_channels(ctx: Ctx, cb: Option<ChannelCb>, user: UserData) -> i32 {
    run32(
        ctx,
        "get_channels",
        Some(permission::READ_CONTACTS),
        Box::new(move |call| {
            let Some(cb) = cb else {
                return invalid();
            };
            let channels = call.session()?.subscribed_channels();
            for channel in &channels {
                let mut scope = Scope::default();
                let raw = channel_to_raw(channel, &mut scope);
                // SAFETY: raw 在 scope 存活期间有效。
                unsafe { cb(&raw, user) };
            }
            Ok(channels.len() as i64)
        }),
    )
}

unsafe extern "C" fn get_topics(
    ctx: Ctx,
    stream_id: i64,
    cb: Option<TopicCb>,
    user: UserData,
) -> i32 {
    run32(
        ctx,
        "get_topics",
        Some(permission::READ_CONTACTS),
        Box::new(move |call| {
            let Some(cb) = cb else {
                return invalid();
            };
            let session = call.session()?;
            if !session
                .channel(stream_id)
                .is_some_and(|c| c.is_subscribed())
            {
                return Err(Fail::Code(abi::ERR_NOT_FOUND));
            }
            if session.topics(stream_id).is_none() {
                call.wait(session.load_topics(stream_id))?;
            }
            let topics = session.topics(stream_id).unwrap_or_default();
            for topic in &topics {
                let mut scope = Scope::default();
                let raw = RawTopic {
                    struct_size: size_of::<RawTopic>() as u32,
                    stream_id,
                    max_message_id: topic.max_id,
                    name: scope.text(&topic.name),
                };
                // SAFETY: raw 在 scope 存活期间有效。
                unsafe { cb(&raw, user) };
            }
            Ok(topics.len() as i64)
        }),
    )
}

unsafe extern "C" fn add_reaction(ctx: Ctx, message_id: i64, emoji_name: *const c_char) -> i32 {
    // SAFETY: 插件传入以 0 结尾的字符串。
    let name = unsafe { read_text(emoji_name) };
    run32(
        ctx,
        "add_reaction",
        Some(permission::MODIFY_MESSAGE),
        Box::new(move |call| {
            let name = name.filter(|n| !n.is_empty());
            let (Some(name), true) = (name, message_id > 0) else {
                return invalid();
            };
            let session = call.session()?;
            let catalog = session.emoji_catalog();
            let emoji = catalog.get(&name).ok_or(Fail::Code(abi::ERR_NOT_FOUND))?;
            call.wait(session.client().add_reaction(
                message_id,
                &emoji.name,
                &emoji.code,
                emoji.kind.reaction_type(),
            ))?;
            Ok(0)
        }),
    )
}

unsafe extern "C" fn set_starred(ctx: Ctx, message_id: i64, starred: i32) -> i32 {
    run32(
        ctx,
        "set_starred",
        Some(permission::MODIFY_MESSAGE),
        Box::new(move |call| {
            if message_id <= 0 {
                return invalid();
            }
            call.wait(call.session()?.set_starred(&[message_id], starred != 0))?;
            Ok(0)
        }),
    )
}

unsafe extern "C" fn mark_read(ctx: Ctx, message_id: i64) -> i32 {
    run32(
        ctx,
        "mark_read",
        Some(permission::MODIFY_MESSAGE),
        Box::new(move |call| {
            if message_id <= 0 {
                return invalid();
            }
            call.wait(call.session()?.mark_read(&[message_id]))?;
            Ok(0)
        }),
    )
}

unsafe extern "C" fn upload_file(
    ctx: Ctx,
    local_path: *const c_char,
    cb: Option<StringCb>,
    user: UserData,
) -> i32 {
    // SAFETY: 插件传入以 0 结尾的字符串。
    let path = unsafe { read_text(local_path) };
    run32(
        ctx,
        "upload_file",
        Some(permission::UPLOAD_FILE),
        Box::new(move |call| {
            let path = path.filter(|p| !p.is_empty());
            let (Some(cb), Some(path)) = (cb, path) else {
                return invalid();
            };
            let path = Path::new(&path);
            if !path.is_file() {
                return Err(Fail::Code(abi::ERR_NOT_FOUND));
            }
            let result = call.wait(call.session()?.upload_file(path, |_| {}))?;
            send_string(cb, &result.url, user);
            Ok(0)
        }),
    )
}

unsafe extern "C" fn set_outgoing_content(ctx: Ctx, markdown: *const c_char) -> i32 {
    // SAFETY: 插件传入以 0 结尾的字符串。
    let content = unsafe { read_text(markdown) };
    run32(
        ctx,
        "set_outgoing_content",
        Some(permission::MODIFY_MESSAGE),
        Box::new(move |call| {
            let Some(content) = content else {
                return invalid();
            };
            if call.host.is_plugin_thread() && thread::try_set_outgoing(content) {
                Ok(0)
            } else {
                Err(Fail::Code(abi::ERR_WRONG_CONTEXT))
            }
        }),
    )
}

unsafe extern "C" fn set_annotation(ctx: Ctx, message_id: i64, json: *const c_char) -> i32 {
    // SAFETY: 插件传入以 0 结尾的字符串。
    let text = unsafe { read_text(json) };
    run32(
        ctx,
        "set_annotation",
        Some(permission::ANNOTATE_MESSAGE),
        Box::new(move |call| {
            if message_id <= 0 {
                return invalid();
            }
            let plugin = call.plugin;
            let mut annotation = None;
            if let Some(text) = text.filter(|t| !t.trim().is_empty()) {
                annotation = PluginAnnotation::parse(&plugin.id(), &text).map_err(|error| {
                    call.warn(format!("set_annotation：{error}"));
                    Fail::Code(abi::ERR_INVALID_ARG)
                })?;
            }
            if let Some(a) = &annotation {
                let missing = if !a.replace.is_empty()
                    && !plugin.has_permission(permission::RENDER_MESSAGE)
                {
                    Some(permission::RENDER_MESSAGE)
                } else if a.actions.iter().any(|x| x.is_fill())
                    && !plugin.has_permission(permission::FILL_COMPOSER)
                {
                    Some(permission::FILL_COMPOSER)
                } else if a.actions.iter().any(|x| !x.is_fill())
                    && !plugin.has_permission(permission::LAUNCH)
                {
                    Some(permission::LAUNCH)
                } else {
                    None
                };
                if let Some(missing) = missing {
                    call.warn(format!("set_annotation：plugin.json 未声明权限 {missing}"));
                    return Err(Fail::Code(abi::ERR_PERMISSION));
                }
            }
            let account_id = call.session()?.account_id();
            call.host
                .set_annotation(account_id, message_id, &plugin.id(), annotation);
            Ok(0)
        }),
    )
}

unsafe extern "C" fn launch_target(ctx: Ctx, target: *const c_char, args: *const c_char) -> i32 {
    // SAFETY: 插件传入以 0 结尾的字符串。
    let (target, args) = unsafe { (read_text(target), read_text(args)) };
    run32(
        ctx,
        "launch",
        Some(permission::LAUNCH),
        Box::new(move |call| {
            let target = target.as_deref().map(str::trim).unwrap_or_default();
            if target.is_empty() {
                return invalid();
            }
            // 白名单之外的目标只能放在附加内容的按钮中，由用户点击。
            if !PluginHost::is_whitelisted(call.plugin, target) {
                call.warn(format!("launch：不在白名单中：{target}"));
                return Err(Fail::Code(abi::ERR_PERMISSION));
            }
            let args = args.as_deref().unwrap_or_default();
            match launch::launch(target, args) {
                Ok(()) => {
                    call.plugin.add_log(
                        abi::LOG_INFO,
                        HOST_CATEGORY,
                        &format!("已打开 {}", launch::describe(target, args)),
                    );
                    Ok(0)
                }
                Err(launch::LaunchError::InvalidTarget) => invalid(),
                Err(error) => {
                    call.plugin
                        .add_log(abi::LOG_ERROR, HOST_CATEGORY, &format!("launch：{error}"));
                    Err(Fail::Code(abi::ERR_NOT_FOUND))
                }
            }
        }),
    )
}

unsafe extern "C" fn edit_message(ctx: Ctx, message_id: i64, markdown: *const c_char) -> i32 {
    // SAFETY: 插件传入以 0 结尾的字符串。
    let content = unsafe { read_text(markdown) };
    run32(
        ctx,
        "edit_message",
        Some(permission::EDIT_MESSAGE),
        Box::new(move |call| {
            let content = content.filter(|c| !c.trim().is_empty());
            let (Some(content), true) = (content, message_id > 0) else {
                return invalid();
            };
            call.wait(call.session()?.edit_message(message_id, &content))?;
            Ok(0)
        }),
    )
}

unsafe extern "C" fn delete_message(ctx: Ctx, message_id: i64) -> i32 {
    run32(
        ctx,
        "delete_message",
        Some(permission::DELETE_MESSAGE),
        Box::new(move |call| {
            if message_id <= 0 {
                return invalid();
            }
            call.wait(call.session()?.delete(DeleteTarget::Message(message_id)))?;
            Ok(0)
        }),
    )
}

unsafe extern "C" fn update_channel(
    ctx: Ctx,
    stream_id: i64,
    name: *const c_char,
    description: *const c_char,
) -> i32 {
    // SAFETY: 插件传入以 0 结尾的字符串。
    let (name, description) = unsafe { (read_text(name), read_text(description)) };
    run32(
        ctx,
        "update_channel",
        Some(permission::MANAGE_CHANNEL),
        Box::new(move |call| {
            let name = name.as_deref().map(str::trim);
            if stream_id <= 0
                || (name.is_none() && description.is_none())
                || name.is_some_and(str::is_empty)
            {
                return invalid();
            }
            let mut args = Vec::new();
            if let Some(name) = name {
                args.push(("new_name", Value::from(name)));
            }
            if let Some(description) = description {
                args.push(("description", Value::from(description)));
            }
            call.wait(call.session()?.update_channel(stream_id, &args))?;
            Ok(0)
        }),
    )
}

unsafe extern "C" fn update_user(
    ctx: Ctx,
    user_id: i64,
    full_name: *const c_char,
    role: i32,
) -> i32 {
    // SAFETY: 插件传入以 0 结尾的字符串。
    let name = unsafe { read_text(full_name) };
    run32(
        ctx,
        "update_user",
        Some(permission::MANAGE_USER),
        Box::new(move |call| {
            let name = name.as_deref().map(str::trim);
            if user_id <= 0
                || (name.is_none() && role == 0)
                || name.is_some_and(str::is_empty)
                || !ROLES.contains(&role)
            {
                return invalid();
            }
            let role = (role != 0).then_some(role);
            call.wait(call.session()?.update_user(user_id, name, role))?;
            Ok(0)
        }),
    )
}

unsafe extern "C" fn publish_resources(ctx: Ctx, kind: *const c_char, json: *const c_char) -> i32 {
    // SAFETY: 插件传入以 0 结尾的字符串。
    let (kind, text) = unsafe { (read_text(kind), read_text(json)) };
    run32(
        ctx,
        "publish_resources",
        None,
        Box::new(move |call| {
            let kind = kind.unwrap_or_default();
            if !resources::is_valid_type(&kind) {
                return invalid();
            }
            let id = call.plugin.id();
            let list = match text.filter(|t| !t.trim().is_empty()) {
                Some(text) => match resources::parse(&id, &text) {
                    Some(list) => list,
                    None => return invalid(),
                },
                None => Vec::new(),
            };
            call.host.publish_resources(&id, &kind, list);
            Ok(0)
        }),
    )
}

unsafe extern "C" fn request_resource(
    ctx: Ctx,
    json: *const c_char,
    cb: Option<StringCb>,
    user: UserData,
) -> i32 {
    // SAFETY: 插件传入以 0 结尾的字符串。
    let text = unsafe { read_text(json) };
    run32(
        ctx,
        "request_resource",
        None,
        Box::new(move |call| {
            // 插件线程上等待会卡住发布资源的插件（它的回调也在插件线程上）。
            if call.host.is_plugin_thread() {
                return Err(Fail::Code(abi::ERR_WRONG_CONTEXT));
            }
            let value: Value = text
                .as_deref()
                .and_then(|t| serde_json::from_str(t).ok())
                .unwrap_or(Value::Null);
            let Some(target) = ResourceSelection::from_json(&value["resource"]) else {
                return invalid();
            };
            if !call.plugin.depends_on(&target.plugin_id) {
                call.warn(format!(
                    "request_resource：plugin.json 的 dependencies 未声明 {}",
                    target.plugin_id
                ));
                return Err(Fail::Code(abi::ERR_PERMISSION));
            }
            let reply = call
                .host
                .request_resource(call.plugin, &target, &value["request"])
                .map_err(Fail::Code)?;
            if let Some(cb) = cb {
                send_string(cb, &reply, user);
            }
            Ok(0)
        }),
    )
}

unsafe extern "C" fn reply_resource(ctx: Ctx, request_id: i64, json: *const c_char) -> i32 {
    // SAFETY: 插件传入以 0 结尾的字符串。
    let text = unsafe { read_text(json) }.unwrap_or_default();
    run32(
        ctx,
        "reply_resource",
        None,
        Box::new(move |call| {
            if call.host.reply_resource(call.plugin, request_id, text) {
                Ok(0)
            } else {
                Err(Fail::Code(abi::ERR_NOT_FOUND))
            }
        }),
    )
}

unsafe extern "C" fn reply_panel(ctx: Ctx, json: *const c_char) -> i32 {
    // SAFETY: 插件传入以 0 结尾的字符串。
    let text = unsafe { read_text(json) }.unwrap_or_default();
    run32(
        ctx,
        "reply_panel",
        None,
        Box::new(move |call| {
            if call.host.is_plugin_thread() && thread::try_reply_panel(text) {
                Ok(0)
            } else {
                Err(Fail::Code(abi::ERR_WRONG_CONTEXT))
            }
        }),
    )
}

fn send_json(cb: Option<StringCb>, value: &Value, user: UserData) -> Result<(), Fail> {
    send_string(required(cb)?, &value.to_string(), user);
    Ok(())
}

// 带频道和话题的操作的参数。
fn topic_key(stream_id: i64, topic: Option<String>) -> Result<ConversationKey, Fail> {
    match topic.filter(|t| !t.is_empty()) {
        Some(topic) if stream_id > 0 => Ok(ConversationKey::topic(stream_id, topic)),
        _ => invalid(),
    }
}

unsafe extern "C" fn search_messages(
    ctx: Ctx,
    query: *const c_char,
    next_cursor: *mut i64,
    cb: Option<MessageCb>,
    user: UserData,
) -> i32 {
    // SAFETY: 插件传入以 0 结尾的字符串。
    let text = unsafe { read_text(query) };
    let next_cursor = next_cursor as usize;
    run32(
        ctx,
        "search_messages",
        Some(permission::READ_MESSAGE),
        Box::new(move |call| {
            let Some(cb) = cb else {
                return invalid();
            };
            let session = call.session()?;
            let Some(query) = text
                .as_deref()
                .and_then(|t| request::parse_search(t, session.self_id()))
            else {
                return invalid();
            };
            let mut pager = match query.cursor {
                Some(cursor) => MessagePager::resume(query.search, query.page_size, cursor),
                None => MessagePager::with_page_size(query.search, query.page_size),
            };
            let items = call.wait(session.next_page(&mut pager))?;
            let mut count = 0;
            for item in &items {
                if send_message(cb, &item.message, session, user) {
                    count += 1;
                }
            }
            let next = if pager.is_done() {
                0
            } else {
                pager.cursor().unwrap_or_default()
            };
            if next_cursor != 0 {
                // SAFETY: 不为空时插件传入有效的指针，调用期间有效。
                unsafe { *(next_cursor as *mut i64) = next };
            }
            Ok(count)
        }),
    )
}

unsafe extern "C" fn get_conversations(ctx: Ctx, cb: Option<StringCb>, user: UserData) -> i32 {
    run32(
        ctx,
        "get_conversations",
        Some(permission::READ_MESSAGE),
        Box::new(move |call| {
            let session = call.session()?;
            let mut rows = session.message_rows();
            rows.extend(session.folder_rows(ConversationFolder::Helper));
            rows.extend(session.folder_rows(ConversationFolder::Resolved));
            let list: Vec<Value> = rows
                .iter()
                .filter_map(|row| {
                    let key = row.key()?;
                    Some(json!({
                        "conv": conversation_json(Some(key), session),
                        "title": row.title,
                        "preview": row.preview,
                        "time": row.time,
                        "unread": row.unread_count,
                        "pinned": row.pinned,
                    }))
                })
                .collect();
            let total = session.total_unread_for_badge();
            send_json(
                cb,
                &json!({"total_unread": total, "conversations": list}),
                user,
            )?;
            Ok(list.len() as i64)
        }),
    )
}

unsafe extern "C" fn get_current_conversation(
    ctx: Ctx,
    cb: Option<StringCb>,
    user: UserData,
) -> i32 {
    run32(
        ctx,
        "get_current_conversation",
        Some(permission::READ_MESSAGE),
        Box::new(move |call| {
            let session = call.session()?;
            let key = session
                .current_conversation()
                .ok_or(Fail::Code(abi::ERR_NOT_FOUND))?;
            send_json(cb, &conversation_json(Some(&key), session), user)?;
            Ok(0)
        }),
    )
}

unsafe extern "C" fn get_presence(
    ctx: Ctx,
    user_id: i64,
    cb: Option<StringCb>,
    user: UserData,
) -> i32 {
    run32(
        ctx,
        "get_presence",
        Some(permission::READ_CONTACTS),
        Box::new(move |call| {
            let session = call.session()?;
            let ids: Vec<i64> = match user_id {
                0 => session
                    .users()
                    .into_iter()
                    .filter(|u| u.is_active)
                    .map(|u| u.id)
                    .collect(),
                id if session.user(id).is_some() => vec![id],
                _ => return Err(Fail::Code(abi::ERR_NOT_FOUND)),
            };
            let list: Vec<Value> = ids
                .iter()
                .map(|id| {
                    let presence = match session.presence(*id) {
                        PresenceState::Online => "online",
                        PresenceState::Idle => "idle",
                        PresenceState::Offline => "offline",
                    };
                    json!({"user": id, "presence": presence, "status_text": session.status_text(*id)})
                })
                .collect();
            send_json(cb, &Value::Array(list), user)?;
            Ok(ids.len() as i64)
        }),
    )
}

unsafe extern "C" fn get_user_groups(ctx: Ctx, cb: Option<StringCb>, user: UserData) -> i32 {
    run32(
        ctx,
        "get_user_groups",
        Some(permission::READ_CONTACTS),
        Box::new(move |call| {
            let groups = call.session()?.user_groups();
            send_json(cb, &json!(groups), user)?;
            Ok(groups.len() as i64)
        }),
    )
}

unsafe extern "C" fn get_all_channels(ctx: Ctx, cb: Option<ChannelCb>, user: UserData) -> i32 {
    run32(
        ctx,
        "get_all_channels",
        Some(permission::READ_CONTACTS),
        Box::new(move |call| {
            let Some(cb) = cb else {
                return invalid();
            };
            let channels = call.session()?.channels();
            for channel in &channels {
                let mut scope = Scope::default();
                let raw = channel_to_raw(channel, &mut scope);
                // SAFETY: raw 在 scope 存活期间有效。
                unsafe { cb(&raw, user) };
            }
            Ok(channels.len() as i64)
        }),
    )
}

unsafe extern "C" fn get_topic_visibility(ctx: Ctx, stream_id: i64, topic: *const c_char) -> i32 {
    // SAFETY: 插件传入以 0 结尾的字符串。
    let topic = unsafe { read_text(topic) };
    run32(
        ctx,
        "get_topic_visibility",
        Some(permission::READ_CONTACTS),
        Box::new(move |call| {
            let topic = topic.filter(|t| !t.is_empty());
            let (Some(topic), true) = (topic, stream_id > 0) else {
                return invalid();
            };
            Ok(call.session()?.topic_visibility(stream_id, &topic) as i64)
        }),
    )
}

unsafe extern "C" fn get_edit_history(
    ctx: Ctx,
    message_id: i64,
    cb: Option<StringCb>,
    user: UserData,
) -> i32 {
    run32(
        ctx,
        "get_edit_history",
        Some(permission::READ_MESSAGE),
        Box::new(move |call| {
            if message_id <= 0 {
                return invalid();
            }
            let history = call.wait(call.session()?.message_history(message_id))?;
            send_json(cb, &json!(history), user)?;
            Ok(history.len() as i64)
        }),
    )
}

unsafe extern "C" fn remove_reaction(ctx: Ctx, message_id: i64, emoji_name: *const c_char) -> i32 {
    // SAFETY: 插件传入以 0 结尾的字符串。
    let name = unsafe { read_text(emoji_name) };
    run32(
        ctx,
        "remove_reaction",
        Some(permission::MODIFY_MESSAGE),
        Box::new(move |call| {
            let name = name.filter(|n| !n.is_empty());
            let (Some(name), true) = (name, message_id > 0) else {
                return invalid();
            };
            let session = call.session()?;
            let catalog = session.emoji_catalog();
            let emoji = catalog.get(&name).ok_or(Fail::Code(abi::ERR_NOT_FOUND))?;
            call.wait(session.client().remove_reaction(
                message_id,
                &emoji.name,
                &emoji.code,
                emoji.kind.reaction_type(),
            ))?;
            Ok(0)
        }),
    )
}

unsafe extern "C" fn mark_unread(ctx: Ctx, message_id: i64) -> i32 {
    run32(
        ctx,
        "mark_unread",
        Some(permission::MODIFY_MESSAGE),
        Box::new(move |call| {
            if message_id <= 0 {
                return invalid();
            }
            let session = call.session()?;
            let key = match session.message(message_id) {
                Some(message) => message.key.clone(),
                None => {
                    let dto = call.wait(session.client().get_message(message_id, false))?;
                    message_from_dto(&dto, session.self_id()).key
                }
            };
            call.wait(session.mark_unread_from(&key, message_id))?;
            Ok(0)
        }),
    )
}

unsafe extern "C" fn move_topic(
    ctx: Ctx,
    stream_id: i64,
    topic: *const c_char,
    new_stream_id: i64,
    new_topic: *const c_char,
) -> i32 {
    // SAFETY: 插件传入以 0 结尾的字符串。
    let (topic, new_topic) = unsafe { (read_text(topic), read_text(new_topic)) };
    run32(
        ctx,
        "move_topic",
        Some(permission::MANAGE_TOPIC),
        Box::new(move |call| {
            let key = topic_key(stream_id, topic)?;
            let new_topic = new_topic
                .as_deref()
                .map(str::trim)
                .filter(|t| !t.is_empty());
            let (Some(new_topic), true) = (new_topic, new_stream_id >= 0) else {
                return invalid();
            };
            let destination = TopicDestination {
                stream_id: if new_stream_id == 0 {
                    stream_id
                } else {
                    new_stream_id
                },
                topic: new_topic.to_owned(),
            };
            call.wait(call.session()?.move_topic(&key, &destination))?;
            Ok(0)
        }),
    )
}

unsafe extern "C" fn set_topic_resolved(
    ctx: Ctx,
    stream_id: i64,
    topic: *const c_char,
    resolved: i32,
) -> i32 {
    // SAFETY: 插件传入以 0 结尾的字符串。
    let topic = unsafe { read_text(topic) };
    run32(
        ctx,
        "set_topic_resolved",
        Some(permission::MANAGE_TOPIC),
        Box::new(move |call| {
            let key = topic_key(stream_id, topic)?;
            let session = call.session()?;
            if is_resolved_name(&session.topic_name(&key)) != (resolved != 0) {
                call.wait(session.toggle_topic_resolved(&key))?;
            }
            Ok(0)
        }),
    )
}

unsafe extern "C" fn delete_topic(ctx: Ctx, stream_id: i64, topic: *const c_char) -> i32 {
    // SAFETY: 插件传入以 0 结尾的字符串。
    let topic = unsafe { read_text(topic) };
    run32(
        ctx,
        "delete_topic",
        Some(permission::MANAGE_TOPIC),
        Box::new(move |call| {
            let key = topic_key(stream_id, topic)?;
            call.wait(call.session()?.delete(DeleteTarget::Topic(key)))?;
            Ok(0)
        }),
    )
}

unsafe extern "C" fn open_conversation(
    ctx: Ctx,
    conv: *const RawConversation,
    message_id: i64,
) -> i32 {
    // SAFETY: conv 为空或指向插件提供的有效结构。
    let conversation = unsafe { conv.as_ref().and_then(|c| Conversation::from_raw(c)) };
    run32(
        ctx,
        "open_conversation",
        Some(permission::OPEN_CONVERSATION),
        Box::new(move |call| {
            let (Some(conversation), true) = (conversation, message_id >= 0) else {
                return invalid();
            };
            let session = call.session()?;
            if let Conversation::Channel { stream_id, .. } = &conversation
                && session.channel(*stream_id).is_none()
            {
                return Err(Fail::Code(abi::ERR_NOT_FOUND));
            }
            call.host.emit(PluginEvent::OpenConversation {
                account_id: session.account_id(),
                key: conversation.to_key(session.self_id()),
                message_id: (message_id > 0).then_some(message_id),
            });
            Ok(0)
        }),
    )
}

unsafe extern "C" fn fill_composer(ctx: Ctx, text: *const c_char, mode: i32) -> i32 {
    // SAFETY: 插件传入以 0 结尾的字符串。
    let text = unsafe { read_text(text) };
    run32(
        ctx,
        "fill_composer",
        Some(permission::FILL_COMPOSER),
        Box::new(move |call| {
            let (Some(text), true) = (text, matches!(mode, abi::FILL_INSERT | abi::FILL_REPLACE))
            else {
                return invalid();
            };
            call.host.emit(PluginEvent::FillComposer {
                plugin_id: call.plugin.id(),
                account_id: call.session()?.account_id(),
                text,
                replace: mode == abi::FILL_REPLACE,
            });
            Ok(0)
        }),
    )
}

unsafe extern "C" fn schedule_message(
    ctx: Ctx,
    conv: *const RawConversation,
    markdown: *const c_char,
    deliver_at: i64,
) -> i64 {
    // SAFETY: conv 为空或指向插件提供的有效结构；插件传入以 0 结尾的字符串。
    let (conversation, content) = unsafe {
        (
            conv.as_ref().and_then(|c| Conversation::from_raw(c)),
            read_text(markdown),
        )
    };
    guard(
        ctx,
        "schedule_message",
        Some(permission::SEND_MESSAGE),
        Box::new(move |call| {
            let content = content.filter(|c| !c.trim().is_empty());
            let (Some(conversation), Some(content), true) = (conversation, content, deliver_at > 0)
            else {
                return invalid();
            };
            let session = call.session()?;
            let key = conversation.to_key(session.self_id());
            Ok(call.wait(session.schedule_message(&key, &content, deliver_at))?)
        }),
    )
}

fn limited(text: Option<String>, max: usize) -> Option<String> {
    text.map(|t| t.chars().take(max).collect())
}

unsafe extern "C" fn show_toast(ctx: Ctx, text: *const c_char) -> i32 {
    // SAFETY: 插件传入以 0 结尾的字符串。
    let text = unsafe { read_text(text) };
    run32(
        ctx,
        "show_toast",
        Some(permission::NOTIFY),
        Box::new(move |call| {
            let Some(text) =
                limited(text, MAX_NOTICE_LENGTH).filter(|t: &String| !t.trim().is_empty())
            else {
                return invalid();
            };
            call.host.emit(PluginEvent::Toast {
                plugin_id: call.plugin.id(),
                text,
            });
            Ok(0)
        }),
    )
}

unsafe extern "C" fn show_notification(ctx: Ctx, title: *const c_char, body: *const c_char) -> i32 {
    // SAFETY: 插件传入以 0 结尾的字符串。
    let (title, body) = unsafe { (read_text(title), read_text(body)) };
    run32(
        ctx,
        "show_notification",
        Some(permission::NOTIFY),
        Box::new(move |call| {
            let Some(title) =
                limited(title, MAX_NOTICE_LENGTH).filter(|t: &String| !t.trim().is_empty())
            else {
                return invalid();
            };
            call.host.emit(PluginEvent::SystemNotification {
                plugin_id: call.plugin.id(),
                title,
                body: limited(body, MAX_NOTICE_LENGTH).unwrap_or_default(),
            });
            Ok(0)
        }),
    )
}

unsafe extern "C" fn set_status_text(ctx: Ctx, text: *const c_char) -> i32 {
    // SAFETY: 插件传入以 0 结尾的字符串。
    let text = unsafe { read_text(text) };
    run32(
        ctx,
        "set_status_text",
        Some(permission::NOTIFY),
        Box::new(move |call| {
            let text = limited(text, MAX_STATUS_LENGTH).unwrap_or_default();
            call.host.set_status_text(&call.plugin.id(), text.trim());
            Ok(0)
        }),
    )
}

unsafe extern "C" fn download_attachment(
    ctx: Ctx,
    upload_path: *const c_char,
    cb: Option<StringCb>,
    user: UserData,
) -> i32 {
    // SAFETY: 插件传入以 0 结尾的字符串。
    let path = unsafe { read_text(upload_path) };
    run32(
        ctx,
        "download_attachment",
        Some(permission::READ_ATTACHMENT),
        Box::new(move |call| {
            let (Some(cb), Some(path)) = (cb, path.as_deref().and_then(request::upload_path))
            else {
                return invalid();
            };
            let local = call.wait(call.session()?.download_to_cache(&path, |_, _| {}))?;
            send_string(cb, &local.to_string_lossy(), user);
            Ok(0)
        }),
    )
}

unsafe extern "C" fn api_request(
    ctx: Ctx,
    method: *const c_char,
    path: *const c_char,
    params: *const c_char,
    cb: Option<StringCb>,
    user: UserData,
) -> i32 {
    // SAFETY: 插件传入以 0 结尾的字符串。
    let (method, path, params) = unsafe { (read_text(method), read_text(path), read_text(params)) };
    run32(
        ctx,
        "api_request",
        Some(permission::ZULIP_API),
        Box::new(move |call| {
            let path = path.unwrap_or_default();
            if !request::is_allowed_api_path(&path) {
                call.warn(format!("api_request：不允许访问 {path}"));
                return Err(Fail::Code(abi::ERR_PERMISSION));
            }
            let method = method.as_deref().and_then(request::api_method);
            let form = request::api_form(params.as_deref());
            let (Some(cb), Some(method), Some(form)) = (cb, method, form) else {
                return invalid();
            };
            call.plugin.add_log(
                abi::LOG_INFO,
                HOST_CATEGORY,
                &format!("api_request：{method} {path}"),
            );
            let reply = call.wait(call.session()?.client().request(
                method,
                &path,
                Some(&form),
                Some(API_TIMEOUT),
            ))?;
            send_string(cb, &reply.to_string(), user);
            Ok(0)
        }),
    )
}
