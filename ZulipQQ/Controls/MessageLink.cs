using Avalonia.Interactivity;
using ZulipQQ.Core;

namespace ZulipQQ.Controls;

/// <summary>消息内容中可点击的目标。</summary>
public abstract record MessageLinkTarget;

/// <summary>普通链接（可能是组织内的 <c>#narrow/</c> 链接或相对路径）。</summary>
public sealed record UrlTarget(string Href) : MessageLinkTarget;

/// <summary>上传文件；<see cref="Action"/> 为文件卡片上选择的操作。</summary>
public sealed record FileTarget(UploadLink Link, FileAction Action = FileAction.Open) : MessageLinkTarget;

/// <summary>图片预览：<see cref="Href"/> 为原图或原链接。</summary>
public sealed record ImageTarget(string Src, string Href) : MessageLinkTarget;

/// <summary>提及（用户、全体成员 / 话题参与者、用户组）。</summary>
public abstract record AnyMentionTarget : MessageLinkTarget;

public sealed record MentionTarget(long UserId) : AnyMentionTarget;

/// <summary>@全体成员（all、everyone、channel、stream）或 @话题参与者（topic）；<see cref="Name"/> 为 Markdown 中的名字。</summary>
public sealed record WildcardMentionTarget(string Name) : AnyMentionTarget;

public sealed record GroupMentionTarget(string Name) : AnyMentionTarget;

public enum FileAction
{
    /// <summary>左键点击：与 <see cref="Open"/> 相同，以只读方式直接打开（见 <see cref="Services.FileService.OpenAsync"/>）。</summary>
    Click,
    Open,

    /// <summary>以原文件名保存到“接收或下载的文件保存到”的目录。</summary>
    Save,
    SaveAs,
    RevealInFolder,
}

public sealed class MessageLinkEventArgs(RoutedEvent routedEvent, MessageLinkTarget target) : RoutedEventArgs(routedEvent)
{
    public MessageLinkTarget Target { get; } = target;
}
