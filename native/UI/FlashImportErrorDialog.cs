using System.Reflection;
using System.Text;

namespace VectorAnimationEngine;

internal sealed class FlashImportErrorDialog : ModernDialogForm
{
    private readonly TextBox _summary;
    private readonly TextBox _details;
    private readonly Label _copyStatus;
    private readonly List<AnimatedContextMenuStrip> _menus = [];

    internal FlashImportErrorDialog(Exception exception, string sourcePath, string? destinationPath, string stage)
        : base(UiLocalization.T("Flash Import"), new Size(820, 600))
    {
        AccessibleName = UiLocalization.T("Flash import error details");
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty, BackColor = Theme.Panel
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        _summary = CreateTextBox(UiLocalization.T("The Flash project could not be imported.")
            + "\r\n\r\n" + exception.Message, true, "Flash import failure reason");
        _summary.ForeColor = Theme.Danger;
        _details = CreateTextBox(BuildReport(exception, sourcePath, destinationPath, stage), false, "Flash import error details");
        _copyStatus = new Label
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Muted,
            AutoEllipsis = true, UseMnemonic = false, Margin = Padding.Empty,
            Text = UiLocalization.T("Select text and press Ctrl+C, or copy the complete report."), AccessibleRole = AccessibleRole.StaticText
        };
        layout.Controls.Add(_summary, 0, 0);
        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill, Text = UiLocalization.T("Diagnostic report (including stack trace)"),
            ForeColor = Theme.Text, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false, Margin = Padding.Empty
        }, 0, 1);
        layout.Controls.Add(_details, 0, 2);
        layout.Controls.Add(_copyStatus, 0, 3);
        DialogContent.Controls.Add(layout);
        var close = AddDialogAction(UiLocalization.T("OK"), DialogResult.OK, DialogActionStyle.Primary);
        var copy = AddDialogAction(UiLocalization.T("Copy report"), DialogResult.None, canClose: () => { CopyReport(); return false; });
        copy.Width = 136;
        copy.AccessibleName = UiLocalization.T("Copy report");
        AcceptButton = close;
        CancelButton = close;
    }

    internal static string BuildReport(Exception exception, string sourcePath, string? destinationPath, string stage)
    {
        var assembly = typeof(FlashDocumentImporter).Assembly;
        var report = new StringBuilder();
        report.AppendLine(UiLocalization.T("Flash import error details"));
        report.AppendLine($"{UiLocalization.T("Failure stage")}: {UiLocalization.T(stage)}");
        report.AppendLine($"{UiLocalization.T("Source file")}: {sourcePath}");
        if (!string.IsNullOrWhiteSpace(destinationPath)) report.AppendLine($"{UiLocalization.T("Destination folder")}: {destinationPath}");
        report.AppendLine($"UTC: {DateTime.UtcNow:O}");
        report.AppendLine($"Version: {assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? assembly.GetName().Version?.ToString()}");
        report.AppendLine($"Assembly: {assembly.Location}");
        report.AppendLine($"Build ID (MVID): {assembly.ManifestModule.ModuleVersionId}");
        report.AppendLine($"Process: {Environment.ProcessId}; Runtime: {Environment.Version}; OS: {Environment.OSVersion}");
        if (!string.IsNullOrWhiteSpace(AppLog.LogPath)) report.AppendLine($"{UiLocalization.T("Log file")}: {AppLog.LogPath}");
        report.AppendLine();
        report.AppendLine(UiLocalization.T("Full exception and stack trace"));
        report.AppendLine(exception.ToString());
        if (string.IsNullOrWhiteSpace(exception.StackTrace) && exception.InnerException is null)
            report.AppendLine(UiLocalization.T("No stack trace was captured for this exception."));
        return report.ToString();
    }

    private TextBox CreateTextBox(string text, bool wrap, string accessibleName)
    {
        var box = new TextBox
        {
            Text = text, Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = wrap,
            ScrollBars = wrap ? ScrollBars.Vertical : ScrollBars.Both, BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.Field, ForeColor = Theme.Text, Font = Theme.UiFont(), HideSelection = false,
            ShortcutsEnabled = true, MaxLength = int.MaxValue, Margin = Padding.Empty,
            AccessibleName = UiLocalization.T(accessibleName)
        };
        var menu = new AnimatedContextMenuStrip();
        var copy = new ToolStripMenuItem(UiLocalization.T("Copy selected text"), null, (_, _) => box.Copy());
        menu.Items.Add(copy);
        menu.Items.Add(new ToolStripMenuItem(UiLocalization.T("Select all"), null, (_, _) => box.SelectAll()));
        menu.Opening += (_, _) => copy.Enabled = box.SelectionLength > 0;
        box.ContextMenuStrip = menu;
        _menus.Add(menu);
        return box;
    }

    private void CopyReport()
    {
        try
        {
            Clipboard.SetText(_details.Text, TextDataFormat.UnicodeText);
            _copyStatus.Text = UiLocalization.T("Report copied.");
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.ExternalException or System.Threading.ThreadStateException)
        {
            AppLog.Warn($"Could not copy Flash import report: {exception.Message}");
            _copyStatus.Text = UiLocalization.T("Clipboard is unavailable. Select the text and try Ctrl+C again.");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) foreach (var menu in _menus) menu.Dispose();
        base.Dispose(disposing);
    }
}
