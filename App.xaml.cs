using System.Windows;
using System.Windows.Media.Imaging;

namespace deepseek_copilot;

using Forms = System.Windows.Forms;

public partial class App : System.Windows.Application
{
    private static readonly System.Threading.Mutex _mutex = new(true, "DeepSeekCopilot-Singleton");
    private WindowManager? _windowManager;
    private KeyboardHookService? _keyboardService;
    private ExternalInvokeService? _externalInvokeService;
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ToolStripMenuItem? _mapMenuItem;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var question = ParseQuestionArg(e.Args);

        if (!_mutex.WaitOne(0, false))
        {
            if (question != null)
            {
                var forwarded = ExternalInvokeService.SendQuestionAsync(question).GetAwaiter().GetResult();
                if (!forwarded)
                    System.Windows.MessageBox.Show("DeepSeek Copilot 未在运行，无法转发问题", "DeepSeek Copilot",
                        MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                System.Windows.MessageBox.Show("DeepSeek Copilot 已在运行中", "DeepSeek Copilot",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            Shutdown();
            return;
        }

        if (e.Args.Length > 0 && e.Args[0] == "--detect")
        {
            new MainWindow().Show();
            return;
        }

        _windowManager = new WindowManager();
        _keyboardService = new KeyboardHookService(_windowManager.Settings);
        _keyboardService.CopilotKeyPressed += OnCopilotKeyPressed;
        _keyboardService.CopilotLongPressed += OnCopilotLongPressed;

        _externalInvokeService = new ExternalInvokeService(_windowManager);
        _externalInvokeService.Start();

        SetupTrayIcon();

        _keyboardService.MappingToggled += () =>
        {
            if (_mapMenuItem != null)
                _mapMenuItem.Checked = _windowManager!.Settings.MapCopilotToRightCtrl;
        };

        if (question != null && _windowManager != null)
            Dispatcher.BeginInvoke(async () => await _windowManager.AskAsync(question));
    }

    private static string? ParseQuestionArg(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--ask" && i + 1 < args.Length)
                return args[i + 1];
        }
        if (args.Length > 0 && args[0] != "--detect")
            return args[0];
        return null;
    }

    private void OnCopilotKeyPressed()
    {
        _windowManager?.Toggle();
    }

    private bool _captureActive;

    private async void OnCopilotLongPressed()
    {
        if (_captureActive) return;
        if (_windowManager?.Settings.EnableLongPressCapture == false) return;
        _captureActive = true;
        try
        {
            await CaptureAndPasteAsync();
        }
        finally
        {
            _captureActive = false;
        }
    }

    private async Task CaptureAndPasteAsync()
    {
        _windowManager?.HideSidebar();
        await Task.Delay(300);

        var capture = new ScreenCaptureWindow();
        var tcs = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        capture.Confirmed += img => tcs.TrySetResult(img);
        capture.Cancelled += () => tcs.TrySetResult(null);
        capture.Show();
        ScreenCaptureWindow.ForceForeground(new System.Windows.Interop.WindowInteropHelper(capture).Handle);

        var image = await tcs.Task;
        if (image == null || _windowManager == null) return;

        System.Windows.Clipboard.SetImage(image);
        await _windowManager.ShowSidebarAndPasteAsync();
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Text = "DeepSeek Copilot\n点击切换侧边栏",
            Visible = true
        };
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                _windowManager?.Toggle();
        };

        var menu = new Forms.ContextMenuStrip();

        _mapMenuItem = new Forms.ToolStripMenuItem("将Copilot映射为右Ctrl")
        {
            Checked = _windowManager!.Settings.MapCopilotToRightCtrl
        };
        _mapMenuItem.Click += (_, _) =>
        {
            var s = _windowManager.Settings;
            s.MapCopilotToRightCtrl = !s.MapCopilotToRightCtrl;
            s.Save();
            _mapMenuItem.Checked = s.MapCopilotToRightCtrl;
        };

        menu.Items.Add("显示/隐藏", null, (_, _) => _windowManager?.Toggle());
        menu.Items.Add("设置", null, (_, _) => _windowManager?.OpenSettings());
        menu.Items.Add(new Forms.ToolStripSeparator());

        var themeMenu = new Forms.ToolStripMenuItem("主题");
        var themes = new[] { ("跟随系统", deepseek_copilot.ThemeMode.System), ("浅色", deepseek_copilot.ThemeMode.Light), ("深色", deepseek_copilot.ThemeMode.Dark) };
        foreach (var (label, mode) in themes)
        {
            var item = new Forms.ToolStripMenuItem(label)
            {
                Checked = _windowManager!.Settings.GetThemeMode() == mode
            };
            item.Click += (_, _) =>
            {
                _windowManager!.SetTheme(mode);
                foreach (Forms.ToolStripMenuItem t in themeMenu.DropDownItems)
                    t.Checked = t == item;
            };
            themeMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(themeMenu);

        menu.Items.Add(_mapMenuItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApp());
        _trayIcon.ContextMenuStrip = menu;
    }

    private static System.Drawing.Icon CreateTrayIcon()
    {
        return IconHelper.RenderToIcon(32);
    }

    private void ExitApp()
    {
        _windowManager?.Cleanup();
        _keyboardService?.Dispose();
        _externalInvokeService?.Dispose();
        _trayIcon?.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Close();
        Shutdown();
    }
}
