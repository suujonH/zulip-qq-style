using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using ZulipQQ.Core;
using ZulipQQ.Services;

namespace ZulipQQ.Controls;

/// <summary>
/// 显示一条消息的内容：把服务器渲染的 HTML 转成 Avalonia 控件（见 <see cref="ZulipHtmlRenderer"/>）。
/// 链接、文件、图片、提及的点击通过冒泡的 <see cref="LinkClickedEvent"/> 交给聊天视图处理。
/// </summary>
public class MessageContent : Decorator
{
    public static readonly StyledProperty<string?> HtmlProperty =
        AvaloniaProperty.Register<MessageContent, string?>(nameof(Html));

    /// <summary>“/me” 消息的发送者名字；不为 null 时内容以斜体显示，并在前面加上名字。</summary>
    public static readonly StyledProperty<string?> MeSenderProperty =
        AvaloniaProperty.Register<MessageContent, string?>(nameof(MeSender));

    /// <summary>文件卡片读取下载状态用的服务，由聊天视图设置在根元素上，向下继承。</summary>
    public static readonly AttachedProperty<FileService?> FilesProperty =
        AvaloniaProperty.RegisterAttached<MessageContent, Control, FileService?>("Files", inherits: true);

    public static readonly RoutedEvent<MessageLinkEventArgs> LinkClickedEvent =
        RoutedEvent.Register<MessageContent, MessageLinkEventArgs>(nameof(LinkClicked), RoutingStrategies.Bubble);

    public string? Html
    {
        get => GetValue(HtmlProperty);
        set => SetValue(HtmlProperty, value);
    }

    public string? MeSender
    {
        get => GetValue(MeSenderProperty);
        set => SetValue(MeSenderProperty, value);
    }

    public event EventHandler<MessageLinkEventArgs>? LinkClicked
    {
        add => AddHandler(LinkClickedEvent, value);
        remove => RemoveHandler(LinkClickedEvent, value);
    }

    public static FileService? GetFiles(Control c) => c.GetValue(FilesProperty);

    public static void SetFiles(Control c, FileService? value) => c.SetValue(FilesProperty, value);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HtmlProperty || change.Property == MeSenderProperty)
            Rebuild();
    }

    private void Rebuild()
    {
        var html = Html ?? "";
        try
        {
            Child = ZulipHtmlRenderer.Build(html, MeSender);
        }
        catch (Exception ex)
        {
            // 渲染出错时降级为纯文本
            Log.Warn("渲染消息失败", ex);
            Child = new SelectableTextBlock { Text = SafePlainText(html), TextWrapping = TextWrapping.Wrap };
        }
    }

    private static string SafePlainText(string html)
    {
        try
        {
            return ZulipHtml.ToPlainText(html);
        }
        catch (Exception)
        {
            return html;
        }
    }
}
