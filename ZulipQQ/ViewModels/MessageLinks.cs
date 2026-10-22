using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using ZulipQQ.Controls;
using ZulipQQ.Core;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels;

/// <summary>消息内容中链接和文件的点击处理，聊天与聊天记录窗口共用。</summary>
internal static class MessageLinks
{
    /// <summary>
    /// 打开链接：组织内的会话链接（<c>#narrow/…</c>）在客户端中打开并定位；网页和邮件链接用浏览器打开，
    /// 相对链接以组织地址为基准；其它协议忽略。
    /// </summary>
    public static async Task OpenUrlAsync(ZulipSession session, Func<ConversationKey, long?, Task> openChat, string href)
    {
        var realm = session.Client.RealmUrl;
        if (ZulipLinks.TryParse(href, realm, out var link))
        {
            ConversationKey? key = link switch
            {
                { StreamId: { } streamId, Topic: { } topic } => new TopicKey(streamId, topic),
                { DmUserIds: { } ids } => DmKey.Create(ids, session.SelfId),
                _ => null,
            };
            if (key is not null)
            {
                await openChat(key, link.NearMessageId);
                return;
            }
        }

        if (!Uri.TryCreate(realm, href, out var uri) || uri.Scheme is not ("http" or "https" or "mailto"))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"打开链接失败：{uri}", ex);
        }
    }

    /// <summary>文件卡片上的操作：打开（未下载时先下载）、另存为、打开所在文件夹。失败时提示。</summary>
    public static async Task HandleFileAsync(ZulipSession session, FileTarget file, ConversationKey key, long messageId,
        TopLevel? top, Action<string> showToast)
    {
        switch (file.Action)
        {
            case FileAction.Open:
                if (!await session.Files.OpenAsync(file.Link, key, messageId) && !session.IsDisposed)
                    showToast("文件下载失败");
                break;
            case FileAction.SaveAs when top is not null:
                if (!await session.Files.SaveAsAsync(file.Link, top) && !session.IsDisposed)
                    showToast("文件下载失败");
                break;
            case FileAction.RevealInFolder:
                if (session.Files.LocalPath(file.Link.Path) is { } path)
                    FileService.RevealInFolder(path);
                break;
        }
    }

    /// <summary>
    /// 在图片列表中找到点击的那张：同一张图片可能出现在多条消息中，优先匹配所在消息 <paramref name="messageId"/>，
    /// 其次匹配第一次出现；都找不到时只返回它一张。
    /// </summary>
    public static (IReadOnlyList<InlineImage> Images, int Index) LocateImage(
        IReadOnlyList<(long MessageId, InlineImage Image)> images, InlineImage clicked, long? messageId)
    {
        var index = -1;
        for (var i = 0; i < images.Count; i++)
        {
            if (images[i].Image.Src != clicked.Src || images[i].Image.Href != clicked.Href)
                continue;
            if (images[i].MessageId == messageId)
            {
                index = i;
                break;
            }
            if (index < 0)
                index = i;
        }
        return index >= 0 ? (images.Select(i => i.Image).ToList(), index) : ([clicked], 0);
    }
}
