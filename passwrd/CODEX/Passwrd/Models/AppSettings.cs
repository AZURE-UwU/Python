using System.Collections.ObjectModel;

namespace Passwrd.Models;

public sealed class AppSettings
{
    public bool HotkeysEnabled { get; set; } = true;

    public ObservableCollection<ShortcutEntry> Shortcuts { get; set; } = [];
}
