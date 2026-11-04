using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using ZulipQQ.ViewModels.Settings;

namespace ZulipQQ.Views.Settings;

public partial class ResourcesPageView : UserControl
{
    public ResourcesPageView()
    {
        InitializeComponent();
    }

    // 点击文件展开 / 收起引用它的消息；点在按钮上时只执行按钮
    private void OnHeaderTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;
        if ((sender as Control)?.DataContext is ResourceItem item)
            item.ToggleCommand.Execute(null);
    }
}
