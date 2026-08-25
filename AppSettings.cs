using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace deepseek_copilot;

public enum ThemeMode
{
    System,
    Light,
    Dark
}

public sealed class AppSettings
{
    private const string AutoStartKey = "DeepSeekCopilot";

    public double SidebarWidth { get; set; } = 450;
    public bool AutoStart { get; set; }
    public int UnloadDelaySeconds { get; set; } = 5;
    public string ChatUrl { get; set; } = "https://chat.deepseek.com/";
    public string Theme { get; set; } = "System";
    public double AnimationSpeed { get; set; } = 1.0;
    public double SidebarOpacity { get; set; } = 1.0;
    public bool MapCopilotToRightCtrl { get; set; }
    public bool EnableToggleShortcut { get; set; }
    public bool EnableLongPressCapture { get; set; } = true;

    public ThemeMode GetThemeMode() => Theme switch
    {
        "Dark" => ThemeMode.Dark,
        "Light" => ThemeMode.Light,
        _ => ThemeMode.System
    };

    public void SetThemeMode(ThemeMode mode) => Theme = mode.ToString();

    public static bool IsSystemDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int i)
                return i == 0;
        }
        catch { }
        return false;
    }

    public bool IsDarkTheme => GetThemeMode() switch
    {
        ThemeMode.Dark => true,
        ThemeMode.Light => false,
        _ => IsSystemDarkMode()
    };

    private static string FilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeepSeekCopilot", "settings.json");

    public void Save()
    {
        SyncAutoStart();
        var dir = Path.GetDirectoryName(FilePath);
        if (dir != null) Directory.CreateDirectory(dir);
        File.WriteAllText(FilePath, DanKeJson.JSON.ToJson(this));
    }

    private void SyncAutoStart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run", true);
        if (AutoStart)
            key?.SetValue(AutoStartKey, Environment.ProcessPath ?? "");
        else
            key?.DeleteValue(AutoStartKey, false);
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var raw = File.ReadAllText(FilePath);
                var merged = MergeJson(raw);
                var s = DanKeJson.JSON.ToData<AppSettings>(merged);
                if (s != null)
                {
                    s.ChatUrl = string.IsNullOrEmpty(s.ChatUrl) ? "https://chat.deepseek.com/" : s.ChatUrl;
                    if (s.SidebarWidth <= 0) s.SidebarWidth = 450;
                    if (s.UnloadDelaySeconds <= 0) s.UnloadDelaySeconds = 5;
                    OverwriteIfChanged(merged, raw);
                    return s;
                }
            }
        }
        catch { }
        return new AppSettings();
    }

    private static void OverwriteIfChanged(string merged, string raw)
    {
        if (merged == raw) return;
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, merged);
        }
        catch { }
    }

    private static string MergeJson(string raw)
    {
        var def = JsonNode.Parse(DanKeJson.JSON.ToJson(new AppSettings())) as JsonObject ?? new JsonObject();
        JsonObject? old = null;
        try { old = JsonNode.Parse(raw) as JsonObject; } catch { old = null; }

        var result = new JsonObject();
        foreach (var kv in def)
        {
            JsonNode? value;
            if (old == null || !old.TryGetPropertyValue(kv.Key, out var oldValue) || kv.Value == null || oldValue == null)
            {
                value = kv.Value?.DeepClone();
            }
            else if (AreCompatible(kv.Value, oldValue))
            {
                value = oldValue.DeepClone();
            }
            else
            {
                value = kv.Value.DeepClone();
            }
            result[kv.Key] = value;
        }
        return result.ToJsonString();
    }

    private static bool AreCompatible(JsonNode defaultNode, JsonNode oldNode)
    {
        if (defaultNode is JsonObject dobj)
        {
            if (oldNode is not JsonObject oobj) return false;
            if (dobj.Count != oobj.Count) return false;
            foreach (var kv in dobj)
                if (!oobj.TryGetPropertyValue(kv.Key, out var ov) || kv.Value == null || ov == null || !AreCompatible(kv.Value, ov))
                    return false;
            return true;
        }
        if (defaultNode is JsonValue dval)
        {
            if (oldNode is not JsonValue oval) return false;
            var dKind = dval.GetValueKind();
            var oKind = oval.GetValueKind();
            if (dKind is JsonValueKind.False or JsonValueKind.True)
                return oKind is JsonValueKind.False or JsonValueKind.True;
            return dKind == oKind;
        }
        if (defaultNode is null) return oldNode is null;
        return false;
    }
}
