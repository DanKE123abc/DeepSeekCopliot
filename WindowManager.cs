namespace deepseek_copilot;

public sealed class WindowManager
{
    public AppSettings Settings { get; }
    private SidebarWindow? _sidebar;

    public WindowManager()
    {
        Settings = AppSettings.Load();
    }

    public void Toggle()
    {
        if (_sidebar is { IsAnimating: true }) return;

        if (_sidebar == null || !_sidebar.IsVisible)
        {
            ShowSidebar();
        }
        else
        {
            HideSidebar();
        }
    }

    private void ShowSidebar()
    {
        if (_sidebar == null)
        {
            _sidebar = new SidebarWindow(Settings);
            _sidebar.ApplyWidth(Settings.SidebarWidth);
            _sidebar.ApplyTheme(Settings.GetThemeMode());
            _sidebar.ApplyOpacity(Settings.SidebarOpacity);
            _sidebar.SettingsClicked += OnSettingsClicked;
        }

        _sidebar.SlideIn();
    }

    public void HideSidebar()
    {
        _sidebar?.SlideOut();
    }

    public async Task ShowSidebarAndPasteAsync()
    {
        ShowSidebar();
        if (_sidebar == null) return;
        await _sidebar.WaitForSlideInAsync();
        await _sidebar.PasteClipboardImageAsync();
    }

    public async Task<bool> AskAsync(string question)
    {
        if (string.IsNullOrWhiteSpace(question)) return false;
        ShowSidebar();
        if (_sidebar == null) return false;
        await _sidebar.WaitForSlideInAsync();
        return await _sidebar.AskAsync(question);
    }

    private void OnSettingsClicked()
    {
        var win = new SettingsWindow(Settings,
            onWidthChanged: w => _sidebar?.ApplyWidth(w),
            onThemeChanged: t => _sidebar?.ApplyTheme(t),
            onOpacityChanged: o => _sidebar!.ApplyOpacity(o));
        win.ShowDialog();
    }

    public void OpenSettings()
    {
        OnSettingsClicked();
    }

    public void SetTheme(ThemeMode mode)
    {
        Settings.SetThemeMode(mode);
        Settings.Save();
        _sidebar?.ApplyTheme(mode);
    }

    public void Cleanup()
    {
        _sidebar?.Close();
        _sidebar = null;
    }
}
