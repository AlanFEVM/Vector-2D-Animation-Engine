using System.Windows.Forms;

namespace VectorAnimationEngine.DistributionLauncher;

internal sealed class RuntimeDownloadDialog : Form
{
    private readonly string _root;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Label _status;
    private readonly ProgressBar _progress;
    private readonly Button _cancel;
    private bool _running;

    public string? ErrorMessage { get; private set; }

    public RuntimeDownloadDialog(string root)
    {
        _root = root;
        Text = "Vector 2D Animation Engine";
        ClientSize = new Size(520, 154);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;

        var title = new Label
        {
            AutoSize = false,
            Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold),
            Location = new Point(20, 18),
            Size = new Size(480, 24),
            Text = "正在准备首次运行所需的 .NET 8 桌面运行环境"
        };
        _status = new Label
        {
            AutoSize = false,
            Location = new Point(20, 49),
            Size = new Size(480, 22),
            Text = "正在连接 Microsoft 下载服务..."
        };
        _progress = new ProgressBar
        {
            Location = new Point(20, 78),
            Size = new Size(480, 18),
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 24
        };
        _cancel = new Button
        {
            Location = new Point(410, 111),
            Size = new Size(90, 28),
            Text = "取消",
            UseVisualStyleBackColor = true
        };
        _cancel.Click += (_, _) => CancelInstall();
        Controls.AddRange(new Control[] { title, _status, _progress, _cancel });
        Shown += OnShown;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_running && e.CloseReason == CloseReason.UserClosing)
        {
            CancelInstall();
            e.Cancel = true;
        }
        base.OnFormClosing(e);
    }

    private async void OnShown(object? sender, EventArgs e)
    {
        _running = true;
        var progress = new Progress<RuntimeInstallProgress>(UpdateProgress);
        try
        {
            await Task.Run(
                () => RuntimeInstaller.EnsureInstalled(_root, progress, _cancellation.Token),
                _cancellation.Token);
            _running = false;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "运行环境下载已取消。";
            _running = false;
            DialogResult = DialogResult.Cancel;
            Close();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _running = false;
            DialogResult = DialogResult.Abort;
            Close();
        }
    }

    private void UpdateProgress(RuntimeInstallProgress update)
    {
        _status.Text = update.Message;
        if (update.Percent is { } percent)
        {
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = Math.Max(0, Math.Min(100, percent));
        }
        else
        {
            _progress.Style = ProgressBarStyle.Marquee;
        }
    }

    private void CancelInstall()
    {
        if (!_running) return;
        _cancel.Enabled = false;
        _status.Text = "正在取消...";
        _cancellation.Cancel();
    }
}

internal sealed class RuntimeInstallProgress
{
    public RuntimeInstallProgress(string message, int? percent = null)
    {
        Message = message;
        Percent = percent;
    }

    public string Message { get; }
    public int? Percent { get; }
}
