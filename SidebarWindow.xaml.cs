using KBEHtool;
using Microsoft.Web.WebView2.Wpf;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace deepseek_copilot;

public partial class SidebarWindow : Window
{
    private const double BaseAnimationDurationMs = 250;
    private readonly AppSettings _settings;
    private double _sidebarWidth = 450;
    private double _screenRightDip;
    private double _screenTopDip;
    private double _screenHeightDip;
    private DispatcherTimer? _unloadTimer;
    private readonly DispatcherTimer _hideTimer;
    private WebView2? _webView;
    private int _webViewGeneration;
    private bool _isOpeningSettings;
    private TaskCompletionSource<bool>? _slideInTcs;

    public bool IsAnimating { get; private set; }
    public event Action? SettingsClicked;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const uint GA_ROOTOWNER = 3;
    private const int VK_LBUTTON = 0x01;
    private const int MouseKeyDown = 0x8000;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public SidebarWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        Icon = IconHelper.RenderToBitmapSource(16);
        TitleIcon.Source = IconHelper.RenderToBitmapSource(18);

        PositionOnMouseScreen();

        Deactivated += OnDeactivated;
        DpiChanged += OnDpiChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += OnLoaded;
        Closed += OnClosed;

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _hideTimer.Tick += OnHideTick;
    }

    public void ApplyTheme(ThemeMode theme)
    {
        _settings.SetThemeMode(theme);
        ApplyOpacity(_settings.SidebarOpacity);

        var isDark = _settings.IsDarkTheme;
        var textColor = isDark ? Colors.White : System.Windows.Media.Color.FromRgb(0x1A, 0x1A, 0x2E);
        TitleText.Foreground = new SolidColorBrush(textColor);

        var btnColor = isDark ? Colors.White : System.Windows.Media.Color.FromRgb(0x66, 0x66, 0x66);
        SettingsButton.Foreground = new SolidColorBrush(btnColor);
        CloseButton.Foreground = new SolidColorBrush(btnColor);

        SetWebViewColorScheme(isDark);
    }

    public void ApplyOpacity(double opacity)
    {
        var isDark = _settings.IsDarkTheme;
        var baseColor = isDark
            ? System.Windows.Media.Color.FromRgb(0x15, 0x15, 0x17)
            : System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF);
        var alpha = (byte)(opacity * 255);
        var bgColor = System.Windows.Media.Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B);

        MainBorder.Background = new SolidColorBrush(bgColor);
        TitleBar.Background = new SolidColorBrush(bgColor);
    }

    private void SetWebViewColorScheme(bool isDark)
    {
        if (_webView?.CoreWebView2 == null) return;
        _webView.CoreWebView2.Profile.PreferredColorScheme =
            isDark
                ? Microsoft.Web.WebView2.Core.CoreWebView2PreferredColorScheme.Dark
                : Microsoft.Web.WebView2.Core.CoreWebView2PreferredColorScheme.Light;
    }

    public void ApplyWidth(double width)
    {
        _sidebarWidth = width;
        Width = width;
        BeginAnimation(LeftProperty, null);
        PositionOnScreen();
    }

    private void PositionOnMouseScreen()
    {
        var screen = MonitorHelper.MouseScreen();

        // WPF converts Window.Left/Top/Width/Height with the DPI of the monitor the window
        // currently lives on, not the one it is being moved to. Convert with the current
        // scale so the move lands on the right monitor; the resulting DpiChanged event
        // re-runs this method once the window actually has that monitor's DPI.
        var scale = CurrentDpiScale();

        _screenRightDip = screen.WorkingArea.Right / scale;
        _screenTopDip = screen.WorkingArea.Top / scale;
        _screenHeightDip = screen.WorkingArea.Height / scale;
        PositionOnScreen();
    }

    private double CurrentDpiScale()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            var dpi = GetDpiForWindow(hwnd);
            if (dpi > 0) return dpi / 96d;
        }
        return 1d;
    }

    private void OnDpiChanged(object? sender, System.Windows.DpiChangedEventArgs e)
    {
        PositionOnMouseScreen();
    }

    private void PositionOnScreen()
    {
        BeginAnimation(LeftProperty, null);
        Left = _screenRightDip - _sidebarWidth;
        Top = _screenTopDip;
        Height = _screenHeightDip;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await CreateWebViewAsync();
    }

    private async Task CreateWebViewAsync()
    {
        if (_webView != null) return;

        var generation = _webViewGeneration;

        Microsoft.Web.WebView2.Core.CoreWebView2Environment env;
        try
        {
            env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                null,
                System.IO.Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                    "DeepSeekCopilot", "WebView2"));
        }
        catch (Exception)
        {
            return;
        }

        // The sidebar may have been unloaded (or closed) while the environment was starting.
        if (_webView != null || generation != _webViewGeneration) return;

        var webView = new WebView2();
        _webView = webView;
        WebViewHost.Children.Add(webView);

        try
        {
            // While this completes the control has no CoreWebView2 yet, so a concurrent
            // DestroyWebView must not touch CoreWebView2 or this continuation.
            await webView.EnsureCoreWebView2Async(env);
        }
        catch (Exception)
        {
            // Initialization failed or was aborted by disposal: clean up whatever is left.
            if (ReferenceEquals(_webView, webView)) DestroyWebView();
            return;
        }

        if (!ReferenceEquals(_webView, webView))
            return; // destroyed while initializing; it was already disposed

        var core = webView.CoreWebView2;
        if (core == null)
        {
            DestroyWebView();
            return;
        }

        core.DocumentTitleChanged += OnTitleChanged;
        SetWebViewColorScheme(_settings.IsDarkTheme);
        core.Navigate(_settings.ChatUrl);
        TitleText.Text = core.DocumentTitle;
    }

    private void OnTitleChanged(object? sender, object e)
    {
        Dispatcher.Invoke(() =>
        {
            TitleText.Text = _webView?.CoreWebView2?.DocumentTitle ?? "DeepSeek";
        });
    }

    private void DestroyWebView()
    {
        _unloadTimer?.Stop();
        _unloadTimer = null;

        var webView = _webView;
        _webView = null;

        // Invalidate any CreateWebViewAsync still waiting on the environment/browser.
        _webViewGeneration++;

        if (webView == null) return;

        // CoreWebView2 is null while initialization is still in flight; never dereference it.
        try { webView.CoreWebView2?.DocumentTitleChanged -= OnTitleChanged; } catch { }
        try { WebViewHost.Children.Remove(webView); } catch { }
        try { webView.Dispose(); } catch { }

        // Releasing WebView2 tears down the browser process through the finalizer thread;
        // do that GC pressure off the UI thread so unloading never blocks the interface.
        Task.Run(() =>
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        });
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _hideTimer.Stop();
        _unloadTimer?.Stop();
        DestroyWebView();
    }

    private void StartUnloadTimer()
    {
        if (_unloadTimer == null)
        {
            _unloadTimer = new DispatcherTimer();
            _unloadTimer.Tick += OnUnloadTick;
        }
        _unloadTimer.Interval = TimeSpan.FromSeconds(_settings.UnloadDelaySeconds);
        _unloadTimer.Start();
    }

    private void OnUnloadTick(object? sender, EventArgs e)
    {
        _unloadTimer?.Stop();
        if (Visibility != Visibility.Visible)
            DestroyWebView();
    }

    public async void SlideIn()
    {
        if (IsAnimating) return;
        _hideTimer.Stop();
        _unloadTimer?.Stop();

        _slideInTcs?.TrySetCanceled();
        _slideInTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        IsAnimating = true;
        PositionOnMouseScreen();

        if (_webView == null)
        {
            Show();
            Activate();
            await CreateWebViewAsync();
            AnimateIn();
        }
        else
        {
            Show();
            Activate();
            AnimateIn();
        }
    }

    public Task WaitForSlideInAsync()
    {
        if (_slideInTcs != null) return _slideInTcs.Task;

        _slideInTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!IsAnimating && Visibility == Visibility.Visible)
            _slideInTcs.TrySetResult(true);
        return _slideInTcs.Task;
    }

    public async Task PasteClipboardImageAsync()
    {
        if (_webView?.CoreWebView2 == null) return;

        const string script =
            "(()=>{const i=document.querySelector('textarea,input');if(i){i.focus();}" +
            "const v=document.querySelector('[data-model-type=\"vision\"][role=\"radio\"]');" +
            "if(v&&v.getAttribute('aria-checked')!=='true'){v.click();}" +
            "return i?true:false;})()";

        for (var i = 0; i < 40; i++)
        {
            var webView = _webView;
            if (webView?.CoreWebView2 == null) return;
            try
            {
                var result = await webView.CoreWebView2.ExecuteScriptAsync(script);
                if (result == "true")
                {
                    await Task.Delay(250);
                    break;
                }
            }
            catch { }
            await Task.Delay(150);
        }

        var visionHost = _webView;
        if (visionHost?.CoreWebView2 == null) return;

        try
        {
            await visionHost.CoreWebView2.ExecuteScriptAsync(
                "(()=>{const v=document.querySelector('[data-model-type=\"vision\"][role=\"radio\"]');" +
                "if(v&&v.getAttribute('aria-checked')!=='true'){v.click();}return true;})()");
        }
        catch { }

        await Task.Delay(200);
        if (_webView?.CoreWebView2 == null) return;

        try
        {
            KeyAction.PressKey(new[] { KeyCode.LeftControl, KeyCode.V }, 60);
        }
        catch { }
    }

    public async Task<bool> AskAsync(string question)
    {
        if (_webView?.CoreWebView2 == null) return false;

        var encoded = JsonSerializer.Serialize(question);

        const string focusScript =
            "(()=>{const e=document.querySelector('textarea,input');if(e){e.focus();}return e?true:false;})()";

        var setScript =
            "(()=>{" +
            "const el=document.querySelector('textarea,input');" +
            "if(!el)return false;" +
            "const proto=el instanceof HTMLTextAreaElement?HTMLTextAreaElement.prototype:HTMLInputElement.prototype;" +
            "Object.getOwnPropertyDescriptor(proto,'value').set.call(el," + encoded + ");" +
            "el.dispatchEvent(new Event('input',{bubbles:true}));" +
            "return true;})()";

        var found = false;
        for (var i = 0; i < 40; i++)
        {
            var webView = _webView;
            if (webView?.CoreWebView2 == null) return false;
            try
            {
                var ok = await webView.CoreWebView2.ExecuteScriptAsync(setScript);
                if (ok == "true") { found = true; break; }
            }
            catch { }
            await Task.Delay(150);
        }

        if (!found) return false;

        var host = _webView;
        if (host?.CoreWebView2 == null) return false;

        try { await host.CoreWebView2.ExecuteScriptAsync(focusScript); } catch { }
        await Task.Delay(200);

        if (_webView?.CoreWebView2 == null) return false;

        const string enterScript =
            "(()=>{" +
            "const el=document.querySelector('textarea,input');" +
            "if(!el)return false;" +
            "const down=new KeyboardEvent('keydown',{key:'Enter',code:'Enter',keyCode:13,which:13,bubbles:true,cancelable:true});" +
            "const up=new KeyboardEvent('keyup',{key:'Enter',code:'Enter',keyCode:13,which:13,bubbles:true,cancelable:true});" +
            "el.dispatchEvent(down);el.dispatchEvent(up);return true;})()";

        try { await host.CoreWebView2.ExecuteScriptAsync(enterScript); } catch { }
        await Task.Delay(150);

        if (_webView?.CoreWebView2 == null) return false;

        const string clickScript =
            "(()=>{" +
            "const btns=[...document.querySelectorAll('button')];" +
            "const b=btns.find(x=>{const t=(x.getAttribute('aria-label')||x.textContent||'');return /send|submit|发送/i.test(t);});" +
            "if(b&&!b.disabled){b.click();return true;}return false;})()";

        try { await host.CoreWebView2.ExecuteScriptAsync(clickScript); } catch { }

        return true;
    }

    private void AnimateIn()
    {
        var anim = new DoubleAnimation
        {
            From = _screenRightDip,
            To = _screenRightDip - _sidebarWidth,
            Duration = TimeSpan.FromMilliseconds(BaseAnimationDurationMs / _settings.AnimationSpeed),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        anim.Completed += async (_, _) =>
        {
            IsAnimating = false;
            _slideInTcs?.TrySetResult(true);
            if (_webView != null)
            {
                Keyboard.Focus(_webView);
                try
                {
                    await _webView.CoreWebView2!.ExecuteScriptAsync(
                        "try{let e=document.querySelector('textarea,input');e?.focus()}catch{}");
                }
                catch { }
            }
        };
        BeginAnimation(LeftProperty, anim);
    }

    public void SlideOut()
    {
        if (IsAnimating) return;

        IsAnimating = true;
        var anim = new DoubleAnimation
        {
            From = Left,
            To = _screenRightDip,
            Duration = TimeSpan.FromMilliseconds(BaseAnimationDurationMs / _settings.AnimationSpeed),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        anim.Completed += (_, _) =>
        {
            IsAnimating = false;
            Hide();
            StartUnloadTimer();
        };
        BeginAnimation(LeftProperty, anim);
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        _isOpeningSettings = true;
        SettingsClicked?.Invoke();
        _isOpeningSettings = false;
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_isOpeningSettings) return;
        if (Visibility != Visibility.Visible) return;

        var fg = GetForegroundWindow();
        if (fg != IntPtr.Zero)
        {
            var ourHwnd = new WindowInteropHelper(this).Handle;
            var root = GetAncestor(fg, GA_ROOTOWNER);
            if (root == ourHwnd) return;
        }

        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void OnHideTick(object? sender, EventArgs e)
    {
        _hideTimer.Stop();
        if (IsActive) return;
        if (Visibility != Visibility.Visible) return;

        if (IsCursorOverWindow())
            return;

        if ((GetAsyncKeyState(VK_LBUTTON) & MouseKeyDown) != 0)
        {
            _hideTimer.Start();
            return;
        }

        SlideOut();
    }

    private bool IsCursorOverWindow()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!GetCursorPos(out var pt)) return false;
        if (!GetWindowRect(hwnd, out var rect)) return false;
        return pt.X >= rect.Left && pt.X <= rect.Right &&
               pt.Y >= rect.Top && pt.Y <= rect.Bottom;
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            SlideOut();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        SlideOut();
    }
}
