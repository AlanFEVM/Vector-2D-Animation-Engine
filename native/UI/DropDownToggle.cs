using System.Runtime.CompilerServices;

namespace VectorAnimationEngine;

// Buttons that anchor a dropdown should toggle it: click once to open, click
// again to collapse. An open ToolStripDropDown auto-closes on the outside
// click before the owner button's Click handler runs, so the handler can no
// longer rely on Visible alone — it would reopen the just-closed popup. A
// close recorded within the guard window belongs to the same click and is
// treated as the collapse request.
internal static class DropDownToggle
{
    private const int ReopenGuardMilliseconds = 250;
    private static readonly ConditionalWeakTable<ToolStripDropDown, CloseTick> CloseTicks = new();

    private sealed class CloseTick
    {
        public long Value;
    }

    public static void Toggle(ToolStripDropDown dropDown, Action show)
    {
        if (!CloseTicks.TryGetValue(dropDown, out var tick))
        {
            tick = new CloseTick();
            CloseTicks.Add(dropDown, tick);
            dropDown.Closed += (_, _) => tick.Value = Environment.TickCount64;
        }
        if (Environment.TickCount64 - tick.Value < ReopenGuardMilliseconds) return;
        if (dropDown.Visible)
        {
            dropDown.Close();
            return;
        }
        show();
    }
}
