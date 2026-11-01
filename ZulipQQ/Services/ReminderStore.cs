using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ZulipQQ.Zulip;
using ZulipQQ.Zulip.Dtos;

namespace ZulipQQ.Services;

/// <summary>
/// 还没到时间的消息提醒：register 时载入，reminders 事件增删（到时间后服务器发送 remove 事件，
/// 并由 Notification Bot 私信自己，见 <see cref="SystemNoticeStore"/>）。删除不做乐观更新，等待事件。只在 UI 线程上读写。
/// </summary>
public sealed class ReminderStore(ZulipSession session)
{
    private readonly Dictionary<long, ReminderDto> _items = [];

    /// <summary>提醒有增删（或重新载入）。</summary>
    public event EventHandler? Changed;

    /// <summary>待提醒，按提醒时间先后。</summary>
    public IReadOnlyList<ReminderDto> Pending => _items.Values.OrderBy(r => r.ScheduledDeliveryTimestamp).ToList();

    public void Load(RegisterResult reg)
    {
        _items.Clear();
        foreach (var r in reg.Reminders)
            _items[r.ReminderId] = r;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void OnEvent(JsonElement ev)
    {
        switch (ev.GetProperty("op").GetString())
        {
            case "add":
                foreach (var r in ZulipJson.Deserialize<List<ReminderDto>>(ev.GetProperty("reminders")))
                    _items[r.ReminderId] = r;
                break;
            case "remove":
                _items.Remove(ev.GetProperty("reminder_id").GetInt64());
                break;
            default:
                return;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task DeleteAsync(long id) => session.Client.DeleteReminderAsync(id, session.Token);
}
