using System;
using System.Windows.Forms;

// Marks settings for the menu's Closing handler and refreshes check marks
// after the action. Normal commands retain standard dismissal behavior.
sealed class KeepOpenMenuItem : ToolStripMenuItem
{
    readonly Action refresh;

    public KeepOpenMenuItem(string text, EventHandler clicked, Action refresh)
        : base(text, null, clicked)
    {
        this.refresh = refresh;
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        refresh();
    }
}
