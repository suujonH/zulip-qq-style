using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ZulipQQ.ViewModels;
using ZulipQQ.ViewModels.Settings;

namespace ZulipQQ.Views.Settings;

/// <summary>“Zulip 账号”页：账号列表、选中账号的设置导航与当前页（导航的二级列表与页面滚动联动）。</summary>
public partial class AccountsPageView : UserControl
{
    private readonly SettingsSectionNav _sections;
    private AccountsPanelViewModel? _panel;

    public AccountsPageView()
    {
        InitializeComponent();
        _sections = new SettingsSectionNav(PageScroller, () => _panel?.Host?.Nav.FirstOrDefault(n => n.IsSelected));
        NavList.AddHandler(Button.ClickEvent, OnNavClick);
        // 分组标题不能选中
        AccountList.ContainerPrepared += (_, e) => e.Container.IsEnabled = AccountList.Items[e.Index] is not AccountGroupHeader;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Attach((DataContext as AccountsPageViewModel)?.Panel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Attach(null);
        _sections.Detach();
    }

    private void Attach(AccountsPanelViewModel? panel)
    {
        if (_panel == panel)
            return;
        if (_panel is not null)
            _panel.PropertyChanged -= OnPanelPropertyChanged;
        _panel = panel;
        if (panel is not null)
        {
            panel.PropertyChanged += OnPanelPropertyChanged;
            _sections.Refresh();
        }
    }

    // 换账号或换页后回到顶部，重新收集二级列表
    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AccountsPanelViewModel.Host) or nameof(AccountsPanelViewModel.Page))
        {
            PageScroller.Offset = default;
            _sections.Refresh();
        }
    }

    // 账号列表的右键菜单：退出（在线的账号）
    private void OnLogoutClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is AccountEntryViewModel entry)
            _panel?.LogoutCommand.Execute(entry);
    }

    // 账号列表的右键菜单：删除账号（确认后删除登录状态，可选同时删除所有本地数据）
    private void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is AccountEntryViewModel entry)
            _panel?.DeleteCommand.Execute(entry);
    }

    private void OnNavClick(object? sender, RoutedEventArgs e)
    {
        if ((e.Source as Control)?.DataContext is SettingsNavSection section)
            _sections.ScrollTo(section);
    }
}
