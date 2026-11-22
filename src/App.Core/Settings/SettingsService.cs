using System.Text.Json;

namespace App.Core.Settings;

public interface ISettingsService
{
    string FilePath { get; }
    string Get(string key, string defaultValue);
    void Set(string key, string value);
    event Action<string, string>? Changed;
}

/// <summary>Stocke les paramètres dans %AppData%/KeyceCode/settings.json (comme settings.json de VS Code).</summary>
public sealed class JsonSettingsService : ISettingsService
{
    private readonly Dictionary<string, string> _values = new();
    private readonly object _lock = new();
    public string FilePath { get; }
    public event Action<string, string>? Changed;

    public JsonSettingsService(string? path = null)
    {
        FilePath = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeyceCode", "settings.json");
        try
        {
            if (File.Exists(FilePath))
                _values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { _values = new(); } // fichier corrompu : valeurs par défaut
    }

    public string Get(string key, string defaultValue)
    {
        lock (_lock) return _values.TryGetValue(key, out var v) ? v : defaultValue;
    }

    public void Set(string key, string value)
    {
        lock (_lock)
        {
            _values[key] = value;
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(_values, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* TODO: journaliser via le service de log */ }
        }
        Changed?.Invoke(key, value);
    }
}
