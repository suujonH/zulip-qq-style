use zulip_core::ConversationKey;

use crate::settings::SettingsFeedback;

/// 插件宿主的事件。可能在任何线程上发出。
#[derive(Clone, Debug)]
pub enum PluginEvent {
    /// 插件的加载、启用状态或快捷操作变化。
    PluginsChanged,
    /// 该插件有新日志或日志被清空。
    LogsChanged(String),
    /// 某条消息的附加内容、折叠或“显示原文”状态变化。
    AnnotationChanged {
        account_id: i64,
        message_id: i64,
    },
    /// 插件发布的资源变化。
    ResourcesChanged,
    /// 插件回复设置窗口（窗口已关闭时忽略）。
    SettingsFeedback {
        plugin_id: String,
        feedback: SettingsFeedback,
    },
    /// 切换到会话，`message_id` 不为空时定位到消息。
    OpenConversation {
        account_id: i64,
        key: ConversationKey,
        message_id: Option<i64>,
    },
    /// 填入该账号当前会话的输入框（不发送）；`replace` 时替换已有内容，否则插入到光标处。
    FillComposer {
        plugin_id: String,
        account_id: i64,
        text: String,
        replace: bool,
    },
    Toast {
        plugin_id: String,
        text: String,
    },
    /// 系统通知。
    SystemNotification {
        plugin_id: String,
        title: String,
        body: String,
    },
    /// [`PluginHost::status_text`](crate::PluginHost::status_text) 变化。
    StatusTextChanged,
}
