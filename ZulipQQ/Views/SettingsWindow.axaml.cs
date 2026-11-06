using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ZulipQQ.Services;
using ZulipQQ.ViewModels;
using ZulipQQ.ViewModels.Settings;

namespace ZulipQQ.Views;

/// <summary>设置窗口：只允许打开一个，再次打开时切换到对应页。</summary>
public partial class SettingsWindow : Window, ISessionWindow
{
    private static SettingsWindow? _current;

    private readonly SettingsSectionNav _sections;
    private SettingsViewModel? _vm;

    // 已确认（无修改或已放弃修改），关闭时不再询问
    private bool _closeConfirmed;

    public SettingsWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnEscapeKeyDown, RoutingStrategies.Bubble);
        _sections = new SettingsSectionNav(PageScroller, () => _vm?.Nav.FirstOrDefault(n => n.IsSelected));
        NavList.AddHandler(Button.ClickEvent, OnNavClick);
    }

    /// <summary>打开设置窗口并切换到 <paramref name="page"/>；已打开时还原并激活（当前页有未保存的修改时先确认）。</summary>
    public static async void ShowSingle(Window? owner, SettingsPage page = SettingsPage.General) =>
        await OpenAsync(owner, page);

    /// <summary>打开设置窗口，切换到“Zulip 账号”，选中 <paramref name="session"/> 的账号并打开它的 <paramref name="page"/>。</summary>
    public static async void ShowAccountPage(Window? owner, ZulipSession session, SettingsPage page)
    {
        var vm = await OpenAsync(owner, SettingsPage.Accounts);
        // 当前页有未保存的修改、用户选择留下时不切换
        if (vm.CurrentPage is AccountsPageViewModel accounts
            && AccountHubViewModel.Instance.Entries.FirstOrDefault(e => e.Session == session) is { } entry)
            await accounts.Panel.ShowAsync(entry, page);
    }

    private static async Task<SettingsViewModel> OpenAsync(Window? owner, SettingsPage page)
    {
        if (_current is { _vm: { } existing })
        {
            WindowPlacement.BringToFront(_current);
            await existing.NavigateAsync(page);
            return existing;
        }

        // 程序级设置不属于任何账号：账号退出时不关闭
        var vm = new SettingsViewModel();
        var window = new SettingsWindow { _vm = vm };
        window.DataContext = vm;
        vm.PropertyChanged += window.OnVmPropertyChanged;
        _current = window;
        WindowPlacement.CenterOn(window, owner);
        window.Show();
        vm.Navigate(page);
        return vm;
    }

    /// <summary>有未保存的修改时询问；可以关闭时关闭窗口并返回 true。</summary>
    private async Task<bool> ConfirmCloseAsync()
    {
        if (_vm is not null && !await _vm.ConfirmLeaveAsync())
            return false;
        _closeConfirmed = true;
        if (_current == this)
            _current = null;
        Close();
        return true;
    }

    // 用户关闭（×、Esc、Alt+F4）时有未保存的修改先确认；退出账号、退出程序、系统关机时直接关闭
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed || e.CloseReason == WindowCloseReason.OSShutdown || App.IsEndingSession
            || _vm is not { CurrentPage.HasUnsavedChanges: true })
            return;
        e.Cancel = true;
        _ = ConfirmCloseAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _sections.Detach();
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.Close();
        }
        if (_current == this)
            _current = null;
    }

    // 换页后回到顶部，重新收集二级列表
    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.CurrentPage))
        {
            PageScroller.Offset = default;
            _sections.Refresh();
        }
    }

    private void OnNavClick(object? sender, RoutedEventArgs e)
    {
        if ((e.Source as Control)?.DataContext is SettingsNavSection section)
            _sections.ScrollTo(section);
    }

    // Esc 关闭窗口；冒泡阶段处理，先让行内编辑框（取消编辑）、下拉框等子控件处理；
    // 有对话框（包括“Zulip 账号”中选中账号的页面的对话框）时由对话框自己处理
    private void OnEscapeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _vm is not { Dialogs.Count: 0 }
            || _vm.CurrentPage is AccountsPageViewModel { Panel.Host.Dialogs.Count: > 0 })
            return;
        e.Handled = true;
        Close();
    }
}
