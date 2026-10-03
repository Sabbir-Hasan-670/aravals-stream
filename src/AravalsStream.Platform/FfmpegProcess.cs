using System.Diagnostics;

namespace AravalsStream.Platform;

public sealed class FfmpegProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task _errors;
    public Stream Video => _process.StandardOutput.BaseStream;
    public int Id => _process.Id;
    public bool HasExited => _process.HasExited;
    // Never expose raw stderr; it may contain an output stream key.
    public string? Error => HasExited && _process.ExitCode != 0 ? "The media process stopped. Check the device, permissions and output connection." : null;

    public FfmpegProcess(string executable, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        _process = Process.Start(start) ?? throw new IOException("Unable to start FFmpeg.");
        _errors = DrainErrors();
    }

    private async Task DrainErrors()
    {
        while (await _process.StandardError.ReadLineAsync() is not null) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            try { await _process.StandardInput.WriteLineAsync("q"); await _process.StandardInput.FlushAsync(); } catch (IOException) { }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            try { await _process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { _process.Kill(entireProcessTree: true); await _process.WaitForExitAsync(); }
        }
        await _errors;
        _process.Dispose();
    }

    public static string FindExecutable()
    {
        var file = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        foreach (var candidate in new[] { Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", file), Path.Combine(AppContext.BaseDirectory, file) })
            if (File.Exists(candidate)) return candidate;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var candidate = Path.Combine(directory.Trim('"'), file);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("FFmpeg is required. Install it or use a package containing the bundled media tools.");
    }
}
