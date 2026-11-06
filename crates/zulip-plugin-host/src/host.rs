//! 插件宿主。插件随程序启动加载（登录之前），账号登录 / 结束时通知插件，
//! 新消息、收到的消息过滤、发送前过滤按优先级依次调用插件（事件链）。
//! 公开方法可以在任何线程调用；不会同步等待插件线程。

use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Weak};
use std::time::{Duration, Instant};

use parking_lot::{Mutex, RwLock};
use serde_json::{Map, Value, json};
use tokio::runtime::Handle;
use tokio::sync::{broadcast, oneshot};
use zulip_core::ConversationKey;
use zulip_core::html::to_plain_text;
use zulip_core::model::ChatMessage;
use zulip_plugin_sdk::abi;
use zulip_session::{
    ConnectionState, HookEvent, HookFuture, Hub, OutgoingKind, Session, SessionHooks,
};
use zulip_store::{AppPaths, Db, PluginRecord, PluginRepo, SettingRepo};

use crate::annotation::{ActionStyle, AnnotationStore, PluginAction, PluginAnnotation};
use crate::event::PluginEvent;
use crate::fingerprint;
use crate::host_api::HOST_CATEGORY;
use crate::launch;
use crate::manifest::{
    ComposerIcon, FILTER_INCOMING, FILTER_OUTGOING, MONITOR_PRIORITY, ON_CHANNEL_CHANGED,
    ON_CONNECTION_CHANGED, ON_CONVERSATION_OPENED, ON_MESSAGE, ON_MESSAGE_DELETED,
    ON_MESSAGE_EDITED, ON_PRESENCE_CHANGED, ON_REACTION_CHANGED, ON_READ_STATE_CHANGED,
    ON_TOPIC_MOVED, ON_USER_CHANGED, PluginManifest, event_permission, event_title,
};
use crate::markup::{markdown_html, plain_text_html};
use crate::marshal::{Conversation, MessageSnapshot, conversation_json};
use crate::panel::{PanelRequest, PluginTarget, PluginTargetKind};
use crate::permission::{self, PermissionGroup};
use crate::plugin::{NativePlugin, approval_meta};
use crate::resources::{self, PluginResource, ResourceSelection, Resources};
use crate::setting_field::PluginSettingField;
use crate::settings;
use crate::shortcuts::{self, Bindings};
use crate::thread::{self, PluginThread};

/// 事件链和面板最多等待的时间。
pub const CHAIN_TIMEOUT: Duration = Duration::from_secs(2);

const HOST_API_VERSION: u32 = abi::API_VERSION;
const EVENT_CAPACITY: usize = 256;

#[derive(Debug, thiserror::Error)]
pub enum PluginError {
    #[error(transparent)]
    Store(#[from] zulip_store::StoreError),
}

/// 发送前过滤没有通过；`to_string()` 可以直接显示给用户。
#[derive(Clone, Copy, Debug, PartialEq, Eq, thiserror::Error)]
pub enum OutgoingRejected {
    #[error("消息已被插件拦截")]
    Blocked,
    #[error("消息已被插件清空")]
    Emptied,
    #[error("消息过长")]
    TooLong,
}

/// 切换开关的结果。
#[derive(Clone, Debug)]
pub enum ToggleOutcome {
    /// 已经切换。
    Done,
    /// 需要用户确认，确认后调用 [`PluginHost::approve`]。
    NeedsApproval(Box<ApprovalInfo>),
    /// 依赖的插件未开启或未安装（名称）。
    MissingDependencies(Vec<String>),
    /// 无法读取插件文件。
    Unreadable,
}

impl ToggleOutcome {
    /// 需要提示用户的文字。
    pub fn message(&self) -> Option<String> {
        match self {
            Self::MissingDependencies(names) => {
                Some(format!("请先开启依赖的插件：{}", names.join("、")))
            }
            Self::Unreadable => Some("无法读取插件文件".to_owned()),
            _ => None,
        }
    }
}

/// 开启确认的内容：插件可以读取什么、可以做什么、会自动打开哪些链接 / 程序。
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ApprovalInfo {
    pub title: String,
    pub meta: String,
    /// 插件更新后的提示。
    pub notice: Option<String>,
    pub reads: Vec<String>,
    pub acts: Vec<String>,
    pub declared: Vec<String>,
    pub events: Vec<String>,
    pub launch_rules: Vec<String>,
    pub dependencies: Vec<String>,
    /// 指纹的短格式。
    pub fingerprint: String,
}

/// 快捷操作的一行。
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ShortcutRow {
    pub key: String,
    /// “插件名 · 菜单项”。
    pub title: String,
    /// 鼠标手势（消息菜单）；否则为键盘快捷键（插件菜单）。
    pub is_mouse: bool,
    /// 当前绑定（空字符串为不绑定）。
    pub gesture: String,
}

impl ShortcutRow {
    /// 显示用的绑定。
    pub fn label(&self) -> String {
        if self.is_mouse {
            shortcuts::mouse_label(&self.gesture)
        } else if self.gesture.is_empty() {
            "无".to_owned()
        } else {
            self.gesture.clone()
        }
    }
}

/// resource 设置项的一个选项；`selection` 为 `None` 表示不使用。
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ResourceOption {
    pub selection: Option<ResourceSelection>,
    pub label: String,
    pub available: bool,
}

/// 选择对象时列表的一项。
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct TargetCandidate {
    pub target: PluginTarget,
    pub title: String,
    pub subtitle: String,
}

/// 附加内容中的按钮。
#[derive(Clone, Debug)]
pub struct ActionView {
    pub plugin: Arc<NativePlugin>,
    pub action: PluginAction,
    pub label: String,
    pub style: ActionStyle,
    /// 填入的文字，或完整的目标（程序带参数）。
    pub detail: String,
    pub tooltip: String,
    /// 插件仍然启用且声明了对应的权限。
    pub can_run: bool,
}

/// 点击按钮后调用方要做的事。
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum ActionOutcome {
    /// 把文字填入输入框（不发送）。
    Fill(String),
    /// 已经打开，或不能运行。
    Done,
}

/// 气泡下方一个插件附加的内容。
#[derive(Clone, Debug)]
pub struct AnnotationCard {
    pub plugin_id: String,
    pub plugin_name: String,
    pub text: String,
    /// `text` 按受限 Markdown 渲染的安全 HTML。
    pub text_html: String,
    pub is_collapsed: bool,
    pub toggle_tooltip: String,
    pub actions: Vec<ActionView>,
}

/// 插件替换气泡内容。
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct RenderedContent {
    pub plugin_name: String,
    pub html: String,
    /// 用户选择了显示原文。
    pub original_shown: bool,
}

/// 一条消息上要显示的附加内容（只含已开启的插件）。
#[derive(Clone, Debug, Default)]
pub struct MessageAnnotations {
    pub rendered: Option<RenderedContent>,
    pub cards: Vec<AnnotationCard>,
}

/// 右键菜单的种类。
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum ContextMenuKind {
    Conversation,
    User,
    Channel,
}

/// 右键菜单的对象。
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum ContextMenuTarget {
    Conversation(ConversationKey),
    User(i64),
    Channel(i64),
}

impl ContextMenuTarget {
    pub fn kind(&self) -> ContextMenuKind {
        match self {
            Self::Conversation(_) => ContextMenuKind::Conversation,
            Self::User(_) => ContextMenuKind::User,
            Self::Channel(_) => ContextMenuKind::Channel,
        }
    }

    fn to_json(&self, session: &Session) -> Value {
        let (kind, target) = match self {
            Self::Conversation(key) => ("conversation", conversation_json(Some(key), session)),
            Self::User(id) => (
                "user",
                json!({"user": id, "name": session.display_name(*id)}),
            ),
            Self::Channel(id) => (
                "channel",
                json!({"stream": id, "name": session.channel_display_name(*id)}),
            ),
        };
        json!({"account": session.account_id(), "kind": kind, "target": target})
    }
}

/// 输入框工具栏的一个按钮。`icon` 为 [`ComposerIcon::Svg`] 时路径已是绝对路径。
#[derive(Clone, Debug)]
pub struct ComposerButton {
    pub plugin: Arc<NativePlugin>,
    pub id: i32,
    pub icon: ComposerIcon,
    pub tooltip: String,
}

/// 主窗口连接提示位置显示的插件文字。
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct PluginStatusText {
    pub plugin_name: String,
    pub text: String,
}

/// 设置中的按钮被点击（值尚未保存）。
#[derive(Clone, Debug, Default)]
pub struct SettingsButtonClick {
    pub key: String,
    /// list 的行号。
    pub row: Option<i64>,
    /// tags 的项（item_button）。
    pub item: Option<String>,
    /// 所在一层当前的值。
    pub values: Map<String, Value>,
    /// 全部当前的值。
    pub settings: Map<String, Value>,
}

/// [`PluginHost::filter_outgoing`] 的结果可以免过滤发送的时限。
pub const PREFILTER_LIMIT: Duration = Duration::from_secs(30);

pub struct PluginHostConfig {
    pub plugins_dir: PathBuf,
    pub db: Db,
    pub paths: AppPaths,
    /// 宿主函数用它等待网络请求。必须是多线程运行时。
    pub runtime: Handle,
}

/// 插件安装目录：程序目录下的 plugins。
pub fn default_plugins_dir() -> PathBuf {
    std::env::current_exe()
        .ok()
        .and_then(|exe| exe.parent().map(|dir| dir.join("plugins")))
        .unwrap_or_else(|| PathBuf::from("plugins"))
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum ChainResult {
    Ignore,
    Block,
    TimedOut,
}

// 事件链的超时记账。
#[derive(Default)]
struct ChainState {
    // 插件线程上正在调用的插件（超时时用于定位）。
    current: Mutex<Option<Arc<NativePlugin>>>,
    current_call: AtomicU64,
    // 排在同一个慢调用后面的多条事件链只计一次超时。
    charged_call: AtomicU64,
    // 已经超时、仍在插件线程上执行的事件链：期间新的事件直接按 IGNORE 处理，避免越积越多。
    stuck: Mutex<Option<u64>>,
    next_chain: AtomicU64,
}

// 已投递到插件线程的事件链。
struct StartedChain<S> {
    reply: oneshot::Receiver<(ChainResult, S)>,
    cancel: Arc<AtomicBool>,
}

pub struct PluginHost {
    plugins_dir: PathBuf,
    db: Db,
    paths: AppPaths,
    runtime: Handle,
    weak: Weak<PluginHost>,
    thread: PluginThread,
    plugins: RwLock<Vec<Arc<NativePlugin>>>,
    // 已登录的会话（按登录顺序，第一个是插件的默认账号）。
    sessions: RwLock<Vec<Arc<Session>>>,
    events: broadcast::Sender<PluginEvent>,
    annotations: AnnotationStore,
    resources: Resources,
    shortcuts: Mutex<Bindings>,
    chain: ChainState,
    // 插件的状态栏提示（插件 id，文字），最后设置的在后。
    status_texts: Mutex<Vec<(String, String)>>,
    // filter_outgoing 通过的内容，紧接着的发送不再过滤一次。
    prefiltered: Mutex<Vec<Prefiltered>>,
}

struct Prefiltered {
    account_id: i64,
    key: ConversationKey,
    content: String,
    at: Instant,
}

impl PluginHost {
    pub fn new(config: PluginHostConfig) -> Arc<Self> {
        let (events, _) = broadcast::channel(EVENT_CAPACITY);
        let shortcuts = shortcuts::load(&SettingRepo::app(&config.db));
        Arc::new_cyclic(|weak| Self {
            plugins_dir: config.plugins_dir,
            db: config.db,
            paths: config.paths,
            runtime: config.runtime,
            weak: weak.clone(),
            thread: PluginThread::start(),
            plugins: RwLock::new(Vec::new()),
            sessions: RwLock::new(Vec::new()),
            events,
            annotations: AnnotationStore::default(),
            resources: Resources::default(),
            shortcuts: Mutex::new(shortcuts),
            chain: ChainState::default(),
            status_texts: Mutex::new(Vec::new()),
            prefiltered: Mutex::new(Vec::new()),
        })
    }

    /// 接入会话层。要在账号登录之前调用。
    pub fn attach(self: &Arc<Self>, hub: &Hub) {
        hub.set_hooks(self.clone());
    }

    pub fn plugins_dir(&self) -> &std::path::Path {
        &self.plugins_dir
    }

    pub fn subscribe(&self) -> broadcast::Receiver<PluginEvent> {
        self.events.subscribe()
    }

    pub(crate) fn emit(&self, event: PluginEvent) {
        let _ = self.events.send(event);
    }

    pub(crate) fn runtime(&self) -> &Handle {
        &self.runtime
    }

    pub(crate) fn plugin_data_dir(&self, id: &str) -> PathBuf {
        self.paths.plugin_data_dir(id)
    }

    pub(crate) fn is_plugin_thread(&self) -> bool {
        self.thread.is_current()
    }

    pub fn plugins(&self) -> Vec<Arc<NativePlugin>> {
        self.plugins.read().clone()
    }

    pub fn plugin(&self, id: &str) -> Option<Arc<NativePlugin>> {
        self.plugins.read().iter().find(|p| p.id() == id).cloned()
    }

    /// 已登录的会话。
    pub fn sessions(&self) -> Vec<Arc<Session>> {
        self.sessions.read().clone()
    }

    fn session(&self, account_id: i64) -> Option<Arc<Session>> {
        self.sessions
            .read()
            .iter()
            .find(|s| s.account_id() == account_id)
            .cloned()
    }

    // 插件 select_account 选择的账号，没有选择或已退出时为最先登录的账号。
    fn default_session(&self, plugin: &NativePlugin) -> Option<Arc<Session>> {
        let sessions = self.sessions.read();
        let selected = plugin.selected_account();
        sessions
            .iter()
            .find(|s| s.account_id() == selected)
            .or_else(|| sessions.first())
            .cloned()
    }

    // 插件线程上的回调只使用它所属的会话；回调之外使用默认会话。
    pub(crate) fn caller_session(&self, plugin: &NativePlugin) -> Option<Arc<Session>> {
        let in_callback = self.is_plugin_thread().then(thread::call_session).flatten();
        in_callback.or_else(|| self.default_session(plugin))
    }

    /// 扫描插件目录并加载已启用、且文件与用户确认开启时一致的插件；新插件默认停用。
    /// 所有插件加载完成后返回。只能调用一次。
    pub async fn load_all(&self) -> Result<(), PluginError> {
        let Ok(entries) = std::fs::read_dir(&self.plugins_dir) else {
            return Ok(());
        };
        let mut directories: Vec<PathBuf> = entries
            .filter_map(|e| e.ok())
            .map(|e| e.path())
            .filter(|p| p.is_dir())
            .collect();
        directories.sort_by_key(|d| d.to_string_lossy().to_lowercase());

        let records = self.db.call(|db| PluginRepo::new(db).load_all()).await?;
        let settings_repo = SettingRepo::app(&self.db);
        let mut to_load = Vec::new();
        for directory in directories {
            let plugin = Arc::new(self.read_plugin(directory, &records));
            if let Some(manifest) = plugin.manifest() {
                plugin.set_settings_json(
                    Value::Object(settings::load(&settings_repo, manifest)).to_string(),
                );
                if plugin.needs_reapproval() {
                    plugin.add_log(
                        abi::LOG_WARNING,
                        HOST_CATEGORY,
                        "插件文件与上次确认开启时不同，需要重新开启",
                    );
                }
                if plugin.is_approved() && records.get(&manifest.id).is_some_and(|r| r.enabled) {
                    plugin.set_enabled(true);
                    to_load.push(plugin.clone());
                }
            }
            self.plugins.write().push(plugin);
        }
        self.emit(PluginEvent::PluginsChanged);
        for plugin in to_load {
            self.load(&plugin).await;
        }
        Ok(())
    }

    fn read_plugin(
        &self,
        directory: PathBuf,
        records: &std::collections::HashMap<String, PluginRecord>,
    ) -> NativePlugin {
        let name = directory
            .file_name()
            .map(|n| n.to_string_lossy().into_owned())
            .unwrap_or_default();
        let invalid = |directory, error: String| {
            log::warn!("插件清单无效：{name}：{error}");
            NativePlugin::new(
                directory,
                None,
                Some(error),
                None,
                None,
                self.events.clone(),
            )
        };
        let text = match std::fs::read_to_string(directory.join("plugin.json")) {
            Ok(text) => text,
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => {
                return invalid(directory, "缺少 plugin.json".to_owned());
            }
            Err(e) => return invalid(directory, e.to_string()),
        };
        let mut warnings = Vec::new();
        let manifest = match PluginManifest::parse(&text, &name, HOST_API_VERSION, &mut |w| {
            warnings.push(w)
        }) {
            Ok(manifest) => manifest,
            Err(e) => return invalid(directory, e.to_string()),
        };
        let fingerprint = fingerprint::compute(&directory)
            .inspect_err(|e| log::warn!("无法读取插件文件：{name}：{e}"))
            .ok();
        let approved = records
            .get(&manifest.id)
            .and_then(|r| r.fingerprint.clone());
        let plugin = NativePlugin::new(
            directory,
            Some(manifest),
            None,
            fingerprint,
            approved,
            self.events.clone(),
        );
        for warning in warnings {
            plugin.add_log(abi::LOG_WARNING, HOST_CATEGORY, &warning);
        }
        plugin
    }

    async fn load(&self, plugin: &Arc<NativePlugin>) {
        let Some(host) = self.weak.upgrade() else {
            return;
        };
        let target = plugin.clone();
        let done = self
            .thread
            .run(self.default_session(plugin), move || {
                target.load(host.weak.clone());
                host.activate(&target);
            })
            .await;
        if done.is_err() {
            log::warn!("加载插件失败：{}", plugin.id());
        }
        self.emit(PluginEvent::PluginsChanged);
    }

    // 插件线程上：至少有一个账号登录且插件已启用、已加载时调用 on_enable。
    fn activate(&self, plugin: &Arc<NativePlugin>) {
        let logged_in = thread::call_session()
            .is_some_and(|s| self.sessions.read().iter().any(|x| Arc::ptr_eq(x, &s)));
        if logged_in && plugin.is_enabled() && plugin.is_loaded() && !plugin.is_active() {
            plugin.set_active(true);
            self.invoke(plugin, || plugin.call_enable());
        }
    }

    // 插件线程上：收到过 on_enable 的插件调用 on_disable。
    fn deactivate(&self, plugin: &Arc<NativePlugin>) {
        if plugin.is_active() {
            plugin.set_active(false);
            self.invoke(plugin, || plugin.call_disable());
        }
    }

    // 插件线程上：调用一个导出函数，记录正在执行的插件。
    // 只有在超时时间内返回才清零超时计数：每次都超时但最终返回的插件仍会被标记为无响应。
    fn invoke<R>(&self, plugin: &Arc<NativePlugin>, call: impl FnOnce() -> R) -> R {
        self.chain.current_call.fetch_add(1, Ordering::AcqRel);
        *self.chain.current.lock() = Some(plugin.clone());
        let start = Instant::now();
        let result = call();
        if start.elapsed() < CHAIN_TIMEOUT {
            plugin.on_returned();
        }
        *self.chain.current.lock() = None;
        result
    }

    /// 切换开关。依赖未开启、需要确认时不切换，由调用方提示或显示确认后再调用 [`approve`](Self::approve)。
    pub async fn toggle(&self, plugin: &Arc<NativePlugin>, enabled: bool) -> ToggleOutcome {
        if enabled {
            let missing = self.missing_dependencies(plugin);
            if !missing.is_empty() {
                return ToggleOutcome::MissingDependencies(missing);
            }
            if !plugin.is_approved() {
                if plugin.fingerprint().is_none() {
                    return ToggleOutcome::Unreadable;
                }
                if let Some(info) = self.approval_info(plugin) {
                    return ToggleOutcome::NeedsApproval(Box::new(info));
                }
            }
        }
        self.set_enabled(plugin, enabled).await;
        ToggleOutcome::Done
    }

    // 未开启（或未安装）的依赖插件的名称。
    fn missing_dependencies(&self, plugin: &NativePlugin) -> Vec<String> {
        let Some(manifest) = plugin.manifest() else {
            return Vec::new();
        };
        manifest
            .dependencies
            .iter()
            .filter_map(|id| match self.plugin(id) {
                Some(dep) if dep.is_enabled() => None,
                Some(dep) => Some(dep.name()),
                None => Some(format!("{id}（未安装）")),
            })
            .collect()
    }

    fn approval_info(&self, plugin: &NativePlugin) -> Option<ApprovalInfo> {
        let m = plugin.manifest()?;
        let names = |group| {
            permission::ALL
                .iter()
                .filter(|p| p.group == group && m.has_permission(p.id))
                .map(|p| p.name.to_owned())
                .collect::<Vec<_>>()
        };
        let mut events: Vec<String> = m
            .events
            .keys()
            .filter(|e| event_permission(e).is_some_and(|p| m.has_permission(p)))
            .map(|e| event_title(e).to_owned())
            .collect();
        if m.has_permission(permission::READ_MESSAGE) {
            events.extend(
                m.message_menus
                    .iter()
                    .map(|menu| format!("消息右键菜单“{}”", menu.name)),
            );
            events.extend(
                m.conversation_menus
                    .iter()
                    .map(|menu| format!("会话右键菜单“{}”", menu.name)),
            );
            events.extend(m.composer_buttons.iter().map(|button| {
                format!(
                    "输入框工具栏按钮“{}”（点击时读取输入框文字）",
                    button.tooltip
                )
            }));
        }
        if m.has_permission(permission::READ_CONTACTS) {
            events.extend(
                m.user_menus
                    .iter()
                    .map(|menu| format!("用户右键菜单“{}”", menu.name)),
            );
            events.extend(
                m.channel_menus
                    .iter()
                    .map(|menu| format!("频道右键菜单“{}”", menu.name)),
            );
        }
        Some(ApprovalInfo {
            title: format!("开启插件“{}”", plugin.name()),
            meta: approval_meta(m),
            notice: plugin
                .needs_reapproval()
                .then(|| "插件文件与上次开启时不同（已更新或被修改），请重新确认。".to_owned()),
            reads: names(PermissionGroup::Read),
            acts: names(PermissionGroup::Act),
            declared: names(PermissionGroup::Declared),
            events,
            launch_rules: if m.has_permission(permission::LAUNCH) {
                m.launch.iter().map(|r| r.pattern.clone()).collect()
            } else {
                Vec::new()
            },
            dependencies: m
                .dependencies
                .iter()
                .map(|id| self.plugin(id).map_or_else(|| id.clone(), |p| p.name()))
                .collect(),
            fingerprint: plugin
                .fingerprint()
                .map(fingerprint::short)
                .unwrap_or_default(),
        })
    }

    /// 用户在开启确认中同意：重新计算指纹，与扫描时（确认框显示的清单）一致才记录并启用。
    /// 返回 false 时提示“插件文件已变化，请重启程序后再开启”。
    pub async fn approve(&self, plugin: &Arc<NativePlugin>) -> bool {
        let Some(expected) = plugin.fingerprint() else {
            return false;
        };
        if plugin.manifest().is_none()
            || fingerprint::compute(plugin.directory()).ok().as_deref() != Some(expected)
        {
            return false;
        }
        PluginRepo::new(&self.db).approve(&plugin.id(), expected);
        plugin.set_approved(expected.to_owned());
        self.set_enabled(plugin, true).await;
        true
    }

    /// 启用或停用插件（保存状态）。启用只对已确认的插件有效。
    async fn set_enabled(&self, plugin: &Arc<NativePlugin>, enabled: bool) {
        if plugin.manifest().is_none()
            || plugin.is_enabled() == enabled
            || (enabled && !plugin.is_approved())
        {
            return;
        }
        PluginRepo::new(&self.db).set_enabled(&plugin.id(), enabled);
        plugin.set_enabled(enabled);
        self.emit(PluginEvent::PluginsChanged);
        if enabled && !plugin.is_loaded() {
            self.load(plugin).await;
            return;
        }
        let Some(host) = self.weak.upgrade() else {
            return;
        };
        let target = plugin.clone();
        // 生命周期回调在插件线程上按当时的状态决定，期间又切换回来时不会重复或遗漏。
        let _ = self
            .thread
            .run(self.default_session(plugin), move || {
                if target.is_enabled() {
                    host.activate(&target);
                } else {
                    host.deactivate(&target);
                }
            })
            .await;
    }

    /// 插件设置的当前值（解密后，含默认值）。
    pub fn load_settings(&self, plugin: &NativePlugin) -> Map<String, Value> {
        plugin
            .manifest()
            .map(|m| settings::load(&SettingRepo::app(&self.db), m))
            .unwrap_or_default()
    }

    /// 保存插件设置并通知插件。
    pub fn save_settings(&self, plugin: &Arc<NativePlugin>, values: &Map<String, Value>) {
        let Some(manifest) = plugin.manifest() else {
            return;
        };
        let repo = SettingRepo::app(&self.db);
        settings::save(&repo, manifest, values);
        plugin.set_settings_json(Value::Object(settings::load(&repo, manifest)).to_string());
        if plugin.is_enabled() && plugin.is_loaded() && !plugin.is_unresponsive() {
            self.post(plugin, |p| p.call_settings_changed());
        }
    }

    // 以默认会话投递一个调用，不等待。
    fn post(
        &self,
        plugin: &Arc<NativePlugin>,
        call: impl FnOnce(&Arc<NativePlugin>) + Send + 'static,
    ) {
        self.post_with(self.default_session(plugin), plugin, call);
    }

    fn post_with(
        &self,
        session: Option<Arc<Session>>,
        plugin: &Arc<NativePlugin>,
        call: impl FnOnce(&Arc<NativePlugin>) + Send + 'static,
    ) {
        let Some(host) = self.weak.upgrade() else {
            return;
        };
        let target = plugin.clone();
        drop(
            self.thread
                .run(session, move || host.invoke(&target, || call(&target))),
        );
    }

    /// 点击插件菜单。
    pub fn invoke_menu(&self, plugin: &Arc<NativePlugin>, menu_id: i32) {
        if plugin.is_enabled() && plugin.is_loaded() && !plugin.is_unresponsive() {
            self.post(plugin, move |p| p.call_menu(menu_id));
        }
    }

    /// 点击消息右键菜单：以消息所属的账号调用插件。
    pub fn invoke_message_menu(
        &self,
        plugin: &Arc<NativePlugin>,
        menu_id: i32,
        message: &ChatMessage,
        session: &Arc<Session>,
    ) {
        if plugin.active_message_menus().is_empty() {
            return;
        }
        let Some(snapshot) = MessageSnapshot::new(message, session, true) else {
            return;
        };
        self.post_with(Some(session.clone()), plugin, move |p| {
            p.call_message_menu(menu_id, &snapshot);
        });
    }

    /// 点击会话、用户或频道的右键菜单：以目标所属的账号调用插件。
    pub fn invoke_context_menu(
        &self,
        plugin: &Arc<NativePlugin>,
        menu_id: i32,
        session: &Arc<Session>,
        target: &ContextMenuTarget,
    ) {
        if plugin.active_context_menus(target.kind()).is_empty() {
            return;
        }
        let json = target.to_json(session).to_string();
        self.post_with(Some(session.clone()), plugin, move |p| {
            p.call_context_menu(menu_id, &json);
        });
    }

    /// 已开启的插件的输入框工具栏按钮。
    pub fn composer_buttons(&self) -> Vec<ComposerButton> {
        self.plugins()
            .iter()
            .flat_map(|plugin| {
                plugin
                    .active_composer_buttons()
                    .iter()
                    .map(|b| ComposerButton {
                        plugin: plugin.clone(),
                        id: b.id,
                        icon: match &b.icon {
                            ComposerIcon::Svg(file) => ComposerIcon::Svg(
                                plugin.directory().join(file).to_string_lossy().into_owned(),
                            ),
                            builtin => builtin.clone(),
                        },
                        tooltip: b.tooltip.clone(),
                    })
                    .collect::<Vec<_>>()
            })
            .collect()
    }

    /// 点击工具栏按钮，带当前会话和输入框文字；插件用 fill_composer 替换输入框内容，
    /// 结果走 [`PluginEvent::FillComposer`]。
    pub fn invoke_composer_button(
        &self,
        plugin: &Arc<NativePlugin>,
        button_id: i32,
        session: &Arc<Session>,
        key: &ConversationKey,
        text: &str,
    ) {
        if plugin.active_composer_buttons().is_empty() {
            return;
        }
        let json = json!({
            "account": session.account_id(),
            "conv": conversation_json(Some(key), session),
            "text": text,
        })
        .to_string();
        self.post_with(Some(session.clone()), plugin, move |p| {
            p.call_composer_button(button_id, &json);
        });
    }

    pub(crate) fn set_status_text(&self, plugin_id: &str, text: &str) {
        {
            let mut texts = self.status_texts.lock();
            texts.retain(|(id, _)| id != plugin_id);
            if !text.is_empty() {
                texts.push((plugin_id.to_owned(), text.to_owned()));
            }
        }
        self.emit(PluginEvent::StatusTextChanged);
    }

    /// 要显示在主窗口连接提示位置的文字：已开启的插件中最后设置的。
    pub fn status_text(&self) -> Option<PluginStatusText> {
        let texts = self.status_texts.lock().clone();
        texts.iter().rev().find_map(|(id, text)| {
            let plugin = self.plugin(id).filter(|p| p.is_enabled())?;
            Some(PluginStatusText {
                plugin_name: plugin.name(),
                text: text.clone(),
            })
        })
    }

    /// 插件面板：以 target 所属的账号调用 on_panel，返回插件回复的对象。
    /// 插件未开启、没有回复、回复格式错误或 2 秒内没有返回时为 `None`（不计入超时次数）。
    /// 结果用 [`parse_fields`](crate::panel::parse_fields)、[`parse_entries`](crate::panel::parse_entries) 解析。
    pub async fn panel(&self, plugin: &Arc<NativePlugin>, request: PanelRequest) -> Option<Value> {
        if plugin.active_panels().is_empty() {
            return None;
        }
        let host = self.weak.upgrade()?;
        let session = request
            .account_id()
            .and_then(|id| self.session(id))
            .or_else(|| self.default_session(plugin));
        let json = request.to_json().to_string();
        let target = plugin.clone();
        let reply = self.thread.run(session, move || {
            thread::in_panel(|| host.invoke(&target, || target.call_panel(&json)))
        });
        let text = tokio::time::timeout(CHAIN_TIMEOUT, reply)
            .await
            .ok()?
            .ok()??;
        serde_json::from_str::<Value>(&text)
            .ok()
            .filter(Value::is_object)
    }

    /// 聊天窗口中显示了这些消息（打开会话、加载历史、收到新消息），插件可以重新附加保存的结果。
    pub fn notify_messages_shown(&self, session: &Arc<Session>, message_ids: &[i64]) {
        let targets: Vec<_> = self
            .plugins()
            .into_iter()
            .filter(|p| p.handles_messages_shown())
            .collect();
        if message_ids.is_empty() || targets.is_empty() {
            return;
        }
        let json = json!({"account": session.account_id(), "messages": message_ids}).to_string();
        let Some(host) = self.weak.upgrade() else {
            return;
        };
        drop(self.thread.run(Some(session.clone()), move || {
            for plugin in targets.iter().filter(|p| p.handles_messages_shown()) {
                host.invoke(plugin, || plugin.call_messages_shown(&json));
            }
        }));
    }

    /// 设置中的按钮。返回 false 表示插件未开启或不支持。
    pub fn invoke_settings_button(
        &self,
        plugin: &Arc<NativePlugin>,
        click: &SettingsButtonClick,
    ) -> bool {
        if !plugin.can_use_settings_buttons() {
            return false;
        }
        let mut json =
            json!({"key": click.key, "values": click.values, "settings": click.settings});
        if let Some(row) = click.row {
            json["row"] = json!(row);
        }
        if let Some(item) = &click.item {
            json["item"] = json!(item);
        }
        let json = json.to_string();
        self.post(plugin, move |p| p.call_settings_button(&json));
        true
    }

    /// 已开启的插件可以绑定鼠标手势的消息菜单项。
    pub fn message_shortcuts(&self) -> Vec<ShortcutRow> {
        self.shortcut_rows(true)
    }

    /// 已开启的插件可以绑定键盘快捷键的插件菜单项。
    pub fn key_shortcuts(&self) -> Vec<ShortcutRow> {
        self.shortcut_rows(false)
    }

    fn shortcut_rows(&self, is_mouse: bool) -> Vec<ShortcutRow> {
        let bindings = self.shortcuts.lock().clone();
        self.plugins()
            .iter()
            .flat_map(|p| {
                let menus = if is_mouse {
                    p.active_message_menus()
                } else {
                    p.active_menus()
                };
                let id = p.id();
                let name = p.name();
                shortcuts::bindable(menus)
                    .into_iter()
                    .map(|m| ShortcutRow {
                        key: shortcuts::key_of(&id, m.id, is_mouse),
                        title: format!("{name} · {}", m.name),
                        is_mouse,
                        gesture: shortcuts::effective(&bindings, &id, m, is_mouse),
                    })
                    .collect::<Vec<_>>()
            })
            .collect()
    }

    /// 录制了新的手势 / 快捷键：宿主已经使用的鼠标手势不能绑定；同一个手势原来绑定的其他项改为不绑定。
    /// 返回要提示用户的文字。
    pub fn set_shortcut(&self, row: &ShortcutRow, gesture: &str) -> Vec<String> {
        if !gesture.is_empty() && row.is_mouse && !shortcuts::is_allowed_mouse(gesture) {
            return vec![format!(
                "{}已用于选中消息、打开资料卡或右键菜单，请加上 Ctrl / Alt / Shift",
                shortcuts::mouse_label(gesture)
            )];
        }
        let mut toasts = Vec::new();
        let others = self.shortcut_rows(row.is_mouse);
        let mut bindings = self.shortcuts.lock().clone();
        if !gesture.is_empty() {
            for other in others
                .iter()
                .filter(|o| o.key != row.key && o.gesture == gesture)
            {
                bindings.insert(other.key.clone(), String::new());
                toasts.push(format!("已取消“{}”的绑定", other.title));
            }
        }
        bindings.insert(row.key.clone(), gesture.to_owned());
        shortcuts::save(&SettingRepo::app(&self.db), &bindings);
        *self.shortcuts.lock() = bindings;
        self.emit(PluginEvent::PluginsChanged);
        toasts
    }

    // 绑定到手势的菜单项：按插件顺序取第一个。
    fn find_shortcut(&self, gesture: &str, is_mouse: bool) -> Option<(Arc<NativePlugin>, i32)> {
        let bindings = self.shortcuts.lock().clone();
        self.plugins().into_iter().find_map(|p| {
            let menus = if is_mouse {
                p.active_message_menus()
            } else {
                p.active_menus()
            };
            let id = p.id();
            let menu_id = shortcuts::bindable(menus)
                .into_iter()
                .find(|m| shortcuts::effective(&bindings, &id, m, is_mouse) == gesture)
                .map(|m| m.id);
            menu_id.map(|menu_id| (p.clone(), menu_id))
        })
    }

    /// 鼠标手势绑定了消息菜单项。
    pub fn has_mouse_shortcut(&self, gesture: &str) -> bool {
        self.find_shortcut(gesture, true).is_some()
    }

    /// 对消息使用手势：有绑定的菜单项时调用它并返回 true。
    pub fn invoke_mouse_shortcut(
        &self,
        gesture: &str,
        message: &ChatMessage,
        session: &Arc<Session>,
    ) -> bool {
        match self.find_shortcut(gesture, true) {
            Some((plugin, menu_id)) => {
                self.invoke_message_menu(&plugin, menu_id, message, session);
                true
            }
            None => false,
        }
    }

    /// 按下键盘快捷键：绑定了插件菜单项时调用它并返回 true。
    pub fn invoke_key_shortcut(&self, gesture: &str) -> bool {
        match self.find_shortcut(gesture, false) {
            Some((plugin, menu_id)) => {
                self.invoke_menu(&plugin, menu_id);
                true
            }
            None => false,
        }
    }

    /// 提前过滤文字消息，用于在发送前知道结果（比如被拦截时保留输入框内容）。
    /// [`Session::send_as`] 本身会过滤；随后 [`PREFILTER_LIMIT`] 内发送返回的内容时不再过滤。
    pub async fn filter_outgoing(
        &self,
        session: &Arc<Session>,
        key: &ConversationKey,
        content: &str,
    ) -> Result<String, OutgoingRejected> {
        let filtered = self
            .filter_outgoing_as(session, key, content, OutgoingKind::Text)
            .await?;
        let mut prefiltered = self.prefiltered.lock();
        prefiltered.retain(|p| p.at.elapsed() < PREFILTER_LIMIT);
        prefiltered.push(Prefiltered {
            account_id: session.account_id(),
            key: key.clone(),
            content: filtered.clone(),
            at: Instant::now(),
        });
        Ok(filtered)
    }

    fn take_prefiltered(&self, account_id: i64, key: &ConversationKey, content: &str) -> bool {
        let mut prefiltered = self.prefiltered.lock();
        let found = prefiltered.iter().position(|p| {
            p.account_id == account_id
                && p.key == *key
                && p.content == content
                && p.at.elapsed() < PREFILTER_LIMIT
        });
        found.map(|index| prefiltered.remove(index)).is_some()
    }

    /// 发送前过滤：通过时返回（可能被改写的）内容，超时时返回原内容。
    /// 每个插件看到的是前面的插件改写后的内容。
    pub async fn filter_outgoing_as(
        &self,
        session: &Arc<Session>,
        key: &ConversationKey,
        content: &str,
        kind: OutgoingKind,
    ) -> Result<String, OutgoingRejected> {
        let candidates = self.has_handlers(FILTER_OUTGOING);
        let conv = Conversation::from_key(key, session);
        let (false, Some(conv)) = (!candidates, conv) else {
            return Ok(content.to_owned());
        };
        let account_id = session.account_id();
        let (result, state) = self
            .run_chain(
                FILTER_OUTGOING,
                session,
                content.to_owned(),
                move |plugin, current| {
                    let (result, changed) = thread::in_outgoing_filter(|| {
                        plugin.call_filter_outgoing(account_id, &conv, current, kind)
                    });
                    // 只监控的插件不能改写内容。
                    if let Some(changed) = changed
                        && plugin.priority(FILTER_OUTGOING) != MONITOR_PRIORITY
                    {
                        *current = changed;
                    }
                    result
                },
            )
            .await;
        match (result, state) {
            (ChainResult::Block, _) => Err(OutgoingRejected::Blocked),
            (ChainResult::Ignore, Some(current)) if current != content => {
                if current.trim().is_empty() {
                    return Err(OutgoingRejected::Emptied);
                }
                let limit = session.realm().max_message_length;
                if limit > 0 && current.chars().count() > limit as usize {
                    return Err(OutgoingRejected::TooLong);
                }
                Ok(current)
            }
            _ => Ok(content.to_owned()),
        }
    }

    fn has_handlers(&self, event: &str) -> bool {
        self.plugins.read().iter().any(|p| p.handles(event))
    }

    /// 事件链：按优先级（相同时按 id）依次调用接收该事件的插件，返回 BLOCK 时停止；
    /// 优先级 10000 的插件只能监控，它返回 BLOCK 视为 IGNORE。最多等待 2 秒，
    /// 超时后剩余的插件不再调用，正在执行的插件超时计数加 1。
    async fn run_chain<S: Send + 'static>(
        &self,
        event: &'static str,
        session: &Arc<Session>,
        state: S,
        invoke: impl FnMut(&Arc<NativePlugin>, &mut S) -> i32 + Send + 'static,
    ) -> (ChainResult, Option<S>) {
        match self.start_chain(event, session, state, invoke) {
            Ok(started) => self.finish_chain(event, started).await,
            Err(state) => (ChainResult::Ignore, Some(state)),
        }
    }

    // 同步投递到插件线程，事件按调用顺序执行。没有要调用的插件时退回 state。
    fn start_chain<S: Send + 'static>(
        &self,
        event: &'static str,
        session: &Arc<Session>,
        mut state: S,
        mut invoke: impl FnMut(&Arc<NativePlugin>, &mut S) -> i32 + Send + 'static,
    ) -> Result<StartedChain<S>, S> {
        let mut candidates: Vec<_> = self
            .plugins()
            .into_iter()
            .filter(|p| p.handles(event))
            .collect();
        candidates.sort_by_key(|p| (p.priority(event), p.id()));
        let Some(chain_host) = self.weak.upgrade() else {
            return Err(state);
        };
        if candidates.is_empty() || self.chain.stuck.lock().is_some() {
            return Err(state);
        }

        let cancel = Arc::new(AtomicBool::new(false));
        let cancelled = cancel.clone();
        let reply = self.thread.run(Some(session.clone()), move || {
            for plugin in &candidates {
                if cancelled.load(Ordering::Acquire) {
                    break;
                }
                if !plugin.is_enabled() || plugin.is_unresponsive() {
                    continue;
                }
                let result = chain_host.invoke(plugin, || invoke(plugin, &mut state));
                if result == abi::EVENT_BLOCK && plugin.priority(event) != MONITOR_PRIORITY {
                    return (ChainResult::Block, state);
                }
            }
            (ChainResult::Ignore, state)
        });
        Ok(StartedChain { reply, cancel })
    }

    async fn finish_chain<S: Send + 'static>(
        &self,
        event: &'static str,
        started: StartedChain<S>,
    ) -> (ChainResult, Option<S>) {
        let StartedChain {
            reply: mut rx,
            cancel,
        } = started;
        match tokio::time::timeout(CHAIN_TIMEOUT, &mut rx).await {
            Ok(Ok((result, state))) => return (result, Some(state)),
            Ok(Err(_)) => return (ChainResult::Ignore, None),
            Err(_) => {}
        }

        cancel.store(true, Ordering::Release);
        self.charge_timeout(event);
        let Some(host) = self.weak.upgrade() else {
            return (ChainResult::TimedOut, None);
        };
        let chain_id = self.chain.next_chain.fetch_add(1, Ordering::AcqRel) + 1;
        *self.chain.stuck.lock() = Some(chain_id);
        self.runtime.spawn(async move {
            let _ = rx.await;
            let mut stuck = host.chain.stuck.lock();
            if *stuck == Some(chain_id) {
                *stuck = None;
            }
        });
        self.emit(PluginEvent::PluginsChanged);
        (ChainResult::TimedOut, None)
    }

    // 不等待结果的事件链（通知型事件）。
    fn spawn_chain<S: Send + 'static>(
        &self,
        event: &'static str,
        session: &Arc<Session>,
        state: S,
        invoke: impl FnMut(&Arc<NativePlugin>, &mut S) -> i32 + Send + 'static,
    ) {
        let (Some(host), Ok(started)) = (
            self.weak.upgrade(),
            self.start_chain(event, session, state, invoke),
        ) else {
            return;
        };
        self.runtime.spawn(async move {
            host.finish_chain(event, started).await;
        });
    }

    fn notify(&self, event: &'static str, session: &Arc<Session>, mut payload: Value) {
        payload["account"] = json!(session.account_id());
        let json = payload.to_string();
        self.spawn_chain(event, session, (), move |plugin, ()| {
            plugin.call_notification(event, &json);
            abi::EVENT_IGNORE
        });
    }

    fn charge_timeout(&self, event: &str) {
        let call = self.chain.current_call.load(Ordering::Acquire);
        let stuck = self.chain.current.lock().clone();
        log::warn!(
            "插件事件 {event} 超时：{}",
            stuck.as_ref().map_or("未知".to_owned(), |p| p.id())
        );
        let Some(plugin) = stuck else {
            return;
        };
        if self.chain.charged_call.swap(call, Ordering::AcqRel) == call {
            return;
        }
        plugin.on_timeout();
        let suffix = if plugin.is_unresponsive() {
            "，连续多次超时，本次运行内不再派发事件"
        } else {
            ""
        };
        plugin.add_log(
            abi::LOG_WARNING,
            HOST_CATEGORY,
            &format!(
                "{event} 超过 {} 秒没有返回{suffix}",
                CHAIN_TIMEOUT.as_secs()
            ),
        );
    }

    pub(crate) fn set_annotation(
        &self,
        account_id: i64,
        message_id: i64,
        plugin_id: &str,
        annotation: Option<PluginAnnotation>,
    ) {
        self.annotations
            .set(account_id, message_id, plugin_id, annotation);
        self.emit(PluginEvent::AnnotationChanged {
            account_id,
            message_id,
        });
    }

    /// 插件有 launch 权限且目标在其白名单中（可以不经用户点击直接打开）。
    pub fn is_whitelisted(plugin: &NativePlugin, target: &str) -> bool {
        plugin.has_permission(permission::LAUNCH)
            && plugin
                .manifest()
                .is_some_and(|m| m.launch.iter().any(|r| r.matches(target)))
    }

    /// 一条消息上要显示的附加内容：只显示已开启的插件；第一个有 render_message 权限的 replace 替换气泡内容。
    pub fn message_annotations(&self, account_id: i64, message_id: i64) -> MessageAnnotations {
        let mut result = MessageAnnotations::default();
        for annotation in self.annotations.get(account_id, message_id) {
            let Some(plugin) = self
                .plugin(&annotation.plugin_id)
                .filter(|p| p.is_enabled())
            else {
                continue;
            };
            if !annotation.replace.is_empty()
                && result.rendered.is_none()
                && plugin.has_permission(permission::RENDER_MESSAGE)
            {
                result.rendered = Some(RenderedContent {
                    plugin_name: plugin.name(),
                    html: plain_text_html(&annotation.replace),
                    original_shown: self.annotations.is_original_shown(account_id, message_id),
                });
            }
            if annotation.has_card() {
                let name = plugin.name();
                let is_collapsed =
                    self.annotations
                        .is_collapsed(account_id, message_id, &annotation.plugin_id);
                result.cards.push(AnnotationCard {
                    toggle_tooltip: if is_collapsed {
                        format!("展开 {name} 附加的内容")
                    } else {
                        format!("折叠 {name} 附加的内容（不修改消息）")
                    },
                    plugin_id: annotation.plugin_id.clone(),
                    plugin_name: name,
                    text_html: markdown_html(&annotation.text),
                    text: annotation.text.clone(),
                    is_collapsed,
                    actions: annotation
                        .actions
                        .iter()
                        .map(|a| action_view(&plugin, a))
                        .collect(),
                });
            }
        }
        result
    }

    pub fn set_annotation_collapsed(
        &self,
        account_id: i64,
        message_id: i64,
        plugin_id: &str,
        collapsed: bool,
    ) {
        if self
            .annotations
            .set_collapsed(account_id, message_id, plugin_id, collapsed)
        {
            self.emit(PluginEvent::AnnotationChanged {
                account_id,
                message_id,
            });
        }
    }

    /// 插件替换了气泡内容时，用户选择显示原文或译文。
    pub fn set_original_shown(&self, account_id: i64, message_id: i64, shown: bool) {
        if self
            .annotations
            .set_original_shown(account_id, message_id, shown)
        {
            self.emit(PluginEvent::AnnotationChanged {
                account_id,
                message_id,
            });
        }
    }

    /// 点击附加内容中的按钮（用户点击即确认，不要求在白名单中）。失败时返回提示文字。
    pub fn run_action(&self, view: &ActionView) -> Result<ActionOutcome, String> {
        if !view.can_run {
            return Ok(ActionOutcome::Done);
        }
        if view.action.is_fill() {
            return Ok(ActionOutcome::Fill(view.action.fill.clone()));
        }
        match launch::launch(&view.action.target, &view.action.args) {
            Ok(()) => {
                view.plugin.add_log(
                    abi::LOG_INFO,
                    HOST_CATEGORY,
                    &format!("用户点击打开 {}", view.detail),
                );
                Ok(ActionOutcome::Done)
            }
            Err(error) => {
                log::warn!("打开插件按钮失败：{}：{error}", view.detail);
                Err(format!("无法打开：{}", view.action.target))
            }
        }
    }

    pub(crate) fn publish_resources(
        &self,
        plugin_id: &str,
        kind: &str,
        resources: Vec<PluginResource>,
    ) {
        self.resources.publish(plugin_id, kind, resources);
        self.emit(PluginEvent::ResourcesChanged);
    }

    /// 插件发布的某类资源；发布者不可用时为空。
    pub fn available_resources(&self, plugin_id: &str, kind: &str) -> Vec<PluginResource> {
        if self
            .plugin(plugin_id)
            .is_some_and(|p| p.can_provide_resources())
        {
            self.resources.published(plugin_id, kind)
        } else {
            Vec::new()
        }
    }

    /// resource 设置项的选项：第一项为“不使用”。已保存的选择不在可选项中时仍保留，标为不可用。
    pub fn resource_options(
        &self,
        field: &PluginSettingField,
        saved: Option<&ResourceSelection>,
    ) -> Vec<ResourceOption> {
        let mut options = vec![ResourceOption {
            selection: None,
            label: "不使用".to_owned(),
            available: true,
        }];
        for resource in self.available_resources(&field.resource_plugin, &field.resource_type) {
            for item in &resource.items {
                options.push(ResourceOption {
                    selection: Some(ResourceSelection {
                        kind: field.resource_type.clone(),
                        plugin_id: resource.plugin_id.clone(),
                        resource_id: resource.id.clone(),
                        item: item.clone(),
                    }),
                    label: format!("{} / {item}", resource.name),
                    available: true,
                });
            }
        }
        if let Some(saved) = saved
            && !options.iter().any(|o| o.selection.as_ref() == Some(saved))
        {
            options.push(ResourceOption {
                selection: Some(saved.clone()),
                label: format!("{} / {}（不可用）", saved.resource_id, saved.item),
                available: false,
            });
        }
        options
    }

    /// 依赖的插件没有发布该类资源时显示的文字。
    pub fn no_resources_text(&self, field: &PluginSettingField) -> String {
        let name = self
            .plugin(&field.resource_plugin)
            .map_or_else(|| field.resource_plugin.clone(), |p| p.name());
        format!("没有可选项：请先开启插件“{name}”并在其中添加")
    }

    /// 把请求转交给发布资源的插件并等待回复（阻塞，不能在插件线程上调用）。错误为 ABI 错误码。
    pub(crate) fn request_resource(
        &self,
        consumer: &NativePlugin,
        target: &ResourceSelection,
        request: &Value,
    ) -> Result<String, i32> {
        let Some(provider) = self
            .plugin(&target.plugin_id)
            .filter(|p| p.can_provide_resources())
        else {
            return Err(abi::ERR_NOT_FOUND);
        };
        if !self.resources.contains(target) {
            return Err(abi::ERR_NOT_FOUND);
        }
        let (id, reply) = self.resources.begin_request(&target.plugin_id);
        let payload = json!({
            "type": target.kind,
            "resource": target.resource_id,
            "item": target.item,
            "request": request,
            "from": consumer.id(),
        })
        .to_string();
        self.post(&provider, move |p| p.call_resource_request(id, &payload));
        let result = reply
            .recv_timeout(resources::REQUEST_TIMEOUT)
            .map_err(|_| abi::ERR_NETWORK);
        self.resources.end_request(id);
        result
    }

    pub(crate) fn reply_resource(&self, provider: &NativePlugin, id: i64, json: String) -> bool {
        self.resources.reply(&provider.id(), id, json)
    }

    /// 为面板列表选择对象：所有已登录账号的用户（不含自己和机器人），或已订阅频道的话题（没有缓存时加载）。
    pub async fn target_candidates(&self, kind: PluginTargetKind) -> Vec<TargetCandidate> {
        let sessions = self.sessions();
        let show_account = sessions.len() > 1;
        let mut result = Vec::new();
        for session in &sessions {
            let suffix = if show_account {
                format!(" · {}", session.email())
            } else {
                String::new()
            };
            match kind {
                PluginTargetKind::User => {
                    let self_id = session.self_id();
                    let mut users: Vec<_> = session
                        .users()
                        .into_iter()
                        .filter(|u| u.is_active && !u.is_bot && u.id != self_id)
                        .map(|u| (session.display_name(u.id), u.id, u.email))
                        .collect();
                    users.sort();
                    result.extend(users.into_iter().map(|(name, id, email)| TargetCandidate {
                        target: PluginTarget::User {
                            account_id: session.account_id(),
                            user_id: id,
                            name: name.clone(),
                        },
                        title: name,
                        subtitle: format!("{email}{suffix}"),
                    }));
                }
                PluginTargetKind::Topic => {
                    let mut channels = session.subscribed_channels();
                    channels.sort_by(|a, b| a.name.cmp(&b.name));
                    for channel in channels {
                        if session.topics(channel.stream_id).is_none()
                            && let Err(error) = session.load_topics(channel.stream_id).await
                        {
                            log::warn!("加载话题失败：{}：{error}", channel.name);
                        }
                        for topic in session.topics(channel.stream_id).unwrap_or_default() {
                            result.push(TargetCandidate {
                                target: PluginTarget::Topic {
                                    account_id: session.account_id(),
                                    stream_id: channel.stream_id,
                                    name: format!("{} / {}", channel.name, topic.name),
                                    topic: topic.name.clone(),
                                },
                                title: topic.name,
                                subtitle: format!("{}{suffix}", channel.name),
                            });
                        }
                    }
                }
            }
        }
        result
    }

    /// 程序退出：插件线程不再接受新的调用。
    pub fn shutdown(&self) {
        self.thread.stop();
    }
}

fn action_view(plugin: &Arc<NativePlugin>, action: &PluginAction) -> ActionView {
    let detail = if action.is_fill() {
        action.fill.clone()
    } else {
        launch::describe(&action.target, &action.args)
    };
    let tooltip = if action.is_fill() {
        format!("点击填入输入框（不会发送）：{detail}")
    } else if PluginHost::is_whitelisted(plugin, &action.target) {
        format!("白名单内：{detail}")
    } else {
        format!("不在白名单中，点击后打开：{detail}")
    };
    let needed = if action.is_fill() {
        permission::FILL_COMPOSER
    } else {
        permission::LAUNCH
    };
    ActionView {
        plugin: plugin.clone(),
        action: action.clone(),
        label: action.label.clone(),
        style: action.style,
        detail,
        tooltip,
        can_run: plugin.is_enabled() && plugin.has_permission(needed),
    }
}

impl SessionHooks for PluginHost {
    fn session_started(&self, session: &Arc<Session>) {
        {
            let mut sessions = self.sessions.write();
            if sessions.iter().any(|s| Arc::ptr_eq(s, session)) {
                return;
            }
            sessions.push(session.clone());
        }
        let Some(host) = self.weak.upgrade() else {
            return;
        };
        let account_id = session.account_id();
        drop(self.thread.run(Some(session.clone()), move || {
            for plugin in host.plugins() {
                host.activate(&plugin);
                if plugin.is_active() {
                    host.invoke(&plugin, || plugin.call_account_login(account_id));
                }
            }
        }));
    }

    fn session_ending(&self, session: &Arc<Session>) -> HookFuture<()> {
        let removed = {
            let mut sessions = self.sessions.write();
            let before = sessions.len();
            sessions.retain(|s| !Arc::ptr_eq(s, session));
            sessions.len() != before
        };
        let host = self.weak.upgrade();
        let (true, Some(host)) = (removed, host) else {
            return Box::pin(async {});
        };
        let last = self.sessions.read().is_empty();
        let account_id = session.account_id();
        let job_host = host.clone();
        let done = host.thread.run(Some(session.clone()), move || {
            for plugin in job_host.plugins() {
                if plugin.is_active() {
                    job_host.invoke(&plugin, || plugin.call_account_logout(account_id));
                }
                if last {
                    job_host.deactivate(&plugin);
                }
            }
        });
        Box::pin(async move {
            let _ = done.await;
        })
    }

    fn filter_incoming(&self, session: &Arc<Session>, message: &ChatMessage) -> HookFuture<bool> {
        let host = self.weak.upgrade();
        let snapshot = MessageSnapshot::new(message, session, false);
        let (Some(host), Some(snapshot), true) =
            (host, snapshot, self.has_handlers(FILTER_INCOMING))
        else {
            return Box::pin(async { false });
        };
        let session = session.clone();
        Box::pin(async move {
            let (result, _) = host
                .run_chain(FILTER_INCOMING, &session, (), move |plugin, ()| {
                    plugin.call_filter_incoming(&snapshot)
                })
                .await;
            result == ChainResult::Block
        })
    }

    fn on_message(&self, session: &Arc<Session>, message: &ChatMessage) {
        let host = self.weak.upgrade();
        let snapshot = MessageSnapshot::new(message, session, false);
        let (Some(host), Some(snapshot), true) = (host, snapshot, self.has_handlers(ON_MESSAGE))
        else {
            return;
        };
        host.spawn_chain(ON_MESSAGE, session, (), move |plugin, ()| {
            plugin.call_on_message(&snapshot)
        });
    }

    fn on_event(&self, session: &Arc<Session>, event: HookEvent) {
        let name = hook_event_name(&event);
        if self.has_handlers(name) {
            self.notify(name, session, hook_event_payload(event, session));
        }
    }

    fn filter_outgoing(
        &self,
        session: &Arc<Session>,
        key: &ConversationKey,
        content: &str,
        kind: OutgoingKind,
    ) -> HookFuture<Result<String, String>> {
        let Some(host) = self.weak.upgrade() else {
            let content = content.to_owned();
            return Box::pin(async move { Ok(content) });
        };
        let session = session.clone();
        let key = key.clone();
        let content = content.to_owned();
        if kind == OutgoingKind::Text && host.take_prefiltered(session.account_id(), &key, &content)
        {
            return Box::pin(async move { Ok(content) });
        }
        Box::pin(async move {
            host.filter_outgoing_as(&session, &key, &content, kind)
                .await
                .map_err(|rejected| rejected.to_string())
        })
    }
}

fn hook_event_name(event: &HookEvent) -> &'static str {
    match event {
        HookEvent::MessageEdited { .. } => ON_MESSAGE_EDITED,
        HookEvent::MessagesDeleted { .. } => ON_MESSAGE_DELETED,
        HookEvent::ReactionChanged { .. } => ON_REACTION_CHANGED,
        HookEvent::ReadChanged { .. } => ON_READ_STATE_CHANGED,
        HookEvent::TopicMoved { .. } => ON_TOPIC_MOVED,
        HookEvent::ChannelsChanged { .. } => ON_CHANNEL_CHANGED,
        HookEvent::UserChanged(_) => ON_USER_CHANGED,
        HookEvent::PresenceChanged(_) => ON_PRESENCE_CHANGED,
        HookEvent::ConversationOpened(_) => ON_CONVERSATION_OPENED,
        HookEvent::Connection(_) => ON_CONNECTION_CHANGED,
    }
}

fn hook_event_payload(event: HookEvent, session: &Session) -> Value {
    let channel = |stream_id: i64, topic: String| {
        Conversation::Channel {
            stream_id,
            stream_name: session
                .channel(stream_id)
                .map(|c| c.name)
                .unwrap_or_default(),
            topic,
        }
        .to_json()
    };
    match event {
        HookEvent::MessageEdited {
            key,
            id,
            edit_timestamp,
            html,
        } => json!({
            "message_id": id,
            "conv": conversation_json(key.as_ref(), session),
            "edit_timestamp": edit_timestamp,
            "content_text": to_plain_text(&html),
            "content_html": html,
        }),
        HookEvent::MessagesDeleted { key, ids } => json!({
            "message_ids": ids,
            "conv": conversation_json(key.as_ref(), session),
        }),
        HookEvent::ReactionChanged {
            key,
            id,
            reaction,
            added,
        } => json!({
            "message_id": id,
            "conv": conversation_json(key.as_ref(), session),
            "added": added,
            "reaction": reaction,
        }),
        HookEvent::ReadChanged { ids, read, all } => {
            json!({"message_ids": ids, "read": read, "all": all})
        }
        HookEvent::TopicMoved {
            old_stream,
            old_topic,
            new_stream,
            new_topic,
            ids,
            whole,
        } => json!({
            "old": channel(old_stream, old_topic),
            "new": channel(new_stream, new_topic),
            "message_ids": ids,
            "whole": whole,
        }),
        HookEvent::ChannelsChanged { stream_ids, op } => {
            json!({"streams": stream_ids, "op": op})
        }
        HookEvent::UserChanged(id) => json!({"user": id}),
        HookEvent::PresenceChanged(ids) => json!({"users": ids}),
        HookEvent::ConversationOpened(key) => {
            json!({"conv": conversation_json(Some(&key), session)})
        }
        HookEvent::Connection(state) => json!({"state": match state {
            ConnectionState::Connected => "connected",
            ConnectionState::Reconnecting => "reconnecting",
            ConnectionState::AuthFailed => "auth_failed",
        }}),
    }
}
