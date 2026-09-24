using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace GeoJsonEditor.Ui;

/// <summary>
/// 带“用户点击”事件的切换按钮。<see cref="StayChecked"/> 为 true 时像单选按钮一样，
/// 再次点击已选中的按钮不会取消选中（用于工具切换、单选标签）。
/// </summary>
public sealed class ClickToggle : ToggleButton
{
    public bool StayChecked { get; set; }

    /// <summary>用户用鼠标或键盘切换时触发（程序修改 IsChecked 不触发）。</summary>
    public event Action? Clicked;

    public ClickToggle()
    {
    }

    public ClickToggle(string styleName, UIElement content, bool isChecked = false, bool stayChecked = false)
    {
        StyleName = styleName;
        Content = content;
        IsChecked = isChecked;
        StayChecked = stayChecked;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        bool before = IsChecked;
        base.OnMouseUp(e);
        AfterUserToggle(before);
    }

    protected override void ToggleFromKeyboard()
    {
        bool before = IsChecked;
        base.ToggleFromKeyboard();
        AfterUserToggle(before);
    }

    private void AfterUserToggle(bool before)
    {
        if (IsChecked == before) return;
        if (StayChecked && before) IsChecked = true;
        Clicked?.Invoke();
    }
}
