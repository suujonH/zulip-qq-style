using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using ZulipQQ.Plugins;

namespace ZulipQQ.Controls;

/// <summary>
/// 录制快捷操作的输入框（只读）：点击获得焦点后，鼠标模式（<see cref="IsMouse"/>）在框内按下鼠标录制
/// “修饰键 + 按键 + 单击 / 双击”，键盘模式按下组合键录制；Backspace / Delete 清除，Esc 结束录制。
/// 录制到的值写入 <see cref="Gesture"/>（双向绑定，格式见 <see cref="PluginShortcuts"/>），框内显示它的文字。
/// </summary>
public class ShortcutRecorder : TextBox
{
    public static readonly StyledProperty<string> GestureProperty =
        AvaloniaProperty.Register<ShortcutRecorder, string>(nameof(Gesture), "", defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> IsMouseProperty =
        AvaloniaProperty.Register<ShortcutRecorder, bool>(nameof(IsMouse));

    // 获得焦点后还没有录制：显示提示
    private bool _waiting;

    public ShortcutRecorder()
    {
        IsReadOnly = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        ContextFlyout = null;
        ContextMenu = null;
        UpdateText();
    }

    protected override Type StyleKeyOverride => typeof(TextBox);

    public string Gesture
    {
        get => GetValue(GestureProperty);
        set => SetValue(GestureProperty, value);
    }

    public bool IsMouse
    {
        get => GetValue(IsMouseProperty);
        set => SetValue(IsMouseProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == GestureProperty || change.Property == IsMouseProperty)
            UpdateText();
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        _waiting = true;
        UpdateText();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _waiting = false;
        UpdateText();
    }

    // 没有焦点时的按下只用于获得焦点；有焦点时录制（双击的第二次按下覆盖第一次的单击）
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!IsFocused || !IsMouse)
        {
            base.OnPointerPressed(e);
            return;
        }
        e.Handled = true;
        var button = e.GetCurrentPoint(this).Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonPressed => MouseButton.Left,
            PointerUpdateKind.MiddleButtonPressed => MouseButton.Middle,
            PointerUpdateKind.RightButtonPressed => MouseButton.Right,
            _ => MouseButton.None,
        };
        if (PluginShortcuts.MouseGesture(button, e.ClickCount, e.KeyModifiers) is { } gesture)
            Record(gesture);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (IsFocused && IsMouse)
            e.Handled = true;
        else
            base.OnPointerReleased(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        switch (e.Key)
        {
            case Key.Escape when e.KeyModifiers == KeyModifiers.None:
                TopLevel.GetTopLevel(this)?.Focus();
                return;
            case Key.Back or Key.Delete when e.KeyModifiers == KeyModifiers.None:
                Record("");
                return;
            case Key.Tab when e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift:
                e.Handled = false;
                base.OnKeyDown(e);
                return;
        }
        if (!IsMouse && PluginShortcuts.KeyGestureOf(e.Key, e.KeyModifiers) is { } gesture)
            Record(gesture);
    }

    // 不接受文字输入
    protected override void OnTextInput(TextInputEventArgs e) => e.Handled = true;

    private void Record(string gesture)
    {
        _waiting = false;
        Gesture = gesture;
        // 值没有变化（或被视图模型拒绝而恢复）时也刷新显示
        UpdateText();
    }

    private void UpdateText()
    {
        if (_waiting)
            Text = IsMouse ? "在框内按下鼠标…" : "按下快捷键…";
        else if (Gesture.Length == 0)
            Text = "无";
        else
            Text = IsMouse ? PluginShortcuts.MouseLabel(Gesture) : Gesture;
    }
}
