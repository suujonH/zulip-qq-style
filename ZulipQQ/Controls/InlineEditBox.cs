using System;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace ZulipQQ.Controls;

/// <summary>
/// 行内编辑框：变为可见时自动获得焦点并全选；回车或失去焦点时执行 <see cref="CommitCommand"/>，
/// Esc 执行 <see cref="CancelCommand"/>。命令自身需保证重复执行无副作用（取消后仍会因失去焦点触发提交）。
/// </summary>
public class InlineEditBox : TextBox
{
    public static readonly StyledProperty<ICommand?> CommitCommandProperty =
        AvaloniaProperty.Register<InlineEditBox, ICommand?>(nameof(CommitCommand));

    public static readonly StyledProperty<ICommand?> CancelCommandProperty =
        AvaloniaProperty.Register<InlineEditBox, ICommand?>(nameof(CancelCommand));

    protected override Type StyleKeyOverride => typeof(TextBox);

    public ICommand? CommitCommand
    {
        get => GetValue(CommitCommandProperty);
        set => SetValue(CommitCommandProperty, value);
    }

    public ICommand? CancelCommand
    {
        get => GetValue(CancelCommandProperty);
        set => SetValue(CancelCommandProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // 等布局完成后再聚焦，刚变为可见时控件还不能接收焦点
        if (change.Property == IsVisibleProperty && IsVisible)
            Dispatcher.UIThread.Post(() =>
            {
                Focus();
                SelectAll();
            });
    }

    // 提交 / 取消会隐藏编辑框，焦点随之转移到所在行；等这次按键事件处理完再执行，
    // 否则同一个回车会被新获得焦点的行按钮当成点击
    protected override void OnKeyDown(KeyEventArgs e)
    {
        var command = e.Key switch
        {
            Key.Enter => CommitCommand,
            Key.Escape => CancelCommand,
            _ => null,
        };
        if (command is null)
        {
            base.OnKeyDown(e);
            return;
        }

        e.Handled = true;
        Dispatcher.UIThread.Post(() => Execute(command));
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        Execute(CommitCommand);
    }

    private static void Execute(ICommand? command)
    {
        if (command?.CanExecute(null) == true)
            command.Execute(null);
    }
}
