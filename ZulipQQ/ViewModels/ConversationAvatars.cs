using Avalonia.Media;
using ZulipQQ.Core;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels;

/// <summary>会话头像：话题为频道颜色的文字头像，一对一私聊和与自己的私聊为用户头像，多人私聊为“群”。</summary>
public static class ConversationAvatars
{
    public static (string? Url, string Text, IBrush? Fill) Of(ZulipSession session, ConversationKey key, string title) => key switch
    {
        TopicKey t => (null, title, ConversationItemViewModel.BrushOf(session.Channels.Get(t.StreamId)?.Color)),
        DmKey { IsSelfDm: true } => (session.Users.Get(session.SelfId)?.AvatarUrl, title, null),
        DmKey { OtherUserIds.Length: 1 } d => (session.Users.Get(d.OtherUserIds[0])?.AvatarUrl, title, null),
        _ => (null, "群", ConversationItemViewModel.BrushOf(MessagesPageViewModel.GroupDmAvatarColor)),
    };
}
