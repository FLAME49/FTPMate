using System.ComponentModel;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FTPMate.Helpers;
using FTPMate.Server;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace FTPMate;

public partial class MainWindow : Window
{
    private readonly FtpServer _server = new();
    private readonly DispatcherTimer _copyResetTimer;
    private bool _running;
    private bool _syncingToggle;
    private bool _syncingMode;
    private string _shareMode = "folder";
    private string? _selectedPartition;
    private List<DriveInfo> _drives = new();
    private string _address = "";
    private Forms.NotifyIcon? _tray;
    private Drawing.Icon? _trayIcon;

    public MainWindow()
    {
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height - 16;

        _server.Logged += OnServerLogged;
        _server.ClientsChanged += OnClientsChanged;

        _copyResetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
        _copyResetTimer.Tick += (_, _) =>
        {
            _copyResetTimer.Stop();
            CopyGlyph.Text = "\uE8C8";
        };

        SourceInitialized += (_, _) => ApplyDarkTitleBar();
        DataObject.AddPastingHandler(PortBox, OnPortPaste);

        var settings = SettingsStore.Load();
        var root = Directory.Exists(settings.Root) ? settings.Root : DefaultRoot();
        FolderPathText.Text = root;
        PortBox.Text = settings.Port is >= 1 and <= 65535 ? settings.Port.ToString() : "2121";
        UsernameBox.Text = string.IsNullOrWhiteSpace(settings.Username) ? "ftp" : settings.Username;
        PassBox.Password = SecretProtector.Unprotect(settings.PasswordProtected);

        BuildPartitionChips(settings.Partition);

        _syncingMode = true;
        if (settings.ShareMode == "all")
            ModeAllRadio.IsChecked = true;
        else if (settings.ShareMode == "partition")
            ModePartitionRadio.IsChecked = true;
        else
            ModeFolderRadio.IsChecked = true;
        _shareMode = ModeAllRadio.IsChecked == true ? "all"
            : ModePartitionRadio.IsChecked == true ? "partition"
            : "folder";
        _syncingMode = false;
        ApplyShareModeUI();

        AutoStart.RepairIfBroken();
        _syncingToggle = true;
        AutoStartSwitch.IsChecked = AutoStart.IsEnabled();
        _syncingToggle = false;

        CreateTray();
        UpdateFooter("Ready.");
    }

    public void StartHidden()
    {
        if (_tray != null)
            _tray.Visible = true;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_running)
                StartServer();
        }), DispatcherPriority.Background);
    }

    private void BuildPartitionChips(string savedPartition)
    {
        _drives = Partitions.Ready();
        PartitionRow.Children.Clear();

        if (_drives.Count == 0)
        {
            _selectedPartition = null;
            PartitionRow.Children.Add(new TextBlock
            {
                Text = "No drives available.",
                FontSize = 12,
                Foreground = (Brush)FindResource("FaintBrush")
            });
            AllDrivesText.Text = "No drives available.";
            return;
        }

        _selectedPartition = _drives
            .Select(d => d.RootDirectory.FullName)
            .FirstOrDefault(path => string.Equals(path, savedPartition, StringComparison.OrdinalIgnoreCase))
            ?? _drives[0].RootDirectory.FullName;

        foreach (var drive in _drives)
        {
            string path = drive.RootDirectory.FullName;
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock
            {
                Text = Partitions.Letter(drive) + ":",
                VerticalAlignment = VerticalAlignment.Center
            });

            string label = Partitions.Label(drive);
            if (label.Length > 0)
            {
                content.Children.Add(new TextBlock
                {
                    Text = label,
                    FontSize = 11.5,
                    Opacity = 0.55,
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            var chip = new RadioButton
            {
                Style = (Style)FindResource("Chip"),
                GroupName = "SharePartition",
                Content = content,
                Tag = path,
                ToolTip = path,
                IsChecked = string.Equals(path, _selectedPartition, StringComparison.OrdinalIgnoreCase)
            };
            chip.Checked += PartitionChip_Checked;
            AutomationProperties.SetAutomationId(chip, "Partition" + Partitions.Letter(drive));
            PartitionRow.Children.Add(chip);
        }

        AllDrivesText.Text = string.Join("   ·   ", _drives.Select(d => Partitions.Letter(d) + ":"));
    }

    private void PartitionChip_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string path })
            _selectedPartition = path;
    }

    private void ShareMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingMode)
            return;

        _shareMode = ModeAllRadio.IsChecked == true ? "all"
            : ModePartitionRadio.IsChecked == true ? "partition"
            : "folder";
        ApplyShareModeUI();
    }

    private void ApplyShareModeUI()
    {
        FolderRow.Visibility = _shareMode == "folder" ? Visibility.Visible : Visibility.Collapsed;
        PartitionRow.Visibility = _shareMode == "partition" ? Visibility.Visible : Visibility.Collapsed;
        AllRow.Visibility = _shareMode == "all" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AutoStartSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingToggle)
            return;

        bool enable = AutoStartSwitch.IsChecked == true;
        try
        {
            if (enable)
            {
                AutoStart.Enable();
                UpdateFooter("Server will start automatically at sign-in.");
            }
            else
            {
                AutoStart.Disable();
                UpdateFooter("Startup with Windows disabled.");
            }
        }
        catch (Exception ex)
        {
            _syncingToggle = true;
            AutoStartSwitch.IsChecked = !enable;
            _syncingToggle = false;
            UpdateFooter("Could not change the startup setting: " + ex.Message, true);
        }
    }

    private AppSettings BuildSettings()
    {
        int port = int.TryParse(PortBox.Text, out var parsed) && parsed is >= 1 and <= 65535 ? parsed : 2121;
        return new AppSettings
        {
            Root = FolderPathText.Text,
            ShareMode = _shareMode,
            Partition = _selectedPartition ?? "",
            Port = port,
            Username = UsernameBox.Text.Trim(),
            PasswordProtected = SecretProtector.Protect(PassBox.Password)
        };
    }

    private static string DefaultRoot()
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads))
            return downloads;

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Directory.Exists(documents) ? documents : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select a folder to share",
            Multiselect = false
        };

        if (Directory.Exists(FolderPathText.Text))
            dialog.InitialDirectory = FolderPathText.Text;

        if (dialog.ShowDialog(this) == true)
        {
            FolderPathText.Text = dialog.FolderName;
            UpdateFooter("Folder selected.");
        }
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_running)
            StopServer();
        else
            StartServer();
    }

    private void StartServer()
    {
        string source;
        if (_shareMode == "all")
        {
            if (Partitions.Ready().Count == 0)
            {
                UpdateFooter("No drives available to share.", true);
                return;
            }
            _server.ShareAllDrives = true;
            source = "";
        }
        else
        {
            source = _shareMode == "partition" ? _selectedPartition ?? "" : FolderPathText.Text;
            if (source.Length == 0)
            {
                UpdateFooter(_shareMode == "partition" ? "Select a drive to share." : "Select a folder to share.", true);
                return;
            }

            if (!Directory.Exists(source))
            {
                UpdateFooter("The selected path no longer exists.", true);
                return;
            }
            _server.ShareAllDrives = false;
        }

        if (!int.TryParse(PortBox.Text, out var port) || port is < 1 or > 65535)
        {
            UpdateFooter("Enter a port between 1 and 65535.", true);
            return;
        }

        string username = UsernameBox.Text.Trim();
        string password = PassBox.Password;

        if (password.Length > 0 && username.Length == 0)
        {
            UpdateFooter("Enter a username when a password is set.", true);
            return;
        }

        _server.Root = source;
        _server.Port = port;
        _server.Username = username.Length == 0 ? "ftp" : username;
        _server.Password = password;

        try
        {
            _server.Start();
        }
        catch (SocketException ex)
        {
            UpdateFooter(DescribeSocketError(ex, port), true);
            return;
        }
        catch (DirectoryNotFoundException)
        {
            UpdateFooter("The selected path no longer exists.", true);
            return;
        }
        catch (Exception ex)
        {
            UpdateFooter("Could not start the server: " + ex.Message, true);
            return;
        }

        _running = true;
        _address = $"ftp://{Net.LocalIPv4()}:{port}";
        AddressText.Text = _address;
        ClientsText.Text = "0 clients";
        ActivityText.Text = "No activity yet";

        ApplyState();
        SettingsStore.Save(BuildSettings());
        UpdateFooter("Server is running.");
    }

    private void StopServer()
    {
        _server.Stop();
        _running = false;
        ApplyState();
        UpdateFooter("Server stopped.");
    }

    private void ApplyState()
    {
        if (_running)
        {
            StatusPill.Background = (Brush)FindResource("AccentSoftBrush");
            StatusPill.BorderBrush = (Brush)FindResource("AccentLineBrush");
            StatusDot.Fill = (Brush)FindResource("AccentBrush");
            StatusText.Text = "Running";
            StatusText.Foreground = (Brush)FindResource("AccentBrush");
            StartButton.Style = (Style)FindResource("DangerButton");
            PowerGlyph.Visibility = Visibility.Collapsed;
            StopSquare.Visibility = Visibility.Visible;
            PowerLabel.Text = "Stop Server";
            InfoCard.Visibility = Visibility.Visible;

            ChooseFolderButton.IsEnabled = false;
            PortBox.IsEnabled = false;
            UsernameBox.IsEnabled = false;
            PassBox.IsEnabled = false;
            SetShareModeEnabled(false);
        }
        else
        {
            StatusPill.Background = (Brush)FindResource("SurfaceBrush");
            StatusPill.BorderBrush = (Brush)FindResource("LineBrush");
            StatusDot.Fill = (Brush)FindResource("FaintBrush");
            StatusText.Text = "Stopped";
            StatusText.Foreground = (Brush)FindResource("MutedBrush");
            StartButton.Style = (Style)FindResource("PrimaryButton");
            PowerGlyph.Visibility = Visibility.Visible;
            StopSquare.Visibility = Visibility.Collapsed;
            PowerLabel.Text = "Start Server";
            InfoCard.Visibility = Visibility.Collapsed;

            ChooseFolderButton.IsEnabled = true;
            PortBox.IsEnabled = true;
            UsernameBox.IsEnabled = true;
            PassBox.IsEnabled = true;
            SetShareModeEnabled(true);
        }

        KeepInsideWorkArea();
        UpdateTrayState();
    }

    private void SetShareModeEnabled(bool enabled)
    {
        ModeFolderRadio.IsEnabled = enabled;
        ModePartitionRadio.IsEnabled = enabled;
        ModeAllRadio.IsEnabled = enabled;

        foreach (var chip in PartitionRow.Children.OfType<RadioButton>())
            chip.IsEnabled = enabled;
    }

    private void KeepInsideWorkArea()
    {
        var area = SystemParameters.WorkArea;
        MaxHeight = Math.Max(240, area.Height - 16);

        if (!IsVisible)
            return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (WindowState == WindowState.Minimized)
                return;

            if (Top + ActualHeight > area.Bottom)
                Top = Math.Max(area.Top, area.Bottom - ActualHeight);
            if (Top < area.Top)
                Top = area.Top;
        }), DispatcherPriority.Loaded);
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_address.Length == 0)
            return;

        try
        {
            Clipboard.SetText(_address);
            CopyGlyph.Text = "\uE73E";
            _copyResetTimer.Stop();
            _copyResetTimer.Start();
            UpdateFooter("Address copied.");
        }
        catch
        {
            UpdateFooter("Could not copy the address.", true);
        }
    }

    private void OnServerLogged(string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_running)
                ActivityText.Text = message;
        });
    }

    private void OnClientsChanged()
    {
        Dispatcher.BeginInvoke(() =>
        {
            int count = _server.ClientCount;
            ClientsText.Text = count == 1 ? "1 client" : $"{count} clients";
        });
    }

    private void UpdateFooter(string message, bool error = false)
    {
        FooterText.Text = message;
        FooterText.SetResourceReference(TextBlock.ForegroundProperty, error ? "DangerBrush" : "FaintBrush");
    }

    private void PortBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = e.Text.Any(c => !char.IsAsciiDigit(c));
    }

    private void PortBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
            e.Handled = true;
    }

    private void OnPortPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(DataFormats.Text) is not string text || text.Any(c => !char.IsAsciiDigit(c)))
            e.CancelCommand();
    }

    private static string DescribeSocketError(SocketException ex, int port) => ex.SocketErrorCode switch
    {
        SocketError.AddressAlreadyInUse => $"Port {port} is already in use. Try another port.",
        SocketError.AccessDenied => "Access denied for this port. Try a port above 1024.",
        SocketError.AddressNotAvailable => "Network address unavailable. Check your connection.",
        _ => "Could not start the server: " + ex.Message
    };

    private void ApplyDarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int enabled = 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref enabled, sizeof(int));

            int borderNone = unchecked((int)0xFFFFFFFE);
            DwmSetWindowAttribute(hwnd, 34, ref borderNone, sizeof(int));
        }
        catch { }
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);

        if (WindowState == WindowState.Minimized && IsVisible)
            MinimizeToTray();
    }

    private void MinimizeToTray()
    {
        if (_tray == null)
            return;

        _tray.Visible = true;
        Hide();
    }

    private void RestoreFromTray()
    {
        if (_tray != null)
            _tray.Visible = false;

        Show();
        WindowState = WindowState.Normal;
        Activate();
        SetForegroundWindow(new WindowInteropHelper(this).Handle);
        KeepInsideWorkArea();
    }

    private void CreateTray()
    {
        if (_tray != null)
            return;

        _trayIcon = LoadAppIcon();

        var open = new Forms.ToolStripMenuItem("Open") { Padding = new Forms.Padding(10, 4, 10, 4) };
        open.Click += (_, _) => RestoreFromTray();

        var exit = new Forms.ToolStripMenuItem("Exit") { Padding = new Forms.Padding(10, 4, 10, 4) };
        exit.Click += (_, _) => ExitApplication();

        var menu = new Forms.ContextMenuStrip
        {
            ShowImageMargin = false,
            BackColor = Drawing.Color.FromArgb(23, 25, 30),
            ForeColor = Drawing.Color.FromArgb(241, 242, 244),
            Renderer = new TrayMenuRenderer(),
            Font = new Drawing.Font("Segoe UI", 9f)
        };
        menu.Items.Add(open);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exit);

        _tray = new Forms.NotifyIcon
        {
            Icon = _trayIcon,
            Text = "FTPMate",
            ContextMenuStrip = menu,
            Visible = false
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                RestoreFromTray();
        };
    }

    private static Drawing.Icon LoadAppIcon()
    {
        try
        {
            string? path = Environment.ProcessPath;
            if (path != null && Drawing.Icon.ExtractAssociatedIcon(path) is { } icon)
                return icon;
        }
        catch { }

        return (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
    }

    private void UpdateTrayState()
    {
        if (_tray != null)
            _tray.Text = _running ? "FTPMate - Running" : "FTPMate - Stopped";
    }

    private void ExitApplication()
    {
        if (_running)
        {
            _server.Stop();
            _running = false;
        }

        SettingsStore.Save(BuildSettings());
        DisposeTray();
        System.Windows.Application.Current.Shutdown();
    }

    private void DisposeTray()
    {
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }

        _trayIcon?.Dispose();
        _trayIcon = null;
    }

    protected override void OnClosed(EventArgs e)
    {
        DisposeTray();
        base.OnClosed(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_running)
        {
            _server.Stop();
            _running = false;
        }

        SettingsStore.Save(BuildSettings());

        base.OnClosing(e);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    private sealed class TrayMenuRenderer : Forms.ToolStripProfessionalRenderer
    {
        public TrayMenuRenderer() : base(new TrayColorTable())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Selected
                ? Drawing.Color.FromArgb(147, 205, 185)
                : Drawing.Color.FromArgb(241, 242, 244);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
        {
            using var pen = new Drawing.Pen(Drawing.Color.FromArgb(40, 44, 51));
            e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }
    }

    private sealed class TrayColorTable : Forms.ProfessionalColorTable
    {
        private static readonly Drawing.Color Surface = Drawing.Color.FromArgb(23, 25, 30);
        private static readonly Drawing.Color Hover = Drawing.Color.FromArgb(29, 32, 38);
        private static readonly Drawing.Color Line = Drawing.Color.FromArgb(40, 44, 51);

        public override Drawing.Color ToolStripDropDownBackground => Surface;
        public override Drawing.Color ImageMarginGradientBegin => Surface;
        public override Drawing.Color ImageMarginGradientMiddle => Surface;
        public override Drawing.Color ImageMarginGradientEnd => Surface;
        public override Drawing.Color MenuBorder => Line;
        public override Drawing.Color MenuItemBorder => Hover;
        public override Drawing.Color MenuItemSelected => Hover;
        public override Drawing.Color MenuItemSelectedGradientBegin => Hover;
        public override Drawing.Color MenuItemSelectedGradientEnd => Hover;
        public override Drawing.Color SeparatorDark => Line;
        public override Drawing.Color SeparatorLight => Surface;
    }
}
