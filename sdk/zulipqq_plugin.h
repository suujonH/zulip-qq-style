/*
 * ZulipQQ 原生插件接口（参考 CoolQ cqp）。
 *
 * - x64、__cdecl、UTF-8 字符串。
 * - 宿主不导出符号：插件导出 zq_plugin_init，宿主把一张宿主函数表传进来，插件保存后通过它调用宿主。
 *   表中的 ctx 代表“哪个插件”，每次调用原样传回。
 * - 宿主交给插件的指针（消息、用户、字符串）只在回调执行期间有效，需要的话请自行复制。
 * - 所有插件导出函数都在同一个插件线程上调用；插件可以在任何线程调用宿主函数（同步阻塞），
 *   但不要在宿主的 UI 线程上调用（不会发生，除非插件自己创建窗口并在其中调用）。
 * - 原生插件崩溃会导致整个程序崩溃。停用插件只是不再派发事件，DLL 在程序重启后才会卸载。
 * - 插件目录：<程序目录>\plugins\<id>\，包含 plugin.json 与 DLL。
 * - 多账号（ZQ_API_VERSION 2）：多个账号可以同时登录，所有账号的消息都派发给插件，zq_message / zq_outgoing 的
 *   account_id 是消息所属的账号（本地账号 id）。回调中调用宿主函数时使用该回调所属的账号；回调之外（插件自己的线程、
 *   zq_on_menu）使用 select_account 选择的账号，没有选择或该账号已退出时使用最先登录的账号（get_accounts 中 is_primary 的账号）。
 *   结构体只在末尾追加字段：按 api_version 1 编译的插件照常工作（读不到新字段），新字段请先检查 struct_size。
 * - 权限（ZQ_API_VERSION 3）：插件只能调用 plugin.json 中 permissions 声明过的宿主函数（否则返回 ZQ_ERR_PERMISSION）。
 *   新安装的插件默认停用；用户开启时宿主会列出插件声明的权限、事件与 launch 白名单并要求确认。宿主记录插件目录中
 *   所有文件的指纹，任何文件变化（更新、修改）后都需要重新开启，因此运行时数据请写到 get_data_dir 返回的目录。
 *   权限只约束宿主函数：原生插件不在沙箱中，自己访问网络等行为请声明 "network" 等，开启时告知用户。
 *   permissions：read_message、read_contacts、send_message、modify_message、upload_file、annotate_message、
 *   render_message、fill_composer、edit_message、delete_message、manage_channel、manage_user、launch、
 *   network（仅声明）。
 * - plugin.json 的 dependencies（3）：["com.zulipqq.modelcenter"]，依赖的插件 id。开启前需要先开启依赖的插件，
 *   开启确认中会列出；只能使用依赖的插件发布的资源（resource 设置项、request_resource）。
 * - plugin.json 的 message_menus（3）：[{"id": 1, "name": "翻译"}]，显示在消息右键菜单中，点击时调用 zq_on_message_menu
 *   （需要 read_message；回调中的宿主函数使用该消息所属的账号）。子菜单写作 {"name": "设置", "children": [...]}
 *   （不需要 id，最多嵌套 2 层，只有叶子菜单项会调用插件）。宿主把所有插件的菜单项放在右键菜单的“插件”子菜单中。
 *   叶子菜单项可以写 "shortcut": "鼠标手势" 或 ""，表示可以绑定对单条消息的快捷操作（在消息上使用该手势直接调用
 *   zq_on_message_menu）及默认手势（"" 为没有默认值）。手势写作 [ctrl+][alt+][shift+]{left|middle|right}_{click|double}，
 *   例如 middle_click、ctrl+left_click、alt+right_double；left_click、left_double、right_click、right_double、shift+left_click
 *   已由宿主使用，不能绑定。用户在“设置 > 插件 > 消息快捷操作”中统一修改，一个手势只绑定一项。
 * - plugin.json 的 menus 中的项同样可以写 "shortcut": "Ctrl+Shift+T" 或 ""，表示可以绑定键盘快捷键（在 ZulipQQ 的窗口中按下时
 *   调用 zq_on_menu；需要 Ctrl 或 Alt，F1～F24 可以单独使用），在“设置 > 插件 > 快捷键”中修改。
 * - plugin.json 的 panels（3）：[{"kind": "topic" | "user", "label": "话题场景"}]，每种最多一个。宿主在话题的资料面板 / 其他用户的
 *   资料卡中为插件显示一个分组（标题为插件名），在插件设置中显示以 label 为标题的列表；设置项和数据都由插件提供并自行保存，
 *   宿主调用 zq_on_panel，插件在回调中用 reply_panel 回复。需要导出 zq_on_panel。
 * - 插件自己的数据（例如译文、分析结果）由插件保存在 get_data_dir 中（格式自定）；set_annotation 只在本次运行中有效，
 *   宿主在消息显示时调用 zq_on_messages_shown，插件可以重新附加保存的结果。
 * - plugin.json 的 settings（3）：声明设置项，宿主在“设置 > 插件 > 设置”中生成界面并保存（插件只能读取，get_settings）。
 *   每项 {"key", "label", "type": text|password|multiline|select|bool|list|button|tags|resource, "hint", "placeholder", "default",
 *   "options": [{"value", "label"}]（select）, "fields": [...]（list 每行的设置项，不能嵌套 list）,
 *   "visible_when": {"同一层另一项的 key": "值" 或 ["值", ...]}}。password 的值由宿主加密保存。
 *   button 没有值（可以放在 list 的行中）：点击时宿主以表单当前的值（尚未保存）调用 zq_on_settings_button，插件用
 *   set_settings_feedback 把结果显示在按钮下方（例如“测试”）；插件未开启时宿主提示先开启。
 *   tags 的值为字符串数组，可手动输入；插件用 set_settings_feedback 提供 options 时宿主打开候选列表，用户逐项添加 / 删除；"item_button": "测试"
 *   在每一项旁边显示按钮，点击时以该项（item）调用 zq_on_settings_button。
 *   resource 选择依赖的插件发布的资源，"plugin": "发布者的 id", "resource": "资源类型"（例如 model）；
 *   值为 {"type", "plugin", "resource", "item"}，没有选择时为 {}。
 * - 插件间共享资源（3）：宿主只是通道，资源类型（小写字母、数字、下划线）与请求、回复的格式由发布者约定，宿主不理解其内容。
 *   发布者用 publish_resources 发布 [{"id", "name", "items": [...]}]（例如 provider 与其中的模型）并导出
 *   zq_on_resource_request；使用者在 dependencies 中声明发布者，在设置中用 resource 设置项选择，在自己的线程上调用
 *   request_resource，宿主把请求交给发布者，发布者用 reply_resource 回复（发布者的 API Key 等不会交给使用者）。
 *   模型中心的资源类型与格式见 samples/plugins/ModelCenter/Client/ModelCenterClient.cs（C# 插件可以直接编译进来）。
 * - plugin.json 的 launch（3）：白名单，开启时告知用户；launch() 只能打开白名单中的目标。写法："todo:"（协议，
 *   不能是 http/https/file 等）、"https://example.com/path" 或 "https://*.example.com"（网址前缀）、
 *   "C:\Tools\a.exe"（完整路径）或 "notepad.exe"（只匹配同样只写文件名的目标）。
 *   白名单之外的目标请放在 set_annotation 的按钮中，由用户点击后打开。
 */
#pragma once
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
#define ZQ_API_VERSION 3
#define ZQ_EXPORT __declspec(dllexport)
#define ZQ_CALL __cdecl

#define ZQ_EVENT_IGNORE 0
#define ZQ_EVENT_BLOCK  1

/* 错误码：宿主函数返回负数表示失败 */
#define ZQ_OK                 0
#define ZQ_ERR_UNKNOWN       -1
#define ZQ_ERR_INVALID_ARG   -2
#define ZQ_ERR_PERMISSION    -3   /* plugin.json 未声明对应权限 */
#define ZQ_ERR_NOT_LOGGED_IN -4
#define ZQ_ERR_NETWORK       -5
#define ZQ_ERR_SERVER        -6   /* Zulip 返回错误，详情见日志 */
#define ZQ_ERR_NOT_FOUND     -7
#define ZQ_ERR_WRONG_CONTEXT -8   /* 例如在 filter_outgoing 之外调用 set_outgoing_content */

/* 日志级别（与 CoolQ 数值一致） */
#define ZQ_LOG_DEBUG 0
#define ZQ_LOG_INFO 10
#define ZQ_LOG_INFO_SUCCESS 11
#define ZQ_LOG_INFO_RECV 12
#define ZQ_LOG_INFO_SEND 13
#define ZQ_LOG_WARNING 20
#define ZQ_LOG_ERROR 30
#define ZQ_LOG_FATAL 40

#define ZQ_CONV_CHANNEL 1   /* 频道话题（群） */
#define ZQ_CONV_DIRECT  2   /* 私聊（含多人、与自己） */

/* zq_message.flags */
#define ZQ_MSG_OUTGOING          0x01  /* 自己发送 */
#define ZQ_MSG_MENTIONED         0x02  /* @我（含用户组） */
#define ZQ_MSG_WILDCARD_MENTION  0x04  /* @全体 / @topic */
#define ZQ_MSG_STARRED           0x08
#define ZQ_MSG_READ              0x10
#define ZQ_MSG_HISTORICAL        0x20  /* 通过 get_messages 获取，而非新消息事件 */

typedef struct zq_conversation {
    int32_t type;                 /* ZQ_CONV_* */
    int32_t dm_user_count;        /* 私聊：对方人数（不含自己），与自己私聊为 0 */
    int64_t stream_id;            /* 频道：频道 id */
    const char* stream_name;      /* 频道：频道名；私聊为 NULL */
    const char* topic;            /* 频道：话题名；私聊为 NULL */
    const int64_t* dm_user_ids;   /* 私聊：对方 id 数组（升序）；频道为 NULL */
} zq_conversation;

typedef struct zq_message {
    uint32_t struct_size;
    uint32_t flags;               /* ZQ_MSG_* */
    int64_t message_id;
    int64_t timestamp;            /* Unix 秒 */
    int64_t sender_id;
    const char* sender_name;
    const char* sender_email;
    zq_conversation conv;
    const char* content_html;     /* 服务器渲染的 HTML */
    const char* content_text;     /* 宿主转换的纯文本 */
    int64_t account_id;           /* 2：消息所属的账号 */
} zq_message;

typedef struct zq_user {
    uint32_t struct_size; int32_t role;      /* 100 所有者 … 600 访客 */
    int64_t user_id; const char* full_name; const char* email;
    int32_t is_bot; int32_t is_active;
    const char* remark;                       /* 本地备注，可能为 NULL */
} zq_user;

typedef struct zq_channel {
    uint32_t struct_size; int32_t is_private;
    int64_t stream_id; const char* name; const char* description;
} zq_channel;

typedef struct zq_topic { uint32_t struct_size; int32_t reserved; int64_t stream_id; int64_t max_message_id; const char* name; } zq_topic;

typedef struct zq_outgoing {
    uint32_t struct_size; uint32_t reserved; zq_conversation conv; const char* content;
    int64_t account_id;           /* 2：发送消息的账号 */
} zq_outgoing;

/* 2：已登录的账号 */
typedef struct zq_account {
    uint32_t struct_size; int32_t is_primary;  /* 1 = 最先登录的账号（回调之外默认使用） */
    int64_t account_id;                         /* 本地账号 id，程序重启后不变 */
    int64_t user_id;                            /* 该账号在其组织中的用户 id */
    const char* full_name; const char* email; const char* realm_url;
} zq_account;

typedef void (ZQ_CALL *zq_message_cb)(const zq_message* msg, void* user);
typedef void (ZQ_CALL *zq_user_cb)(const zq_user* u, void* user);
typedef void (ZQ_CALL *zq_channel_cb)(const zq_channel* c, void* user);
typedef void (ZQ_CALL *zq_topic_cb)(const zq_topic* t, void* user);
typedef void (ZQ_CALL *zq_string_cb)(const char* s, void* user);
typedef void (ZQ_CALL *zq_account_cb)(const zq_account* a, void* user);

typedef struct zq_host_api {
    uint32_t struct_size;
    uint32_t api_version;          /* = ZQ_API_VERSION */
    void* ctx;                     /* 每次调用原样传回 */
    void    (ZQ_CALL *add_log)(void* ctx, int32_t level, const char* category, const char* text);
    int32_t (ZQ_CALL *is_logged_in)(void* ctx);
    int64_t (ZQ_CALL *get_login_user_id)(void* ctx);
    int32_t (ZQ_CALL *get_login_info)(void* ctx, zq_user_cb cb, void* user);         /* 回调 1 次 */
    int32_t (ZQ_CALL *get_realm_url)(void* ctx, zq_string_cb cb, void* user);
    int32_t (ZQ_CALL *get_data_dir)(void* ctx, zq_string_cb cb, void* user);         /* 插件数据目录，以 \ 结尾 */
    int64_t (ZQ_CALL *send_channel_message)(void* ctx, int64_t stream_id, const char* topic, const char* markdown); /* >0 消息 id */
    int64_t (ZQ_CALL *send_direct_message)(void* ctx, const int64_t* user_ids, int32_t count, const char* markdown);
    int32_t (ZQ_CALL *get_message)(void* ctx, int64_t message_id, zq_message_cb cb, void* user);
    /* conv 为 NULL 表示全部；anchor: 0 = 最新，-1 = 最旧，其它 = 消息 id；返回回调条数 */
    int32_t (ZQ_CALL *get_messages)(void* ctx, const zq_conversation* conv, int64_t anchor,
                                    int32_t num_before, int32_t num_after, zq_message_cb cb, void* user);
    int32_t (ZQ_CALL *get_users)(void* ctx, zq_user_cb cb, void* user);
    int32_t (ZQ_CALL *get_user)(void* ctx, int64_t user_id, zq_user_cb cb, void* user);
    int32_t (ZQ_CALL *get_channels)(void* ctx, zq_channel_cb cb, void* user);        /* 已订阅 */
    int32_t (ZQ_CALL *get_topics)(void* ctx, int64_t stream_id, zq_topic_cb cb, void* user);
    int32_t (ZQ_CALL *add_reaction)(void* ctx, int64_t message_id, const char* emoji_name);
    int32_t (ZQ_CALL *set_starred)(void* ctx, int64_t message_id, int32_t starred);
    int32_t (ZQ_CALL *mark_read)(void* ctx, int64_t message_id);
    int32_t (ZQ_CALL *upload_file)(void* ctx, const char* local_path, zq_string_cb cb, void* user); /* 回调 /user_uploads/... */
    int32_t (ZQ_CALL *set_outgoing_content)(void* ctx, const char* markdown);        /* 仅 zq_filter_outgoing 内有效 */
    /* ---- 2：多账号（api_version >= 2 时可用）---- */
    int32_t (ZQ_CALL *get_accounts)(void* ctx, zq_account_cb cb, void* user);        /* 返回已登录的账号数 */
    int64_t (ZQ_CALL *get_current_account)(void* ctx);  /* 宿主函数现在使用的账号；没有账号登录时 ZQ_ERR_NOT_LOGGED_IN */
    /* 选择回调之外使用的账号；在回调中调用时，本次回调剩余的调用也使用该账号。账号未登录时 ZQ_ERR_NOT_FOUND */
    int32_t (ZQ_CALL *select_account)(void* ctx, int64_t account_id);
    /* ---- 3：设置、附加内容、打开、管理（api_version >= 3 时可用）---- */
    int32_t (ZQ_CALL *get_settings)(void* ctx, zq_string_cb cb, void* user);         /* 设置的 JSON 对象（key -> 值），回调 1 次 */
    /* 给消息附加内容（不修改原文，只在本次运行中有效，停用插件时隐藏；需要保留时由插件保存，在 zq_on_messages_shown 中重新附加）：
       {"text": "气泡下方的文字", "replace": "临时替换气泡显示内容的纯文字",
        "actions": [{"label": "按钮", "target": "URI 或程序", "args": "程序参数"} 或 {"label": "按钮", "fill": "填入输入框的文字"}]}
       （最多 5 个按钮）；json 为 NULL 或空字符串表示清除，再次调用覆盖本插件之前的内容。
       使用当前账号（回调中为回调所属账号，否则为 select_account 选择的账号）。
       需要 annotate_message；replace 还需要 render_message（用户可随时切回原文），fill 按钮需要 fill_composer，target 按钮需要 launch */
    int32_t (ZQ_CALL *set_annotation)(void* ctx, int64_t message_id, const char* json);
    /* 打开白名单中的 URI 或运行白名单中的程序（args 只用于程序，可为 NULL）；不在白名单中返回 ZQ_ERR_PERMISSION。需要 launch */
    int32_t (ZQ_CALL *launch)(void* ctx, const char* target, const char* args);
    int32_t (ZQ_CALL *edit_message)(void* ctx, int64_t message_id, const char* markdown);   /* 需要 edit_message */
    int32_t (ZQ_CALL *delete_message)(void* ctx, int64_t message_id);                       /* 需要 delete_message */
    /* 修改频道名称 / 介绍，NULL 表示不修改。需要 manage_channel */
    int32_t (ZQ_CALL *update_channel)(void* ctx, int64_t stream_id, const char* name, const char* description);
    /* 修改用户名称 / 角色（100 所有者、200 管理员、300 版主、400 成员、600 访客），NULL / 0 表示不修改。需要 manage_user */
    int32_t (ZQ_CALL *update_user)(void* ctx, int64_t user_id, const char* full_name, int32_t role);
    /* 回复设置窗口（任何线程；窗口已关闭时忽略）。不需要权限：
       {"key": "设置项", "row": zq_on_settings_button 收到的行号（顶层不写）, "text": "显示在设置项下方的文字",
        "options": ["tags 设置项的候选"]}；
       tags 某一项旁的按钮（item_button）的结果写作 {"key", "row"?, "item": "该项", "status": "ok" | "error", "text": "失败的原因"}，
       显示在该按钮上（点击后宿主先显示加载中） */
    int32_t (ZQ_CALL *set_settings_feedback)(void* ctx, const char* json);
    /* 发布 type 类资源，替换之前发布的；json 为 NULL 或空字符串表示清除。只保存在内存中，请在 init 和设置变化时发布。
       type 只能由小写字母、数字和下划线组成。不需要权限 */
    int32_t (ZQ_CALL *publish_resources)(void* ctx, const char* type, const char* json);
    /* 使用其他插件的资源：{"resource": resource 设置项的值, "request": {...}}；阻塞到发布者回复（cb 回调 1 次）或超时（120 秒，
       ZQ_ERR_NETWORK）。资源不存在或发布者未开启时 ZQ_ERR_NOT_FOUND；不能在回调中（插件线程上）调用（ZQ_ERR_WRONG_CONTEXT）。
       发布者不在 plugin.json 的 dependencies 中时 ZQ_ERR_PERMISSION */
    int32_t (ZQ_CALL *request_resource)(void* ctx, const char* json, zq_string_cb cb, void* user);
    /* 回复 zq_on_resource_request 收到的请求（任何线程）；请求不存在或已超时 ZQ_ERR_NOT_FOUND */
    int32_t (ZQ_CALL *reply_resource)(void* ctx, int64_t request_id, const char* json);
    /* 回复 zq_on_panel（只能在该回调中调用，否则 ZQ_ERR_WRONG_CONTEXT；再次调用覆盖）。不需要权限。
       get / set：{"fields": [{"key", "label", "type": "select", "options": [{"value", "label"}], "value": "选中的值"}
                              或 {"key", "label", "type": "bool", "value": true}]}（没有设置项时不显示分组）；
       list：{"entries": [{"target": 与 zq_on_panel 的 target 相同, "text": "显示在名称后的说明"}]}；remove 不需要回复 */
    int32_t (ZQ_CALL *reply_panel)(void* ctx, const char* json);
} zq_host_api;

/* ---- 插件导出（init 必须，其余可选） ---- */
ZQ_EXPORT int32_t ZQ_CALL zq_plugin_init(const zq_host_api* api);   /* 返回 ZQ_OK 表示成功 */
ZQ_EXPORT int32_t ZQ_CALL zq_on_start(void);     /* 第一个账号登录完成，或有账号登录时启用插件；与 zq_on_exit 成对 */
ZQ_EXPORT int32_t ZQ_CALL zq_on_exit(void);      /* 最后一个账号结束（退出账号或退出程序） */
ZQ_EXPORT int32_t ZQ_CALL zq_on_enable(void);    /* 会话中插件被启用（在 zq_on_start 之后）；与 zq_on_disable 成对 */
ZQ_EXPORT int32_t ZQ_CALL zq_on_disable(void);
ZQ_EXPORT int32_t ZQ_CALL zq_on_message(const zq_message* msg);           /* 新消息回调（含自己发送的消息） */
ZQ_EXPORT int32_t ZQ_CALL zq_filter_incoming(const zq_message* msg);      /* BLOCK = 不显示（本次运行内加载历史时也不显示）、不提醒、标记已读；自己发送的消息不经过 */
ZQ_EXPORT int32_t ZQ_CALL zq_filter_outgoing(const zq_outgoing* out);     /* 在输入框中发送前调用（发送文件、转发不经过）；BLOCK = 取消发送；可改写内容 */
ZQ_EXPORT int32_t ZQ_CALL zq_on_menu(int32_t menu_id);
ZQ_EXPORT int32_t ZQ_CALL zq_on_account_login(int64_t account_id);   /* 2：一个账号登录完成（第一个账号在 zq_on_start 之后） */
ZQ_EXPORT int32_t ZQ_CALL zq_on_account_logout(int64_t account_id);  /* 2：一个账号结束（最后一个账号在 zq_on_exit 之前） */
ZQ_EXPORT int32_t ZQ_CALL zq_on_settings_changed(void);  /* 3：用户保存了插件设置（之后 get_settings 返回新值） */
ZQ_EXPORT int32_t ZQ_CALL zq_on_message_menu(int32_t menu_id, const zq_message* msg);  /* 3：用户点击了 message_menus 中的菜单项 */
/* 3：用户点击了设置中的 button 或 tags 项旁的按钮。耗时操作请放到自己的线程。json：
   {"key": "设置项", "row": 行号（list 的行中才有）, "item": "tags 的项（item_button 才有）",
    "values": 所在一层（顶层或这一行）当前的值, "settings": 全部当前的值}（尚未保存） */
ZQ_EXPORT int32_t ZQ_CALL zq_on_settings_button(const char* json);
/* 3：其他插件请求本插件发布的资源（插件线程）；请尽快返回，在自己的线程上处理后调用 reply_resource。json：
   {"type", "resource": 资源 id, "item", "request": {...}, "from": "使用者的插件 id"} */
ZQ_EXPORT int32_t ZQ_CALL zq_on_resource_request(int64_t request_id, const char* json);
/* 3：宿主需要插件面板的数据（插件线程，2 秒内没有回复时宿主不显示），在回调中用 reply_panel 回复。json：
   {"action": "get" | "set" | "list" | "remove", "kind": "user" | "topic",
    "target": {"account": 账号 id, "user": 用户 id, "name": "显示名"} 或 {"account", "stream": 频道 id, "topic": "话题", "name": "频道 / 话题"},
    "key": "设置项"（set）, "value": 新的值（set）}；list 没有 target。回调中的宿主函数使用 target 所属的账号 */
ZQ_EXPORT int32_t ZQ_CALL zq_on_panel(const char* json);
/* 3：聊天窗口中显示了这些消息（打开会话、加载历史、收到新消息；需要 read_message）：{"account": 账号 id, "messages": [消息 id]}。
   回调中的宿主函数使用该账号，可以用 set_annotation 重新附加插件保存的结果 */
ZQ_EXPORT int32_t ZQ_CALL zq_on_messages_shown(const char* json);
#ifdef __cplusplus
}
#endif
