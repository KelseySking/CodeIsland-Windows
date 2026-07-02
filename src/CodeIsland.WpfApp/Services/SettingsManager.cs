using System.IO;
using System.Text.Json;

namespace CodeIsland.WpfApp.Services;

public sealed class SettingsManager
{
    public const string AutoApproveAllPermissionsKey = "auto_approve_all_permissions";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _settingsPath;
    private Dictionary<string, JsonElement> _settings;

    public SettingsManager(string? settingsDir = null)
    {
        var dir = settingsDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CodeIsland");
        Directory.CreateDirectory(dir);
        SettingsDirectory = dir;
        _settingsPath = Path.Combine(dir, "settings.json");
        _settings = Load();
    }

    public string SettingsDirectory { get; }

    public event EventHandler<SettingChangedEventArgs>? SettingChanged;

    public T Get<T>(string key, T defaultValue)
    {
        if (!_settings.TryGetValue(key, out var element))
            return defaultValue;

        try
        {
            return element.Deserialize<T>() ?? defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    public void Set<T>(string key, T value)
    {
        var oldValue = _settings.TryGetValue(key, out var existing) ? existing : (JsonElement?)null;
        _settings[key] = JsonSerializer.SerializeToElement(value);
        Save();
        SettingChanged?.Invoke(this, new SettingChangedEventArgs(key, oldValue, _settings[key]));
    }

    public bool Has(string key) => _settings.ContainsKey(key);

    public void Remove(string key)
    {
        _settings.Remove(key);
        Save();
    }

    private Dictionary<string, JsonElement> Load()
    {
        if (!File.Exists(_settingsPath))
            return new Dictionary<string, JsonElement>();

        try
        {
            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ??
                   new Dictionary<string, JsonElement>();
        }
        catch
        {
            return new Dictionary<string, JsonElement>();
        }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_settings, JsonOptions);
        File.WriteAllText(_settingsPath, json);
    }
}

public sealed class SettingChangedEventArgs : EventArgs
{
    public SettingChangedEventArgs(string key, JsonElement? oldValue, JsonElement newValue)
    {
        Key = key;
        OldValue = oldValue;
        NewValue = newValue;
    }

    public string Key { get; }
    public JsonElement? OldValue { get; }
    public JsonElement NewValue { get; }
}
