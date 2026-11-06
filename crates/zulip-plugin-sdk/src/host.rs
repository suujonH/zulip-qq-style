//! 宿主函数的安全封装：可以在任何线程调用，同步阻塞。

use std::ffi::{CString, c_char, c_void};
use std::sync::OnceLock;

use serde_json::{Map, Value, json};

use crate::abi::{
    self, HostApi, RawAccount, RawChannel, RawConversation, RawMessage, RawTopic, RawUser,
};
use crate::types::{Account, Channel, Conversation, Message, Topic, User, read_string};

// 传给宿主的会话：指针在它存活期间有效。
struct ConvArg {
    raw: RawConversation,
    _topic: Option<CString>,
}

impl ConvArg {
    fn new(conv: &Conversation) -> Self {
        let topic = match conv {
            Conversation::Channel { topic, .. } => Some(c_string(topic)),
            Conversation::Direct { .. } => None,
        };
        let (kind, stream_id, ids, count) = match conv {
            Conversation::Channel { stream_id, .. } => {
                (abi::CONV_CHANNEL, *stream_id, std::ptr::null(), 0)
            }
            Conversation::Direct { user_ids } => (
                abi::CONV_DIRECT,
                0,
                user_ids.as_ptr(),
                user_ids.len() as i32,
            ),
        };
        Self {
            raw: RawConversation {
                struct_size: size_of::<RawConversation>() as u32,
                kind,
                stream_id,
                stream_name: std::ptr::null(),
                topic: opt_ptr(&topic),
                dm_user_ids: ids,
                dm_user_count: count,
            },
            _topic: topic,
        }
    }
}

/// 宿主函数返回的错误码（`abi::ERR_*`）。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Error(pub i32);

impl std::fmt::Display for Error {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        let text = match self.0 {
            abi::ERR_INVALID_ARG => "参数错误",
            abi::ERR_PERMISSION => "没有权限",
            abi::ERR_NOT_LOGGED_IN => "没有登录",
            abi::ERR_NETWORK => "网络错误",
            abi::ERR_SERVER => "服务器返回错误",
            abi::ERR_NOT_FOUND => "不存在",
            abi::ERR_WRONG_CONTEXT => "不能在这里调用",
            _ => "未知错误",
        };
        write!(f, "{text}（{}）", self.0)
    }
}

impl std::error::Error for Error {}

pub type Result<T> = std::result::Result<T, Error>;

struct Installed {
    api: &'static HostApi,
    category: CString,
}

// SAFETY: 函数表不变，ctx 只原样传回；宿主函数可以在任何线程调用。
unsafe impl Send for Installed {}
// SAFETY: 同上。
unsafe impl Sync for Installed {}

static HOST: OnceLock<Installed> = OnceLock::new();

/// 检查版本并保存函数表。
///
/// # Safety
/// `api` 为空或指向一直有效的函数表。
pub(crate) unsafe fn install(api: *const HostApi, category: &str) -> bool {
    // SAFETY: 见函数说明。
    let Some(table) = (unsafe { api.as_ref() }) else {
        return false;
    };
    if table.api_version < abi::API_VERSION || (table.struct_size as usize) < size_of::<HostApi>() {
        return false;
    }
    HOST.get_or_init(|| Installed {
        api: table,
        category: c_string(category),
    });
    true
}

fn api() -> Result<&'static HostApi> {
    HOST.get()
        .map(|h| h.api)
        .ok_or(Error(abi::ERR_WRONG_CONTEXT))
}

fn c_string(text: &str) -> CString {
    CString::new(text.replace('\0', "")).unwrap_or_default()
}

fn code(result: i32) -> Result<()> {
    if result < 0 {
        Err(Error(result))
    } else {
        Ok(())
    }
}

fn id(result: i64) -> Result<i64> {
    if result < 0 {
        Err(Error(result as i32))
    } else {
        Ok(result)
    }
}

fn opt_ptr(text: &Option<CString>) -> *const c_char {
    text.as_ref().map_or(std::ptr::null(), |t| t.as_ptr())
}

unsafe extern "C" fn on_string(s: *const c_char, user: *mut c_void) {
    // SAFETY: user 是调用方传入的 &mut String，回调在调用期间同步执行。
    unsafe { *user.cast::<String>() = read_string(s) };
}

macro_rules! collect_cb {
    ($name:ident, $raw:ty, $ty:ty) => {
        unsafe extern "C" fn $name(raw: *const $raw, user: *mut c_void) {
            // SAFETY: user 是调用方传入的 &mut Vec，回调在调用期间同步执行；raw 在回调期间有效。
            unsafe { (*user.cast::<Vec<$ty>>()).push(<$ty>::from_raw(&*raw)) };
        }
    };
}

collect_cb!(on_message_item, RawMessage, Message);
collect_cb!(on_user_item, RawUser, User);
collect_cb!(on_channel_item, RawChannel, Channel);
collect_cb!(on_topic_item, RawTopic, Topic);
collect_cb!(on_account_item, RawAccount, Account);

/// 写插件日志。
pub fn log(level: i32, text: &str) {
    if let Some(host) = HOST.get() {
        let text = c_string(text);
        // SAFETY: 字符串在调用期间有效。
        unsafe {
            (host.api.add_log)(host.api.ctx, level, host.category.as_ptr(), text.as_ptr());
        }
    }
}

pub fn accounts() -> Result<Vec<Account>> {
    let api = api()?;
    let mut items: Vec<Account> = Vec::new();
    let user = (&raw mut items).cast();
    // SAFETY: 回调只在调用期间使用 items。
    code(unsafe { (api.get_accounts)(api.ctx, Some(on_account_item), user) })?;
    Ok(items)
}

pub fn current_account() -> Result<i64> {
    let api = api()?;
    // SAFETY: 没有指针参数。
    id(unsafe { (api.get_current_account)(api.ctx) })
}

/// 回调之外调用宿主函数前，切换到消息所属的账号。
pub fn select_account(account_id: i64) -> Result<()> {
    let api = api()?;
    // SAFETY: 没有指针参数。
    code(unsafe { (api.select_account)(api.ctx, account_id) })
}

/// 插件的数据目录（宿主创建，可读写），以 `\` 结尾。
pub fn data_dir() -> Result<String> {
    let api = api()?;
    let mut text = String::new();
    let user = (&raw mut text).cast();
    // SAFETY: 回调只在调用期间使用 text。
    code(unsafe { (api.get_data_dir)(api.ctx, Some(on_string), user) })?;
    Ok(text)
}

/// 插件设置（JSON 对象）；读取失败时为空对象。
pub fn settings() -> Value {
    let empty = || Value::Object(Map::new());
    let Ok(api) = api() else {
        return empty();
    };
    let mut text = String::new();
    let user = (&raw mut text).cast();
    // SAFETY: 回调只在调用期间使用 text。
    if unsafe { (api.get_settings)(api.ctx, Some(on_string), user) } < 0 {
        return empty();
    }
    serde_json::from_str(&text).unwrap_or_else(|_| empty())
}

/// 回复设置窗口，格式见头文件 `set_settings_feedback`。
pub fn settings_feedback(feedback: &Value) -> Result<()> {
    let api = api()?;
    let json = c_string(&feedback.to_string());
    // SAFETY: 字符串在调用期间有效。
    code(unsafe { (api.set_settings_feedback)(api.ctx, json.as_ptr()) })
}

/// 在设置项的下方显示文字；`row` 为按钮回调收到的行号。
pub fn settings_text(key: &str, row: Option<i64>, text: &str) -> Result<()> {
    let mut feedback = json!({"key": key, "text": text});
    if let Some(row) = row {
        feedback["row"] = json!(row);
    }
    settings_feedback(&feedback)
}

/// tags 设置项中某一项旁的按钮的结果；失败时 `error` 为原因。
pub fn settings_item_result(
    key: &str,
    row: Option<i64>,
    item: &str,
    error: Option<&str>,
) -> Result<()> {
    let status = if error.is_none() { "ok" } else { "error" };
    let mut feedback = json!({"key": key, "item": item, "status": status});
    if let Some(error) = error {
        feedback["text"] = json!(error);
    }
    if let Some(row) = row {
        feedback["row"] = json!(row);
    }
    settings_feedback(&feedback)
}

pub fn send_channel_message(stream_id: i64, topic: &str, markdown: &str) -> Result<i64> {
    let api = api()?;
    let topic = c_string(topic);
    let markdown = c_string(markdown);
    // SAFETY: 字符串在调用期间有效。
    id(
        unsafe {
            (api.send_channel_message)(api.ctx, stream_id, topic.as_ptr(), markdown.as_ptr())
        },
    )
}

pub fn send_direct_message(user_ids: &[i64], markdown: &str) -> Result<i64> {
    let api = api()?;
    let markdown = c_string(markdown);
    // SAFETY: 数组和字符串在调用期间有效。
    id(unsafe {
        (api.send_direct_message)(
            api.ctx,
            user_ids.as_ptr(),
            user_ids.len() as i32,
            markdown.as_ptr(),
        )
    })
}

pub fn message(message_id: i64) -> Result<Message> {
    let api = api()?;
    let mut items: Vec<Message> = Vec::new();
    let user = (&raw mut items).cast();
    // SAFETY: 回调只在调用期间使用 items。
    code(unsafe { (api.get_message)(api.ctx, message_id, Some(on_message_item), user) })?;
    items.pop().ok_or(Error(abi::ERR_NOT_FOUND))
}

/// 取消息。`conv` 为 `None` 表示全部；`anchor`：0 = 最新，-1 = 最旧，其它 = 消息 id（含它本身）。
pub fn messages(
    conv: Option<&Conversation>,
    anchor: i64,
    num_before: i32,
    num_after: i32,
) -> Result<Vec<Message>> {
    let api = api()?;
    let mut items: Vec<Message> = Vec::new();
    let user = (&raw mut items).cast();
    let raw = conv.map(ConvArg::new);
    let raw_ptr = raw.as_ref().map_or(std::ptr::null(), |r| &raw const r.raw);
    // SAFETY: raw 指向的内容和回调用到的 items 在调用期间有效。
    code(unsafe {
        (api.get_messages)(
            api.ctx,
            raw_ptr,
            anchor,
            num_before,
            num_after,
            Some(on_message_item),
            user,
        )
    })?;
    Ok(items)
}

/// 这条消息之前的 `count` 条消息（不含它），按时间先后。需要 read_message。
pub fn context_messages(message: &Message, count: usize) -> Result<Vec<Message>> {
    if count == 0 {
        return Ok(Vec::new());
    }
    let mut items = messages(Some(&message.conv), message.id, count as i32, 0)?;
    items.retain(|m| m.id != message.id);
    Ok(items)
}

pub fn users() -> Result<Vec<User>> {
    let api = api()?;
    let mut items: Vec<User> = Vec::new();
    let user = (&raw mut items).cast();
    // SAFETY: 回调只在调用期间使用 items。
    code(unsafe { (api.get_users)(api.ctx, Some(on_user_item), user) })?;
    Ok(items)
}

pub fn user(user_id: i64) -> Result<User> {
    let api = api()?;
    let mut items: Vec<User> = Vec::new();
    let user = (&raw mut items).cast();
    // SAFETY: 回调只在调用期间使用 items。
    code(unsafe { (api.get_user)(api.ctx, user_id, Some(on_user_item), user) })?;
    items.pop().ok_or(Error(abi::ERR_NOT_FOUND))
}

pub fn channels() -> Result<Vec<Channel>> {
    let api = api()?;
    let mut items: Vec<Channel> = Vec::new();
    let user = (&raw mut items).cast();
    // SAFETY: 回调只在调用期间使用 items。
    code(unsafe { (api.get_channels)(api.ctx, Some(on_channel_item), user) })?;
    Ok(items)
}

pub fn topics(stream_id: i64) -> Result<Vec<Topic>> {
    let api = api()?;
    let mut items: Vec<Topic> = Vec::new();
    let user = (&raw mut items).cast();
    // SAFETY: 回调只在调用期间使用 items。
    code(unsafe { (api.get_topics)(api.ctx, stream_id, Some(on_topic_item), user) })?;
    Ok(items)
}

pub fn add_reaction(message_id: i64, emoji_name: &str) -> Result<()> {
    let api = api()?;
    let name = c_string(emoji_name);
    // SAFETY: 字符串在调用期间有效。
    code(unsafe { (api.add_reaction)(api.ctx, message_id, name.as_ptr()) })
}

pub fn set_starred(message_id: i64, starred: bool) -> Result<()> {
    let api = api()?;
    // SAFETY: 没有指针参数。
    code(unsafe { (api.set_starred)(api.ctx, message_id, i32::from(starred)) })
}

pub fn mark_read(message_id: i64) -> Result<()> {
    let api = api()?;
    // SAFETY: 没有指针参数。
    code(unsafe { (api.mark_read)(api.ctx, message_id) })
}

/// 上传文件，返回 `/user_uploads/...`。
pub fn upload_file(local_path: &str) -> Result<String> {
    let api = api()?;
    let path = c_string(local_path);
    let mut text = String::new();
    let user = (&raw mut text).cast();
    // SAFETY: 字符串在调用期间有效；回调只在调用期间使用 text。
    code(unsafe { (api.upload_file)(api.ctx, path.as_ptr(), Some(on_string), user) })?;
    Ok(text)
}

/// 改写正在发送的内容，只在 `filter_outgoing` 回调内有效。
pub fn set_outgoing_content(markdown: &str) -> Result<()> {
    let api = api()?;
    let markdown = c_string(markdown);
    // SAFETY: 字符串在调用期间有效。
    code(unsafe { (api.set_outgoing_content)(api.ctx, markdown.as_ptr()) })
}

/// 设置消息的附加内容，格式见头文件 `set_annotation`。
pub fn set_annotation(message_id: i64, annotation: &Value) -> Result<()> {
    let api = api()?;
    let json = c_string(&annotation.to_string());
    // SAFETY: 字符串在调用期间有效。
    code(unsafe { (api.set_annotation)(api.ctx, message_id, json.as_ptr()) })
}

/// 清除本插件在这条消息上的附加内容。
pub fn clear_annotation(message_id: i64) -> Result<()> {
    let api = api()?;
    // SAFETY: 空指针表示清除。
    code(unsafe { (api.set_annotation)(api.ctx, message_id, std::ptr::null()) })
}

pub fn launch(target: &str, args: Option<&str>) -> Result<()> {
    let api = api()?;
    let target = c_string(target);
    let args = args.map(c_string);
    // SAFETY: 字符串在调用期间有效。
    code(unsafe { (api.launch)(api.ctx, target.as_ptr(), opt_ptr(&args)) })
}

pub fn edit_message(message_id: i64, markdown: &str) -> Result<()> {
    let api = api()?;
    let markdown = c_string(markdown);
    // SAFETY: 字符串在调用期间有效。
    code(unsafe { (api.edit_message)(api.ctx, message_id, markdown.as_ptr()) })
}

pub fn delete_message(message_id: i64) -> Result<()> {
    let api = api()?;
    // SAFETY: 没有指针参数。
    code(unsafe { (api.delete_message)(api.ctx, message_id) })
}

pub fn update_channel(stream_id: i64, name: Option<&str>, description: Option<&str>) -> Result<()> {
    let api = api()?;
    let name = name.map(c_string);
    let description = description.map(c_string);
    // SAFETY: 字符串在调用期间有效。
    code(unsafe { (api.update_channel)(api.ctx, stream_id, opt_ptr(&name), opt_ptr(&description)) })
}

/// `role` 为 0 表示不修改。
pub fn update_user(user_id: i64, full_name: Option<&str>, role: i32) -> Result<()> {
    let api = api()?;
    let name = full_name.map(c_string);
    // SAFETY: 字符串在调用期间有效。
    code(unsafe { (api.update_user)(api.ctx, user_id, opt_ptr(&name), role) })
}

/// 发布资源，替换之前发布的；空数组表示清除。
pub fn publish_resources(kind: &str, resources: &Value) -> Result<()> {
    let api = api()?;
    let kind = c_string(kind);
    let json = c_string(&resources.to_string());
    // SAFETY: 字符串在调用期间有效。
    code(unsafe { (api.publish_resources)(api.ctx, kind.as_ptr(), json.as_ptr()) })
}

/// 使用其他插件的资源（工作线程，阻塞到回复或超时）。
pub fn request_resource(resource: &Value, request: &Value) -> Result<String> {
    let api = api()?;
    let json = c_string(&json!({"resource": resource, "request": request}).to_string());
    let mut text = String::new();
    let user = (&raw mut text).cast();
    // SAFETY: 字符串在调用期间有效；回调只在调用期间使用 text。
    code(unsafe { (api.request_resource)(api.ctx, json.as_ptr(), Some(on_string), user) })?;
    Ok(text)
}

/// 回复其他插件的资源请求。
pub fn reply_resource(request_id: i64, reply: &Value) -> Result<()> {
    let api = api()?;
    let json = c_string(&reply.to_string());
    // SAFETY: 字符串在调用期间有效。
    code(unsafe { (api.reply_resource)(api.ctx, request_id, json.as_ptr()) })
}

/// 回复面板请求（只能在 `on_panel` 回调中调用），格式见头文件 `reply_panel`。
pub fn reply_panel(reply: &Value) -> Result<()> {
    let api = api()?;
    let json = c_string(&reply.to_string());
    // SAFETY: 字符串在调用期间有效。
    code(unsafe { (api.reply_panel)(api.ctx, json.as_ptr()) })
}

// 带字符串回调的调用，返回回调收到的字符串。
fn string_call(call: impl FnOnce(*mut c_void) -> i32) -> Result<String> {
    let mut text = String::new();
    code(call((&raw mut text).cast()))?;
    Ok(text)
}

fn json_call(call: impl FnOnce(*mut c_void) -> i32) -> Result<Value> {
    let text = string_call(call)?;
    serde_json::from_str(&text).map_err(|_| Error(abi::ERR_UNKNOWN))
}

/// 一页搜索结果。
#[derive(Clone, Debug, Default)]
pub struct SearchPage {
    /// 新的在前。
    pub messages: Vec<Message>,
    /// 下一页的 `cursor`；到头时为 `None`。
    pub next_cursor: Option<i64>,
}

/// 搜索消息，`query` 的格式见头文件 `search_messages`。
pub fn search_messages(query: &Value) -> Result<SearchPage> {
    let api = api()?;
    let json = c_string(&query.to_string());
    let mut items: Vec<Message> = Vec::new();
    let mut next = 0i64;
    let user = (&raw mut items).cast();
    // SAFETY: 字符串和 next 在调用期间有效；回调只在调用期间使用 items。
    code(unsafe {
        (api.search_messages)(
            api.ctx,
            json.as_ptr(),
            &raw mut next,
            Some(on_message_item),
            user,
        )
    })?;
    Ok(SearchPage {
        messages: items,
        next_cursor: (next > 0).then_some(next),
    })
}

/// 消息页的会话和未读数，格式见头文件 `get_conversations`。
pub fn conversations() -> Result<Value> {
    let api = api()?;
    // SAFETY: 回调只在调用期间使用 text。
    json_call(|user| unsafe { (api.get_conversations)(api.ctx, Some(on_string), user) })
}

/// 用户当前打开的会话；没有时为 `None`。
pub fn current_conversation() -> Result<Option<Conversation>> {
    let api = api()?;
    // SAFETY: 回调只在调用期间使用 text。
    let result =
        json_call(|user| unsafe { (api.get_current_conversation)(api.ctx, Some(on_string), user) });
    match result {
        Ok(value) => Ok(Conversation::from_json(&value)),
        Err(Error(abi::ERR_NOT_FOUND)) => Ok(None),
        Err(error) => Err(error),
    }
}

/// 在线状态和签名，`user_id` 为 0 时是所有活跃用户，格式见头文件 `get_presence`。
pub fn presence(user_id: i64) -> Result<Value> {
    let api = api()?;
    // SAFETY: 回调只在调用期间使用 text。
    json_call(|user| unsafe { (api.get_presence)(api.ctx, user_id, Some(on_string), user) })
}

/// 用户组，格式见头文件 `get_user_groups`。
pub fn user_groups() -> Result<Value> {
    let api = api()?;
    // SAFETY: 回调只在调用期间使用 text。
    json_call(|user| unsafe { (api.get_user_groups)(api.ctx, Some(on_string), user) })
}

/// 组织内可见的全部频道（含未订阅）。
pub fn all_channels() -> Result<Vec<Channel>> {
    let api = api()?;
    let mut items: Vec<Channel> = Vec::new();
    let user = (&raw mut items).cast();
    // SAFETY: 回调只在调用期间使用 items。
    code(unsafe { (api.get_all_channels)(api.ctx, Some(on_channel_item), user) })?;
    Ok(items)
}

/// 话题的关注状态（`abi::TOPIC_*`）。
pub fn topic_visibility(stream_id: i64, topic: &str) -> Result<i32> {
    let api = api()?;
    let topic = c_string(topic);
    // SAFETY: 字符串在调用期间有效。
    let result = unsafe { (api.get_topic_visibility)(api.ctx, stream_id, topic.as_ptr()) };
    code(result)?;
    Ok(result)
}

/// 服务器上的编辑历史，格式见头文件 `get_edit_history`。
pub fn edit_history(message_id: i64) -> Result<Value> {
    let api = api()?;
    // SAFETY: 回调只在调用期间使用 text。
    json_call(|user| unsafe { (api.get_edit_history)(api.ctx, message_id, Some(on_string), user) })
}

pub fn remove_reaction(message_id: i64, emoji_name: &str) -> Result<()> {
    let api = api()?;
    let name = c_string(emoji_name);
    // SAFETY: 字符串在调用期间有效。
    code(unsafe { (api.remove_reaction)(api.ctx, message_id, name.as_ptr()) })
}

/// 从这条消息开始，会话中之后的消息全部标记为未读。
pub fn mark_unread(message_id: i64) -> Result<()> {
    let api = api()?;
    // SAFETY: 没有指针参数。
    code(unsafe { (api.mark_unread)(api.ctx, message_id) })
}

/// 移动或改名话题；`new_stream_id` 为 0 表示同一频道。
pub fn move_topic(stream_id: i64, topic: &str, new_stream_id: i64, new_topic: &str) -> Result<()> {
    let api = api()?;
    let topic = c_string(topic);
    let new_topic = c_string(new_topic);
    // SAFETY: 字符串在调用期间有效。
    code(unsafe {
        (api.move_topic)(
            api.ctx,
            stream_id,
            topic.as_ptr(),
            new_stream_id,
            new_topic.as_ptr(),
        )
    })
}

pub fn set_topic_resolved(stream_id: i64, topic: &str, resolved: bool) -> Result<()> {
    let api = api()?;
    let topic = c_string(topic);
    // SAFETY: 字符串在调用期间有效。
    code(unsafe {
        (api.set_topic_resolved)(api.ctx, stream_id, topic.as_ptr(), i32::from(resolved))
    })
}

pub fn delete_topic(stream_id: i64, topic: &str) -> Result<()> {
    let api = api()?;
    let topic = c_string(topic);
    // SAFETY: 字符串在调用期间有效。
    code(unsafe { (api.delete_topic)(api.ctx, stream_id, topic.as_ptr()) })
}

/// 让界面切换到会话，`message_id` 不为空时定位到消息。
pub fn open_conversation(conv: &Conversation, message_id: Option<i64>) -> Result<()> {
    let api = api()?;
    let arg = ConvArg::new(conv);
    // SAFETY: arg 在调用期间有效。
    code(unsafe {
        (api.open_conversation)(api.ctx, &raw const arg.raw, message_id.unwrap_or_default())
    })
}

/// 把文字填入当前会话的输入框（不发送）；`replace` 时替换已有内容，否则插入到光标处。
pub fn fill_composer(text: &str, replace: bool) -> Result<()> {
    let api = api()?;
    let text = c_string(text);
    let mode = if replace {
        abi::FILL_REPLACE
    } else {
        abi::FILL_INSERT
    };
    // SAFETY: 字符串在调用期间有效。
    code(unsafe { (api.fill_composer)(api.ctx, text.as_ptr(), mode) })
}

/// 定时发送，`deliver_at` 为 Unix 秒，返回定时消息 id。
pub fn schedule_message(conv: &Conversation, markdown: &str, deliver_at: i64) -> Result<i64> {
    let api = api()?;
    let arg = ConvArg::new(conv);
    let markdown = c_string(markdown);
    // SAFETY: arg 和字符串在调用期间有效。
    id(unsafe {
        (api.schedule_message)(api.ctx, &raw const arg.raw, markdown.as_ptr(), deliver_at)
    })
}

pub fn show_toast(text: &str) -> Result<()> {
    let api = api()?;
    let text = c_string(text);
    // SAFETY: 字符串在调用期间有效。
    code(unsafe { (api.show_toast)(api.ctx, text.as_ptr()) })
}

pub fn show_notification(title: &str, body: &str) -> Result<()> {
    let api = api()?;
    let title = c_string(title);
    let body = c_string(body);
    // SAFETY: 字符串在调用期间有效。
    code(unsafe { (api.show_notification)(api.ctx, title.as_ptr(), body.as_ptr()) })
}

/// 状态栏提示；空字符串表示清除。
pub fn set_status_text(text: &str) -> Result<()> {
    let api = api()?;
    let text = c_string(text);
    // SAFETY: 字符串在调用期间有效。
    code(unsafe { (api.set_status_text)(api.ctx, text.as_ptr()) })
}

/// 下载附件（`/user_uploads/...`）到缓存，返回本地路径。
pub fn download_attachment(upload_path: &str) -> Result<String> {
    let api = api()?;
    let path = c_string(upload_path);
    // SAFETY: 字符串在调用期间有效；回调只在调用期间使用 text。
    string_call(|user| unsafe {
        (api.download_attachment)(api.ctx, path.as_ptr(), Some(on_string), user)
    })
}

/// 以当前账号调用 Zulip API，`path` 必须在 `/api/v1/` 下；返回服务器的 JSON 回复。
pub fn api_request(method: &str, path: &str, params: &Value) -> Result<Value> {
    let api = api()?;
    let method = c_string(method);
    let path = c_string(path);
    let params = c_string(&params.to_string());
    // SAFETY: 字符串在调用期间有效；回调只在调用期间使用 text。
    json_call(|user| unsafe {
        (api.api_request)(
            api.ctx,
            method.as_ptr(),
            path.as_ptr(),
            params.as_ptr(),
            Some(on_string),
            user,
        )
    })
}
