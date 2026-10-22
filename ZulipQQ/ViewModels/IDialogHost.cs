using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace ZulipQQ.ViewModels;

/// <summary>显示对话框和提示的窗口：主窗口，或独立聊天窗口（聊天中的对话框显示在聊天所在的窗口）。</summary>
public interface IDialogHost
{
    /// <summary>打开中的对话框，后打开的显示在上层。</summary>
    ObservableCollection<ViewModelBase> Dialogs { get; }

    /// <summary>窗口顶部居中的提示文字，3 秒后消失。</summary>
    void ShowToast(string text);

    /// <summary>显示确认对话框，返回用户是否点击了确定。</summary>
    Task<bool> ConfirmAsync(string message, string confirmText = "确定", bool isDanger = false);
}
