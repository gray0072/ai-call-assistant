using System.Runtime.InteropServices;

namespace AiCallAssistant;

/// <summary>
/// Shared bits for the windows. All of them are laid out with layout panels and auto-size in
/// 96-DPI units and scale with <see cref="AutoScaleMode.Dpi"/>, so they stay sharp and proportionate
/// on 4K screens and when dragged between monitors with different scaling.
/// </summary>
static class Ui
{
    /// <summary>Call first in a form constructor, before adding controls.</summary>
    public static void Prepare(Form form)
    {
        form.SuspendLayout();
        form.AutoScaleDimensions = new SizeF(96F, 96F);
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.Font = new Font("Segoe UI", 9F);
        form.Icon = AppIcon.Icon;
    }

    /// <summary>Call last in a form constructor.</summary>
    public static void Done(Form form)
    {
        form.ResumeLayout(false);
        form.PerformLayout();
    }

    public static Label Label(string text = "", bool bold = false)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 6, 3, 6)
        };
        if (bold)
            label.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        return label;
    }

    public static Button Button(string text, EventHandler? click = null)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(80, 0),
            Padding = new Padding(6, 1, 6, 1),
            Anchor = AnchorStyles.Left
        };
        if (click != null)
            button.Click += click;
        return button;
    }

    /// <summary>True if the box is scrolled to the end (so new text should keep it there).</summary>
    public static bool IsAtBottom(RichTextBox box)
    {
        var info = new ScrollInfo { cbSize = (uint)Marshal.SizeOf<ScrollInfo>(), fMask = SifAll };
        if (!GetScrollInfo(box.Handle, SbVert, ref info) || info.nPage == 0)
            return true;
        return info.nPos + info.nPage >= info.nMax - 2;
    }

    public static void ScrollToEnd(RichTextBox box)
    {
        box.SelectionStart = box.TextLength;
        box.SelectionLength = 0;
        box.ScrollToCaret();
    }

    /// <summary>Small dialog with one (optionally multi-line) text field; null if cancelled.</summary>
    public static string? Prompt(IWin32Window? owner, string title, string label, string? text, bool multiline)
    {
        using var form = new Form
        {
            Text = title,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            FormBorderStyle = multiline ? FormBorderStyle.Sizable : FormBorderStyle.FixedDialog
        };
        Prepare(form);

        var box = new TextBox
        {
            Text = text ?? "",
            Multiline = multiline,
            AcceptsReturn = multiline,
            ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None,
            Dock = DockStyle.Fill,
            MinimumSize = new Size(480, multiline ? 220 : 0)
        };
        var ok = Button("OK");
        ok.DialogResult = DialogResult.OK;
        var cancel = Button(Strings.Current.Cancel);
        cancel.DialogResult = DialogResult.Cancel;
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.AddRange(new Control[] { cancel, ok });

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var caption = Label(label);
        caption.MaximumSize = new Size(480, 0);
        layout.Controls.Add(caption, 0, 0);
        layout.Controls.Add(box, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        form.Controls.Add(layout);
        form.AcceptButton = multiline ? null : ok;
        form.CancelButton = cancel;
        form.ClientSize = new Size(520, multiline ? 360 : 140);
        Done(form);

        return form.ShowDialog(owner) == DialogResult.OK ? box.Text : null;
    }

    const int SbVert = 1;
    const uint SifAll = 0x17;

    [StructLayout(LayoutKind.Sequential)]
    struct ScrollInfo
    {
        public uint cbSize, fMask;
        public int nMin, nMax;
        public uint nPage;
        public int nPos, nTrackPos;
    }

    [DllImport("user32.dll")]
    static extern bool GetScrollInfo(IntPtr hwnd, int bar, ref ScrollInfo info);
}
