using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AravalsStream.Platform;

public sealed record CaptureDevice(CaptureKind Kind, string Identifier, string Name)
{
    public override string ToString() => Name;
}

public static partial class DeviceDiscovery
{
    // Enumeration processes receive no destinations or credentials and never open a recording output.
    public static async Task<IReadOnlyList<CaptureDevice>> DiscoverAsync(string ffmpeg, DesktopPlatform platform, CancellationToken ct = default)
    {
        if (platform == DesktopPlatform.Windows)
        {
            var listing = await Run(ffmpeg, ["-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy"], ct);
            return new[] { new CaptureDevice(CaptureKind.Display, "desktop", "Entire desktop") }.Concat(ParseDirectShow(listing)).ToArray();
        }
        if (platform == DesktopPlatform.MacOS)
            return ParseAvFoundation(await Run(ffmpeg, ["-hide_banner", "-f", "avfoundation", "-list_devices", "true", "-i", ""], ct));

        var devices = new List<CaptureDevice>();
        if (platform == DesktopPlatform.LinuxX11 && Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 } display)
            devices.Add(new(CaptureKind.Display, display, "X11 desktop · " + display));
        // Kernel video nodes are refreshed on every discovery; unavailable nodes are not cached.
        if (Directory.Exists("/sys/class/video4linux"))
            foreach (var node in Directory.EnumerateDirectories("/sys/class/video4linux").Order())
            {
                var identifier = "/dev/" + Path.GetFileName(node);
                if (!File.Exists(identifier)) continue;
                var labelPath = Path.Combine(node, "name");
                var label = File.Exists(labelPath) ? (await File.ReadAllTextAsync(labelPath, ct)).Trim() : identifier;
                devices.Add(new(CaptureKind.Camera, identifier, label + " · " + identifier));
            }
        try { devices.AddRange(ParsePulseSources(await Run("pactl", ["--format=json", "list", "sources"], ct))); }
        catch (System.ComponentModel.Win32Exception) { throw new IOException("Audio discovery requires pactl (PulseAudio utilities) and a running desktop audio service."); }
        return devices;
    }

    public static IReadOnlyList<CaptureDevice> ParseDirectShow(string listing)
    {
        var devices = new List<CaptureDevice>();
        foreach (var line in listing.Split('\n'))
        {
            var match = DirectShowDevice().Match(line);
            if (!match.Success)
            {
                // DirectShow's unique alternative identifier distinguishes identical USB devices.
                var alternative = DirectShowAlternative().Match(line);
                if (alternative.Success && devices.Count > 0)
                    devices[^1] = devices[^1] with { Identifier = alternative.Groups[1].Value };
                continue;
            }
            var kind = match.Groups[2].Value == "video" ? CaptureKind.Camera : CaptureKind.Microphone;
            devices.Add(new(kind, match.Groups[1].Value, match.Groups[1].Value));
        }
        return devices.Distinct().ToArray();
    }

    public static IReadOnlyList<CaptureDevice> ParseAvFoundation(string listing)
    {
        var devices = new List<CaptureDevice>();
        var audio = false;
        foreach (var line in listing.Split('\n'))
        {
            if (line.Contains("AVFoundation video devices:")) { audio = false; continue; }
            if (line.Contains("AVFoundation audio devices:")) { audio = true; continue; }
            var match = AvDevice().Match(line);
            if (!match.Success) continue;
            var name = match.Groups[2].Value.Trim();
            var kind = audio ? CaptureKind.Microphone : name.StartsWith("Capture screen ", StringComparison.Ordinal) ? CaptureKind.Display : CaptureKind.Camera;
            devices.Add(new(kind, match.Groups[1].Value, name));
        }
        return devices.Distinct().ToArray();
    }

    public static IReadOnlyList<CaptureDevice> ParsePulseSources(string listing)
    {
        using var json = JsonDocument.Parse(listing);
        var devices = new List<CaptureDevice>();
        foreach (var source in json.RootElement.EnumerateArray())
        {
            var name = source.GetProperty("name").GetString();
            if (string.IsNullOrWhiteSpace(name)) continue;
            var monitor = source.TryGetProperty("monitor_of_sink", out var sink) &&
                (sink.ValueKind == JsonValueKind.Number ? sink.GetInt64() != uint.MaxValue : sink.ValueKind == JsonValueKind.String && sink.GetString() is not (null or "n/a" or "4294967295"));
            var label = source.TryGetProperty("description", out var description) ? description.GetString() : name;
            devices.Add(new(monitor ? CaptureKind.DesktopAudio : CaptureKind.Microphone, name, label ?? name));
        }
        return devices;
    }

    private static async Task<string> Run(string executable, string[] arguments, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Device discovery could not start.");
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var stdout = await output;
            var stderr = await errors;
            // FFmpeg deliberately exits nonzero after listing its devices.
            if (Path.GetFileNameWithoutExtension(executable) == "pactl" && process.ExitCode != 0)
                throw new IOException("The desktop audio service could not be queried. Check the PulseAudio/PipeWire session.");
            return Path.GetFileNameWithoutExtension(executable) == "pactl" ? stdout : stdout + "\n" + stderr;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            try { await Task.WhenAll(output, errors); } catch (OperationCanceledException) { }
            ct.ThrowIfCancellationRequested();
            throw new IOException("Device discovery timed out. Check native device permissions.");
        }
    }

    [GeneratedRegex("\"([^\"]+)\" \\((video|audio)\\)")]
    private static partial Regex DirectShowDevice();
    [GeneratedRegex("Alternative name \"([^\"]+)\"")]
    private static partial Regex DirectShowAlternative();
    [GeneratedRegex(@"\[AVFoundation[^\]]*\]\s+\[(\d+)\]\s+(.+)$")]
    private static partial Regex AvDevice();
}
