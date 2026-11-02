using System.Text.Json;
using Passwrd.Models;

namespace Passwrd.Services;

public sealed record SettingsLoadResult(
    AppSettings Settings,
    string? ErrorMessage,
    bool IsFirstRun);

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public SettingsStore()
    {
        SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Passwrd",
            "settings.json");
    }

    public string SettingsPath { get; }

    public SettingsLoadResult Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return new SettingsLoadResult(CreateDefaultSettings(), null, true);
        }

        try
        {
            string json = File.ReadAllText(SettingsPath);
            AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (settings is null)
            {
                throw new JsonException("配置文件内容为空。");
            }

            Normalize(settings);
            return new SettingsLoadResult(settings, null, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new SettingsLoadResult(
                CreateDefaultSettings(),
                $"读取配置失败，已载入默认配置：{ex.Message}",
                true);
        }
    }

    public void Save(AppSettings settings)
    {
        string? directory = Path.GetDirectoryName(SettingsPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("无法确定配置目录。");
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = SettingsPath + ".tmp";
        string json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, SettingsPath, true);
    }

    private static AppSettings CreateDefaultSettings()
    {
        return new AppSettings
        {
            Shortcuts =
            [
                new ShortcutEntry
                {
                    Name = "示例输入",
                    Text = "这是一段由 Passwrd 输入的示例文本。",
                    Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt,
                    Key = 0x31,
                },
            ],
        };
    }

    private static void Normalize(AppSettings settings)
    {
        settings.Shortcuts ??= [];
        foreach (ShortcutEntry entry in settings.Shortcuts)
        {
            if (entry.Id == Guid.Empty)
            {
                entry.Id = Guid.NewGuid();
            }

            entry.Name = string.IsNullOrWhiteSpace(entry.Name) ? "未命名快捷输入" : entry.Name;
            entry.Text ??= string.Empty;
            if (entry.Key == 0)
            {
                entry.Key = 0x31;
            }
        }

        if (settings.Shortcuts.Count == 0)
        {
            foreach (ShortcutEntry entry in CreateDefaultSettings().Shortcuts)
            {
                settings.Shortcuts.Add(entry);
            }
        }
    }
}
