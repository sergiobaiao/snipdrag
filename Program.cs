using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace SnipDrag;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, "SnipDrag.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }
}

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly Icon _appIcon;
    private readonly NotifyIcon _notifyIcon;
    private readonly ClipboardWatcher _clipboardWatcher;
    private readonly HashSet<string> _recentImageHashes = [];
    private AppSettings _settings;
    private ThumbnailForm? _activeThumbnail;

    public TrayApplicationContext()
    {
        _settings = SettingsStore.Load();
        _appIcon = AppIcon.Load();
        CleanupOldCapturesOnStartup();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Configuracoes...", null, (_, _) => ShowSettings());
        menu.Items.Add("Abrir pasta de destino", null, (_, _) => OpenCaptureFolder());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => ExitThread());

        _notifyIcon = new NotifyIcon
        {
            Icon = _appIcon,
            Text = "SnipDrag",
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowSettings();
            }
        };

        _clipboardWatcher = new ClipboardWatcher();
        _clipboardWatcher.ClipboardChanged += OnClipboardChanged;
    }

    private void OnClipboardChanged(object? sender, EventArgs e)
    {
        try
        {
            if (!Clipboard.ContainsImage())
            {
                return;
            }

            using var clipboardImage = Clipboard.GetImage();
            if (clipboardImage is null)
            {
                return;
            }

            using var bitmap = new Bitmap(clipboardImage);
            var hash = ComputeImageHash(bitmap);
            if (!_recentImageHashes.Add(hash))
            {
                return;
            }

            if (_recentImageHashes.Count > 25)
            {
                _recentImageHashes.Clear();
                _recentImageHashes.Add(hash);
            }

            Directory.CreateDirectory(_settings.CaptureDirectory);
            var filePath = Path.Combine(
                _settings.CaptureDirectory,
                $"snip-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");

            bitmap.Save(filePath, ImageFormat.Png);
            ShowThumbnail(filePath);
        }
        catch (ExternalException)
        {
            // Clipboard can be temporarily locked by another process.
        }
        catch (Exception ex)
        {
            _notifyIcon.ShowBalloonTip(4000, "SnipDrag", ex.Message, ToolTipIcon.Warning);
        }
    }

    private void ShowThumbnail(string filePath)
    {
        _activeThumbnail?.CloseAndDeleteIfPending();
        var thumbnail = new ThumbnailForm(filePath, _settings.TimeoutSeconds, _settings.PathTextMode);
        _activeThumbnail = thumbnail;
        thumbnail.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_activeThumbnail, thumbnail))
            {
                _activeThumbnail = null;
            }
        };
        thumbnail.Show();
    }

    private void ShowSettings()
    {
        using var form = new SettingsForm(_settings);
        if (form.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        _settings = form.Settings;
        SettingsStore.Save(_settings);
        StartupManager.SetEnabled(_settings.StartWithWindows);
    }

    private void OpenCaptureFolder()
    {
        Directory.CreateDirectory(_settings.CaptureDirectory);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = _settings.CaptureDirectory,
            UseShellExecute = true
        });
    }

    private void CleanupOldCapturesOnStartup()
    {
        if (!_settings.DeleteOldCapturesOnStartup || !Directory.Exists(_settings.CaptureDirectory))
        {
            return;
        }

        try
        {
            foreach (var filePath in Directory.EnumerateFiles(_settings.CaptureDirectory, "snip-*.png"))
            {
                TryDelete(filePath);
            }
        }
        catch (Exception ex)
        {
            _notifyIcon?.ShowBalloonTip(4000, "SnipDrag", ex.Message, ToolTipIcon.Warning);
        }
    }

    protected override void ExitThreadCore()
    {
        _activeThumbnail?.CloseAndDeleteIfPending();
        _clipboardWatcher.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _appIcon.Dispose();
        base.ExitThreadCore();
    }

    private static string ComputeImageHash(Image image)
    {
        using var stream = new MemoryStream();
        image.Save(stream, ImageFormat.Png);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static void TryDelete(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch
        {
            // Best effort cleanup.
        }
    }
}

internal sealed class ClipboardWatcher : NativeWindow, IDisposable
{
    private const int WmClipboardUpdate = 0x031D;

    public event EventHandler? ClipboardChanged;

    public ClipboardWatcher()
    {
        CreateHandle(new CreateParams());
        AddClipboardFormatListener(Handle);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmClipboardUpdate)
        {
            ClipboardChanged?.Invoke(this, EventArgs.Empty);
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        RemoveClipboardFormatListener(Handle);
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}

internal sealed class ThumbnailForm : Form
{
    private readonly string _filePath;
    private readonly PathTextMode _pathTextMode;
    private readonly System.Windows.Forms.Timer _timer;
    private bool _dragStarted;
    private string? _lastDragText;

    public ThumbnailForm(string filePath, int timeoutSeconds, PathTextMode pathTextMode)
    {
        _filePath = filePath;
        _pathTextMode = pathTextMode;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        Opacity = 0.96;
        Size = new Size(220, 150);
        Cursor = Cursors.Hand;

        using var image = Image.FromFile(filePath);
        BackgroundImage = CreateThumbnail(image, ClientSize);
        BackgroundImageLayout = ImageLayout.Center;

        var bounds = Screen.PrimaryScreen?.WorkingArea ?? SystemInformation.WorkingArea;
        Location = new Point(bounds.Right - Width - 18, bounds.Bottom - Height - 18);

        _timer = new System.Windows.Forms.Timer { Interval = Math.Max(1, timeoutSeconds) * 1000 };
        _timer.Tick += (_, _) => CloseAndDeleteIfPending();
        _timer.Start();

        MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                BeginDrag();
            }
        };
    }

    public void CloseAndDeleteIfPending()
    {
        if (!_dragStarted && File.Exists(_filePath))
        {
            TryDelete(_filePath);
        }

        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            BackgroundImage?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void BeginDrag()
    {
        _timer.Stop();
        _dragStarted = true;

        var data = new DataObject();
        data.SetFileDropList([_filePath]);
        UpdateDragText(data);

        GiveFeedback += OnGiveFeedback;
        DoDragDrop(data, DragDropEffects.Copy);
        GiveFeedback -= OnGiveFeedback;
        Close();

        void OnGiveFeedback(object? sender, GiveFeedbackEventArgs e)
        {
            UpdateDragText(data);
        }
    }

    private void UpdateDragText(DataObject data)
    {
        var text = PathFormatter.GetDragText(_filePath, _pathTextMode);
        if (text == _lastDragText)
        {
            return;
        }

        _lastDragText = text;
        data.SetText(text, TextDataFormat.UnicodeText);
        data.SetText(text, TextDataFormat.Text);
    }

    private static Image CreateThumbnail(Image source, Size canvasSize)
    {
        var canvas = new Bitmap(canvasSize.Width, canvasSize.Height);
        using var graphics = Graphics.FromImage(canvas);
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.Clear(Color.FromArgb(28, 28, 28));

        var scale = Math.Min((canvasSize.Width - 12f) / source.Width, (canvasSize.Height - 12f) / source.Height);
        var width = (int)(source.Width * scale);
        var height = (int)(source.Height * scale);
        var x = (canvasSize.Width - width) / 2;
        var y = (canvasSize.Height - height) / 2;

        graphics.FillRectangle(Brushes.White, x - 2, y - 2, width + 4, height + 4);
        graphics.DrawImage(source, x, y, width, height);
        return canvas;
    }

    private static void TryDelete(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch
        {
            // Best effort cleanup.
        }
    }
}

internal static class PathFormatter
{
    public static string GetDragText(string windowsPath, PathTextMode mode)
    {
        return mode switch
        {
            PathTextMode.Windows => windowsPath,
            PathTextMode.Wsl => ToWslPath(windowsPath),
            PathTextMode.Both => $"{windowsPath}{Environment.NewLine}{ToWslPath(windowsPath)}",
            _ => DragTargetInspector.IsLikelyWslUnderCursor() ? ToWslPath(windowsPath) : windowsPath
        };
    }

    private static string ToWslPath(string windowsPath)
    {
        var fullPath = Path.GetFullPath(windowsPath);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root) || root.Length < 2 || root[1] != ':')
        {
            return fullPath.Replace('\\', '/');
        }

        var drive = char.ToLowerInvariant(root[0]);
        var relative = fullPath[root.Length..].Replace('\\', '/');
        return $"/mnt/{drive}/{relative}";
    }
}

internal static class DragTargetInspector
{
    private static readonly string[] WslSignals =
    [
        "wsl",
        "ubuntu",
        "debian",
        "kali",
        "opensuse",
        "suse",
        "alpine"
    ];

    public static bool IsLikelyWslUnderCursor()
    {
        if (!GetCursorPos(out var point))
        {
            return false;
        }

        var hwnd = WindowFromPoint(point);
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var root = GetAncestor(hwnd, 2);
        if (root != IntPtr.Zero)
        {
            hwnd = root;
        }

        return HasWslSignal(GetProcessName(hwnd)) || HasWslSignal(GetWindowTitle(hwnd));
    }

    private static string GetProcessName(IntPtr hwnd)
    {
        _ = GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0)
        {
            return string.Empty;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length + 1);
        _ = GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    private static bool HasWslSignal(string value)
    {
        return WslSignals.Any(signal => value.Contains(signal, StringComparison.OrdinalIgnoreCase));
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);
}

internal sealed class SettingsForm : Form
{
    private readonly TextBox _directoryTextBox = new();
    private readonly NumericUpDown _timeoutInput = new();
    private readonly ComboBox _pathTextModeComboBox = new();
    private readonly CheckBox _deleteOldCapturesCheckBox = new();
    private readonly CheckBox _startupCheckBox = new();

    public AppSettings Settings { get; private set; }

    public SettingsForm(AppSettings settings)
    {
        Settings = settings with { };

        Text = "SnipDrag - Preferencias";
        Icon = AppIcon.Load();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(680, 330);
        ClientSize = new Size(680, 330);
        Padding = new Padding(18);
        Font = new Font("Segoe UI", 9F);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        var directoryRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Margin = Padding.Empty
        };
        directoryRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        directoryRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        directoryRow.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        directoryRow.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));

        directoryRow.Controls.Add(new Label
        {
            Text = "Pasta para salvar prints",
            AutoSize = true,
            Dock = DockStyle.Fill
        }, 0, 0);
        directoryRow.SetColumnSpan(directoryRow.Controls[0], 2);

        _directoryTextBox.Text = Settings.CaptureDirectory;
        _directoryTextBox.Dock = DockStyle.Fill;
        _directoryTextBox.Margin = new Padding(0, 0, 10, 0);

        var browseButton = new Button
        {
            Text = "Procurar...",
            Dock = DockStyle.Fill,
            Margin = Padding.Empty
        };
        browseButton.Click += (_, _) => BrowseDirectory();

        directoryRow.Controls.Add(_directoryTextBox, 0, 1);
        directoryRow.Controls.Add(browseButton, 1, 1);

        var timeoutRow = CreateTwoColumnRow("Tempo do thumbnail (segundos)");
        _timeoutInput.Minimum = 1;
        _timeoutInput.Maximum = 300;
        _timeoutInput.Value = Settings.TimeoutSeconds;
        _timeoutInput.Width = 92;
        timeoutRow.Controls.Add(_timeoutInput, 1, 0);

        var pathModeRow = CreateTwoColumnRow("Formato do path ao arrastar");
        _pathTextModeComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _pathTextModeComboBox.Width = 260;
        _pathTextModeComboBox.Items.AddRange([
            PathTextModeOption.Auto,
            PathTextModeOption.Windows,
            PathTextModeOption.Wsl,
            PathTextModeOption.Both
        ]);
        _pathTextModeComboBox.SelectedItem = PathTextModeOption.FromMode(Settings.PathTextMode);
        pathModeRow.Controls.Add(_pathTextModeComboBox, 1, 0);

        _startupCheckBox.Text = "Iniciar automaticamente com o Windows";
        _startupCheckBox.AutoSize = true;
        _startupCheckBox.Checked = StartupManager.IsEnabled();
        _startupCheckBox.Dock = DockStyle.Fill;

        _deleteOldCapturesCheckBox.Text = "Excluir prints antigos ao iniciar";
        _deleteOldCapturesCheckBox.AutoSize = true;
        _deleteOldCapturesCheckBox.Checked = Settings.DeleteOldCapturesOnStartup;
        _deleteOldCapturesCheckBox.Dock = DockStyle.Top;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = Padding.Empty
        };

        var cancelButton = new Button
        {
            Text = "Cancelar",
            DialogResult = DialogResult.Cancel,
            Size = new Size(94, 30)
        };

        var saveButton = new Button
        {
            Text = "Salvar",
            DialogResult = DialogResult.OK,
            Size = new Size(94, 30)
        };
        saveButton.Click += (_, _) => Save();

        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(saveButton);

        root.Controls.Add(directoryRow, 0, 0);
        root.Controls.Add(timeoutRow, 0, 1);
        root.Controls.Add(pathModeRow, 0, 2);
        root.Controls.Add(_startupCheckBox, 0, 3);
        root.Controls.Add(_deleteOldCapturesCheckBox, 0, 4);
        root.Controls.Add(buttons, 0, 5);

        Controls.Add(root);

        AcceptButton = saveButton;
        CancelButton = cancelButton;
    }

    private void BrowseDirectory()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Escolha a pasta onde os prints serao salvos",
            SelectedPath = Directory.Exists(_directoryTextBox.Text)
                ? _directoryTextBox.Text
                : AppSettings.DefaultCaptureDirectory
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _directoryTextBox.Text = dialog.SelectedPath;
        }
    }

    private void Save()
    {
        var directory = string.IsNullOrWhiteSpace(_directoryTextBox.Text)
            ? AppSettings.DefaultCaptureDirectory
            : Environment.ExpandEnvironmentVariables(_directoryTextBox.Text.Trim());

        Settings = new AppSettings
        {
            CaptureDirectory = directory,
            TimeoutSeconds = (int)_timeoutInput.Value,
            StartWithWindows = _startupCheckBox.Checked,
            DeleteOldCapturesOnStartup = _deleteOldCapturesCheckBox.Checked,
            PathTextMode = (_pathTextModeComboBox.SelectedItem as PathTextModeOption)?.Mode ?? PathTextMode.Auto
        };
    }

    private static TableLayoutPanel CreateTwoColumnRow(string label)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 280));
        row.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        return row;
    }
}

internal enum PathTextMode
{
    Auto,
    Windows,
    Wsl,
    Both
}

internal sealed record PathTextModeOption(string Label, PathTextMode Mode)
{
    public static readonly PathTextModeOption Auto = new("Automatico", PathTextMode.Auto);
    public static readonly PathTextModeOption Windows = new("Windows (C:\\...)", PathTextMode.Windows);
    public static readonly PathTextModeOption Wsl = new("WSL (/mnt/c/...)", PathTextMode.Wsl);
    public static readonly PathTextModeOption Both = new("Windows + WSL", PathTextMode.Both);

    public static PathTextModeOption FromMode(PathTextMode mode) => mode switch
    {
        PathTextMode.Windows => Windows,
        PathTextMode.Wsl => Wsl,
        PathTextMode.Both => Both,
        _ => Auto
    };

    public override string ToString() => Label;
}

internal sealed record AppSettings
{
    public static string DefaultCaptureDirectory =>
        Path.Combine(Path.GetTempPath(), "SnipDrag");

    public string CaptureDirectory { get; init; } = DefaultCaptureDirectory;

    public int TimeoutSeconds { get; init; } = 10;

    public bool StartWithWindows { get; init; }

    public bool DeleteOldCapturesOnStartup { get; init; }

    public PathTextMode PathTextMode { get; init; } = PathTextMode.Auto;
}

internal static class SettingsStore
{
    private static readonly string DirectoryPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnipDrag");

    private static readonly string LegacyDirectoryPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnipDragTray");

    private static readonly string FilePath = Path.Combine(DirectoryPath, "settings.json");

    private static readonly string LegacyFilePath = Path.Combine(LegacyDirectoryPath, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            var settingsPath = File.Exists(FilePath) ? FilePath : LegacyFilePath;
            if (!File.Exists(settingsPath))
            {
                return new AppSettings();
            }

            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsPath));
            return settings ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }
}

internal static class AppIcon
{
    public static Icon Load()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        return File.Exists(iconPath) ? new Icon(iconPath) : (Icon)SystemIcons.Application.Clone();
    }
}

internal static class StartupManager
{
    private const string ValueName = "SnipDrag";
    private const string LegacyValueName = "SnipDragTray";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
        return HasCurrentExecutable(key?.GetValue(ValueName) as string)
            || HasCurrentExecutable(key?.GetValue(LegacyValueName) as string);
    }

    private static bool HasCurrentExecutable(string? value)
    {
        return value?.Contains(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) == true;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
        if (key is null)
        {
            return;
        }

        if (enabled)
        {
            key.SetValue(ValueName, $"\"{Application.ExecutablePath}\"");
            key.DeleteValue(LegacyValueName, false);
        }
        else
        {
            key.DeleteValue(ValueName, false);
            key.DeleteValue(LegacyValueName, false);
        }
    }
}
