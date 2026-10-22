/*
 * EchoC：ZulipQQ 原生插件的 C 语言示例。
 * - 私聊收到 "ping" 时回复 "pong"。
 * - 初始化时用 add_log 打印各结构体的大小与关键偏移，用于核对宿主的 ABI。
 *
 * 编译：build.cmd（需要 Visual Studio 的 x64 C 编译器），然后把 echo.dll 与 plugin.json
 * 一起复制到 <程序目录>\plugins\com.zulipqq.echo-c\。
 */
#include <stddef.h>
#include <stdio.h>
#include <string.h>
#include "../../../sdk/zulipqq_plugin.h"

static const zq_host_api* g_api;

static void log_text(int32_t level, const char* text)
{
    g_api->add_log(g_api->ctx, level, "EchoC", text);
}

ZQ_EXPORT int32_t ZQ_CALL zq_plugin_init(const zq_host_api* api)
{
    char buf[512];
    if (api == NULL || api->api_version < ZQ_API_VERSION || api->struct_size < sizeof(zq_host_api))
        return ZQ_ERR_INVALID_ARG;
    g_api = api;

    snprintf(buf, sizeof buf,
        "sizeof: conversation=%zu message=%zu user=%zu channel=%zu topic=%zu outgoing=%zu host_api=%zu",
        sizeof(zq_conversation), sizeof(zq_message), sizeof(zq_user), sizeof(zq_channel),
        sizeof(zq_topic), sizeof(zq_outgoing), sizeof(zq_host_api));
    log_text(ZQ_LOG_DEBUG, buf);
    snprintf(buf, sizeof buf,
        "offsetof: conversation.stream_id=%zu conversation.dm_user_ids=%zu message.conv=%zu "
        "message.content_html=%zu message.content_text=%zu user.is_bot=%zu user.remark=%zu "
        "channel.name=%zu topic.name=%zu outgoing.conv=%zu outgoing.content=%zu "
        "host_api.ctx=%zu host_api.add_log=%zu host_api.set_outgoing_content=%zu",
        offsetof(zq_conversation, stream_id), offsetof(zq_conversation, dm_user_ids),
        offsetof(zq_message, conv), offsetof(zq_message, content_html), offsetof(zq_message, content_text),
        offsetof(zq_user, is_bot), offsetof(zq_user, remark), offsetof(zq_channel, name), offsetof(zq_topic, name),
        offsetof(zq_outgoing, conv), offsetof(zq_outgoing, content),
        offsetof(zq_host_api, ctx), offsetof(zq_host_api, add_log), offsetof(zq_host_api, set_outgoing_content));
    log_text(ZQ_LOG_DEBUG, buf);
    return ZQ_OK;
}

ZQ_EXPORT int32_t ZQ_CALL zq_on_start(void)
{
    log_text(ZQ_LOG_INFO_SUCCESS, "已启动");
    return ZQ_OK;
}

ZQ_EXPORT int32_t ZQ_CALL zq_on_message(const zq_message* msg)
{
    int64_t id;
    char buf[64];
    /* 只处理别人发来的一对一私聊 */
    if ((msg->flags & ZQ_MSG_OUTGOING) || msg->conv.type != ZQ_CONV_DIRECT || msg->conv.dm_user_count != 1)
        return ZQ_EVENT_IGNORE;
    if (msg->content_text == NULL || strcmp(msg->content_text, "ping") != 0)
        return ZQ_EVENT_IGNORE;

    log_text(ZQ_LOG_INFO_RECV, "收到 ping");
    id = g_api->send_direct_message(g_api->ctx, msg->conv.dm_user_ids, 1, "pong");
    if (id > 0)
    {
        snprintf(buf, sizeof buf, "已回复 pong（消息 %lld）", (long long)id);
        log_text(ZQ_LOG_INFO_SEND, buf);
    }
    else
    {
        snprintf(buf, sizeof buf, "回复失败：%lld", (long long)id);
        log_text(ZQ_LOG_WARNING, buf);
    }
    return ZQ_EVENT_IGNORE;
}
