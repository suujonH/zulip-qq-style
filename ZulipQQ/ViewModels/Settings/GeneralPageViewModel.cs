using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using ZulipQQ.Core;
using ZulipQQ.Data;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels.Settings;

/// <summary>
/// 通用（程序级，所有账号共用）：打开聊天的方式、话题标题模板、收起已解决话题、通知类型、
/// 主界面（边缘隐藏、托盘、关闭行为、经典模式）、字体大小。
/// </summary>
public sealed partial class GeneralPageViewModel : SettingsPageViewModel
{
    public GeneralPageViewModel(SettingsViewModel owner) : base(owner)
    {
        TitleFormat = Local.TopicTitleFormat;
        TitleDraft = TitleFormat;
        Local.Changed += OnChanged;
    }

    private LocalSettings Local => Owner.Local;

    public bool IsOpenChatInMain
    {
        get => !Local.OpenChatInNewWindow;
        set
        {
            if (value)
                Local.OpenChatInNewWindow = false;
        }
    }

    public bool IsOpenChatInWindow
    {
        get => Local.OpenChatInNewWindow;
        set
        {
            if (value)
                Local.OpenChatInNewWindow = true;
        }
    }

    /// <summary>经典模式下聊天总是在独立窗口中打开，打开方式不可选。</summary>
    public bool CanChooseOpenChat => !Local.ClassicMode;

    /// <summary>话题标题模板，视图提交（回车、点击输入框外、失去焦点）时保存；清空时按默认（只显示话题名）显示。</summary>
    [ObservableProperty]
    public partial string TitleFormat { get; set; } = "";

    /// <summary>输入框中正在编辑的模板：输入时立即更新预览，提交时才保存为 <see cref="TitleFormat"/>。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TitlePreview))]
    public partial string TitleDraft { get; set; } = "";

    /// <summary>用主窗口显示的账号最近的一个话题会话预览输入框中的模板；没有在线的账号或还没有话题会话时为空。</summary>
    public string TitlePreview =>
        AccountHubViewModel.Instance.Active?.Session is { IsDisposed: false } session
        && session.Conversations.Entries.Where(e => e.Key is TopicKey).MaxBy(e => e.LastMessageTime)?.Key is TopicKey latest
            ? "预览：" + TopicTitleFormat.Format(TitleDraft, session.Channels.Get(latest.StreamId)?.Name ?? "",
                session.Conversations.GetTitle(latest))
            : "";

    partial void OnTitleFormatChanged(string value)
    {
        Local.TopicTitleFormat = value;
        TitleDraft = value;
    }

    /// <summary>收起已解决话题，切换即保存。</summary>
    public bool CollapseResolvedTopics
    {
        get => Local.CollapseResolvedTopics;
        set => Local.CollapseResolvedTopics = value;
    }

    public bool IsBanner
    {
        get => Local.NotificationStyle == NotificationStyle.Banner;
        set
        {
            if (value)
                Local.NotificationStyle = NotificationStyle.Banner;
        }
    }

    public bool IsTrayOnly
    {
        get => Local.NotificationStyle == NotificationStyle.TrayOnly;
        set
        {
            if (value)
                Local.NotificationStyle = NotificationStyle.TrayOnly;
        }
    }

    public bool EdgeAutoHide
    {
        get => Local.EdgeAutoHide;
        set => Local.EdgeAutoHide = value;
    }

    public bool ShowTrayIcon
    {
        get => Local.ShowTrayIcon;
        set => Local.ShowTrayIcon = value;
    }

    /// <summary>托盘图标隐藏时只能退出程序，关闭行为不可选（显示为“退出程序”）。</summary>
    public bool IsCloseToTray
    {
        get => Local.EffectiveCloseAction == CloseAction.MinimizeToTray;
        set
        {
            if (value)
                Local.CloseAction = CloseAction.MinimizeToTray;
        }
    }

    public bool IsCloseExit
    {
        get => Local.EffectiveCloseAction == CloseAction.Exit;
        set
        {
            if (value)
                Local.CloseAction = CloseAction.Exit;
        }
    }

    public bool ClassicMode
    {
        get => Local.ClassicMode;
        set => Local.ClassicMode = value;
    }

    public double MinFontSize => ThemeService.MinFontSize;

    public double MaxFontSize => ThemeService.MaxFontSize;

    /// <summary>正文字号；滑块拖动时立即生效。</summary>
    public double FontSize
    {
        get => Local.FontSize;
        set => Local.FontSize = (int)Math.Round(value);
    }

    public override void Close()
    {
        Local.Changed -= OnChanged;
    }

    private void OnChanged(object? sender, string name) => OnPropertyChanged(string.Empty);
}
