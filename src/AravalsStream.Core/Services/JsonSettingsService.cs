using System.Text.Json;
using AravalsStream.Core.Interfaces;
using AravalsStream.Core.Alerts;
using AravalsStream.Core.Models;
using AravalsStream.Core.Platforms;
using AravalsStream.Core.Settings;

namespace AravalsStream.Core.Services;

public sealed class JsonSettingsService : ISettingsService
{
    private readonly string _path;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public JsonSettingsService(string? path = null) =>
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AravalsStream", "settings.json");

    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return new();
        await using var stream = File.OpenRead(_path);
        var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, Options, ct) ?? new();
        MigrateSettings(settings);
        return settings;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await using var stream = File.Create(_path);
        await JsonSerializer.SerializeAsync(stream, settings, Options, ct);
    }

    public static void MigrateSettings(AppSettings settings)
    {
        settings.General ??= new();
        settings.Performance ??= new();
        settings.Audio ??= new();
        settings.Canvas ??= new();
        if (!settings.Canvas.IsValid) settings.Canvas = new();
        settings.Streaming ??= new();
        settings.Hotkeys ??= [];
        settings.AudioRoutes ??= [];
        settings.GoogleOAuth ??= new();
        settings.TwitchOAuth ??= new();
        settings.KickOAuth ??= new();
        settings.Alerts ??= new();
        settings.Alerts.Definitions ??= AlertSettings.Defaults();
        settings.Alerts.ChatOverlay ??= new();
        settings.Relay ??= new();
        settings.TikTokOAuth ??= new();
        settings.YouTubeAccounts ??= [];
        if (settings.YouTubeAccount != null && !settings.YouTubeAccounts.Any(a => a.Id == settings.YouTubeAccount.Id))
        {
            settings.YouTubeAccounts.Add(settings.YouTubeAccount);
        }

        if (settings.Hotkeys.Count == 0)
        {
            settings.Hotkeys.Add(new HotkeyBinding { Action = HotkeyAction.ToggleStreaming, DisplayName = "Start / Stop Stream", Key = 0x78, ShortcutText = "F9", Enabled = true }); // F9
            settings.Hotkeys.Add(new HotkeyBinding { Action = HotkeyAction.ToggleRecording, DisplayName = "Start / Stop Recording", Key = 0x79, ShortcutText = "F10", Enabled = true }); // F10
            settings.Hotkeys.Add(new HotkeyBinding { Action = HotkeyAction.PauseResumeRecording, DisplayName = "Pause / Resume Recording", Key = 0x7A, ShortcutText = "F11", Enabled = true }); // F11
            settings.Hotkeys.Add(new HotkeyBinding { Action = HotkeyAction.MuteMicrophone, DisplayName = "Mute Microphone", Key = 0x77, ShortcutText = "F8", Enabled = true }); // F8
            settings.Hotkeys.Add(new HotkeyBinding { Action = HotkeyAction.MuteDesktopAudio, DisplayName = "Mute Desktop Audio", Key = 0x76, ShortcutText = "F7", Enabled = true }); // F7
            for (int i = 1; i <= 9; i++)
            {
                var action = (HotkeyAction)Enum.Parse(typeof(HotkeyAction), $"SwitchScene{i}");
                settings.Hotkeys.Add(new HotkeyBinding { Action = action, DisplayName = $"Switch Scene {i}", Key = 0x60 + i, ShortcutText = $"Num {i}", Enabled = true });
            }
        }

        settings.SettingsSchemaVersion = 9;

        if (settings.DestinationGroups.Count == 0 && settings.Destinations.Count > 0)
        {
            int order = 0;
            foreach (var oldDest in settings.Destinations)
            {
                var profile = PlatformRegistry.Get(oldDest.Platform);
                var routing = oldDest.OutputMode switch
                {
                    OutputMode.Vertical => RoutingMode.Vertical,
                    OutputMode.Both => RoutingMode.Both,
                    _ => RoutingMode.Horizontal
                };

                var group = new PlatformDestinationGroup
                {
                    Id = oldDest.Id,
                    PlatformType = profile.PlatformType,
                    Platform = oldDest.Platform,
                    Name = oldDest.Name,
                    Enabled = oldDest.Enabled,
                    Routing = routing,
                    Order = order++,
                    ServerUrl = oldDest.StreamUrl,
                    StreamKeyReference = oldDest.StreamKeyReference
                };

                if (oldDest.OutputMode == OutputMode.Vertical)
                {
                    group.Vertical = oldDest.Copy();
                    group.Horizontal = new Destination
                    {
                        OutputMode = OutputMode.Horizontal,
                        Platform = oldDest.Platform,
                        Name = $"{oldDest.Name} - H",
                        StreamUrl = oldDest.StreamUrl,
                        StreamKeyReference = oldDest.StreamKeyReference,
                        VideoBitrateKbps = profile.DefaultVideoBitrateKbps,
                        AudioBitrateKbps = oldDest.AudioBitrateKbps,
                        FrameRate = profile.DefaultFps
                    };
                }
                else
                {
                    group.Horizontal = oldDest.Copy();
                    group.Vertical = new Destination
                    {
                        OutputMode = OutputMode.Vertical,
                        Platform = oldDest.Platform,
                        Name = $"{oldDest.Name} - V",
                        StreamUrl = oldDest.StreamUrl,
                        StreamKeyReference = oldDest.StreamKeyReference,
                        VideoBitrateKbps = Math.Min(oldDest.VideoBitrateKbps, 5000),
                        AudioBitrateKbps = oldDest.AudioBitrateKbps,
                        FrameRate = 30
                    };
                }

                group.EnsureChildDestinations();
                group.UpdateAggregation();
                settings.DestinationGroups.Add(group);
            }
        }
        else
        {
            foreach (var group in settings.DestinationGroups)
            {
                group.EnsureChildDestinations();
                group.UpdateAggregation();
            }
        }
    }
}
