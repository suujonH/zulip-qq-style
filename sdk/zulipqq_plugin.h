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
 */
#pragma once
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
#define ZQ_API_VERSION 1
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

typedef struct zq_outgoing { uint32_t struct_size; uint32_t reserved; zq_conversation conv; const char* content; } zq_outgoing;

typedef void (ZQ_CALL *zq_message_cb)(const zq_message* msg, void* user);
typedef void (ZQ_CALL *zq_user_cb)(const zq_user* u, void* user);
typedef void (ZQ_CALL *zq_channel_cb)(const zq_channel* c, void* user);
typedef void (ZQ_CALL *zq_topic_cb)(const zq_topic* t, void* user);
typedef void (ZQ_CALL *zq_string_cb)(const char* s, void* user);

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
} zq_host_api;

/* ---- 插件导出（init 必须，其余可选） ---- */
ZQ_EXPORT int32_t ZQ_CALL zq_plugin_init(const zq_host_api* api);   /* 返回 ZQ_OK 表示成功 */
ZQ_EXPORT int32_t ZQ_CALL zq_on_start(void);     /* 会话开始：登录完成，或会话中启用插件时；与 zq_on_exit 成对 */
ZQ_EXPORT int32_t ZQ_CALL zq_on_exit(void);      /* 会话结束（退出账号或退出程序） */
ZQ_EXPORT int32_t ZQ_CALL zq_on_enable(void);    /* 会话中插件被启用（在 zq_on_start 之后）；与 zq_on_disable 成对 */
ZQ_EXPORT int32_t ZQ_CALL zq_on_disable(void);
ZQ_EXPORT int32_t ZQ_CALL zq_on_message(const zq_message* msg);           /* 新消息回调（含自己发送的消息） */
ZQ_EXPORT int32_t ZQ_CALL zq_filter_incoming(const zq_message* msg);      /* BLOCK = 不显示（本次运行内加载历史时也不显示）、不提醒、标记已读；自己发送的消息不经过 */
ZQ_EXPORT int32_t ZQ_CALL zq_filter_outgoing(const zq_outgoing* out);     /* 在输入框中发送前调用（发送文件、转发不经过）；BLOCK = 取消发送；可改写内容 */
ZQ_EXPORT int32_t ZQ_CALL zq_on_menu(int32_t menu_id);
#ifdef __cplusplus
}
#endif
