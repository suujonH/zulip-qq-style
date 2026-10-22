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

public sealed record MentionTarget(long UserId) : MessageLinkTarget;

public enum FileAction
{
    Open,
    SaveAs,
    RevealInFolder,
}

public sealed class MessageLinkEventArgs(RoutedEvent routedEvent, MessageLinkTarget target) : RoutedEventArgs(routedEvent)
{
    public MessageLinkTarget Target { get; } = target;
}
