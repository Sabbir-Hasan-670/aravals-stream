namespace AravalsStream.Core.Settings;

public enum HotkeyAction
{
    ToggleStreaming,
    ToggleRecording,
    PauseResumeRecording,
    SwitchScene1,
    SwitchScene2,
    SwitchScene3,
    SwitchScene4,
    SwitchScene5,
    SwitchScene6,
    SwitchScene7,
    SwitchScene8,
    SwitchScene9,
    MuteMicrophone,
    MuteDesktopAudio
}

public sealed class HotkeyBinding
{
    public HotkeyAction Action { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public int Key { get; set; } // Virtual Key code or WPF Key
    public int Modifiers { get; set; } // None, Alt=1, Ctrl=2, Shift=4, Win=8
    public string ShortcutText { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;

    public bool ConflictsWith(HotkeyBinding other)
    {
        if (other == null || !Enabled || !other.Enabled) return false;
        if (Key == 0 || other.Key == 0) return false;
        return Key == other.Key && Modifiers == other.Modifiers;
    }
}
