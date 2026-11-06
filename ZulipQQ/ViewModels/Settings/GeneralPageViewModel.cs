using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using ZulipQQ.Core;
using ZulipQQ.Data;
using ZulipQQ.Services;
using ZulipQQ.Zulip;

namespace ZulipQQ.ViewModels.Settings;

/// <summary>报告给服务器的客户端名称的选项；<see cref="Name"/> 为 null 时是自定义。</summary>
public sealed record ClientNameOption(string? Name, string Text)
{
    public static readonly IReadOnlyList<ClientNameOption> All =
    [
        new(ZulipClient.ZulipQQClientName, "ZulipQQ"),
        new(ZulipClient.DesktopClientName, "桌面版"),
        new(ZulipClient.WebClientName, "网页版"),
        new(null, "自定义"),
    ];

    public override string ToString() => Text;
}

/// <summary>
/// 通用（程序级，所有账号共用）：打开聊天的方式、话题标题模板、收起已解决话题、
/// 主界面（边缘隐藏、托盘、关闭行为、经典模式）、字体大小、报告给服务器的客户端名称。
/// </summary>
public sealed partial class GeneralPageViewModel : SettingsPageViewModel
{
    public GeneralPageViewModel(SettingsViewModel owner) : base(owner)
    {
        TitleFormat = Local.TopicTitleFormat;
        TitleDraft = TitleFormat;
        var name = Local.ClientName;
        var preset = ClientNameOption.All.FirstOrDefault(o => o.Name is not null
            && string.Equals(o.Name, name.Length == 0 ? ZulipClient.ZulipQQClientName : name, StringComparison.Ordinal));
        CustomClientName = preset is null ? name : "";
        SelectedClientName = preset ?? ClientNameOption.All[^1];
        Local.Changed += OnChanged;
    }

    public IReadOnlyList<ClientNameOption> ClientNameOptions => ClientNameOption.All;

    /// <summary>选择的客户端名称；选择预设时立即保存，选择自定义时保存输入框中的名称（为空时不改）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomClientName))]
    public partial ClientNameOption SelectedClientName { get; set; } = ClientNameOption.All[0];

    /// <summary>自定义的客户端名称，输入时立即保存（服务器只取第一个“/”或空格之前的部分）。</summary>
    [ObservableProperty]
    public partial string CustomClientName { get; set; } = "";

    public bool IsCustomClientName => SelectedClientName.Name is null;

    /// <summary>实际发送的 User-Agent。</summary>
    public string UserAgentText => "User-Agent：" + ZulipClient.UserAgentFor(Local.ClientName);

    partial void OnSelectedClientNameChanged(ClientNameOption value)
    {
        if (value.Name is { } name)
            Local.ClientName = name;
        else if (CustomClientName.Trim().Length > 0)
            Local.ClientName = CustomClientName;
    }

    partial void OnCustomClientNameChanged(string value)
    {
        if (IsCustomClientName && value.Trim().Length > 0)
            Local.ClientName = value;
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

    /// <summary>用示例的“频道名”“话题名”预览输入框中的模板。</summary>
    public string TitlePreview => TopicTitleFormat.Format(TitleDraft, "频道名", "话题名");

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

    /// <summary>开机自动运行，切换即生效（保存在 Windows 的启动项中）。</summary>
    public bool RunAtStartup
    {
        get => LaunchAtStartup.Current.IsEnabled;
        set
        {
            LaunchAtStartup.Current.Set(value);
            // 修改失败时开关回到实际的状态
            OnPropertyChanged();
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
