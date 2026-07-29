namespace VectorAnimationEngine;

internal sealed class InputFocusDismissalFilter : IMessageFilter
{
    private const int WmLeftButtonDown = 0x0201;
    private const int WmRightButtonDown = 0x0204;
    private const int WmMiddleButtonDown = 0x0207;

    public bool PreFilterMessage(ref Message message)
    {
        if (message.Msg is not (WmLeftButtonDown or WmRightButtonDown or WmMiddleButtonDown)) return false;

        var activeForm = Form.ActiveForm;
        var focusedControl = FindFocusedControl(activeForm);
        if (activeForm is null || focusedControl is null) return false;
        var inputScope = FindInputScope(focusedControl);
        if (inputScope is null) return false;

        var clickedControl = Control.FromChildHandle(message.HWnd);
        // Native combo-box popup windows are not represented by WinForms controls.
        if (clickedControl is null || !ReferenceEquals(clickedControl.FindForm(), activeForm)) return false;
        if (IsDescendantOrSelf(clickedControl, inputScope)) return false;

        ClearActiveControlChain(focusedControl, activeForm);
        return false;
    }

    private static Control? FindFocusedControl(ContainerControl? root)
    {
        Control? focused = root?.ActiveControl;
        while (focused is ContainerControl container && container.ActiveControl is { } child)
        {
            focused = child;
        }

        return focused;
    }

    private static Control? FindInputScope(Control? focusedControl)
    {
        for (var current = focusedControl; current is not null; current = current.Parent)
        {
            if (current is ModernNumericUpDown) return current;
        }

        for (var current = focusedControl; current is not null; current = current.Parent)
        {
            if (current is TextBoxBase or ComboBox or NumericUpDown) return current;
        }

        return null;
    }

    private static bool IsDescendantOrSelf(Control control, Control ancestor)
    {
        for (var current = control; current is not null; current = current.Parent)
        {
            if (ReferenceEquals(current, ancestor)) return true;
        }

        return false;
    }

    private static void ClearActiveControlChain(Control focusedControl, Form activeForm)
    {
        for (var current = focusedControl.Parent; current is not null; current = current.Parent)
        {
            if (current is ContainerControl container) container.ActiveControl = null;
        }

        activeForm.ActiveControl = null;
    }
}
