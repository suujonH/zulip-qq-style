using System.Linq;
using System.Windows.Input;

namespace ZulipQQ.ViewModels.Settings;

/// <summary>
/// Zulip 账号：所有账号（与主窗口导航一致）及选中账号的设置（<see cref="AccountsPanelViewModel"/>），占满设置窗口的右侧。
/// 打开时选中主窗口显示的账号。
/// </summary>
public sealed class AccountsPageViewModel : SettingsPageViewModel
{
    public AccountsPageViewModel(SettingsViewModel owner) : base(owner)
    {
        Panel = new AccountsPanelViewModel(owner);
        var hub = AccountHubViewModel.Instance;
        if ((hub.Active ?? hub.Entries.FirstOrDefault()) is { } entry)
            Panel.Show(entry);
    }

    public AccountsPanelViewModel Panel { get; }

    public override bool FillsWindow => true;

    // 未保存的修改在选中账号的页中，由面板自己显示保存提示；这里只用于切换页、关闭窗口前的确认
    public override bool HasUnsavedChanges => Panel.HasUnsavedChanges;

    public override ICommand? DiscardChangesCommand => Panel.Host?.CurrentPage?.DiscardChangesCommand;

    public override void Close() => Panel.Close();
}
