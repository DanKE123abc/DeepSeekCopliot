using System.Windows;
using iNKORE.UI.WPF.Modern;

namespace deepseek_copilot;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action<double> _onWidthChanged;
    private readonly Action<ThemeMode> _onThemeChanged;
    private readonly Action<double> _onOpacityChanged;
    private readonly bool _originalAutoStart;
    private readonly string _originalChatUrl;
    private readonly ThemeMode _originalTheme;
    private readonly double _originalOpacity;
    private readonly bool _originalCopilotMap;
    private readonly bool _originalToggleShortcut;
    private readonly bool _originalLongPressCapture;

    public SettingsWindow(AppSettings settings, Action<double> onWidthChanged,
        Action<ThemeMode> onThemeChanged, Action<double> onOpacityChanged)
    {
        _settings = settings;
        _originalAutoStart = settings.AutoStart;
        _originalChatUrl = settings.ChatUrl;
        _originalTheme = settings.GetThemeMode();
        _originalOpacity = settings.SidebarOpacity;
        _originalCopilotMap = settings.MapCopilotToRightCtrl;
        _originalToggleShortcut = settings.EnableToggleShortcut;
        _originalLongPressCapture = settings.EnableLongPressCapture;
        _onWidthChanged = onWidthChanged;
        _onThemeChanged = onThemeChanged;
        _onOpacityChanged = onOpacityChanged;
        InitializeComponent();
        DataContext = settings;
        ThemeManager.SetRequestedTheme(this, settings.GetThemeMode() switch
        {
            deepseek_copilot.ThemeMode.Dark => ElementTheme.Dark,
            deepseek_copilot.ThemeMode.Light => ElementTheme.Light,
            _ => ElementTheme.Default
        });
        WidthSlider.Value = settings.SidebarWidth;
        DelaySlider.Value = settings.UnloadDelaySeconds;
        SpeedSlider.Value = settings.AnimationSpeed;
        OpacitySlider.Value = settings.SidebarOpacity;
        ThemeCombo.SelectedIndex = (int)settings.GetThemeMode();
    }

    private void OnWidthChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _onWidthChanged(e.NewValue);
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        _settings.SidebarWidth = WidthSlider.Value;
        _settings.AutoStart = AutoStartCheck.IsChecked ?? false;
        _settings.UnloadDelaySeconds = (int)DelaySlider.Value;
        _settings.SetThemeMode((ThemeMode)ThemeCombo.SelectedIndex);
        _settings.AnimationSpeed = SpeedSlider.Value;
        _settings.SidebarOpacity = OpacitySlider.Value;
        _settings.EnableToggleShortcut = ToggleShortcutCheck.IsChecked ?? false;
        _settings.EnableLongPressCapture = LongPressCaptureCheck.IsChecked ?? false;
        _settings.Save();
        _onThemeChanged(_settings.GetThemeMode());
        _onOpacityChanged(_settings.SidebarOpacity);
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _settings.AutoStart = _originalAutoStart;
        _settings.ChatUrl = _originalChatUrl;
        _settings.SetThemeMode(_originalTheme);
        _settings.SidebarOpacity = _originalOpacity;
        _settings.MapCopilotToRightCtrl = _originalCopilotMap;
        _settings.EnableToggleShortcut = _originalToggleShortcut;
        _settings.EnableLongPressCapture = _originalLongPressCapture;
        _onWidthChanged(_settings.SidebarWidth);
        _onThemeChanged(_originalTheme);
        _onOpacityChanged(_originalOpacity);
        Close();
    }
}
