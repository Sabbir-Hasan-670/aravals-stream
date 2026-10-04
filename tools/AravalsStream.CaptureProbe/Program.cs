using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AravalsStream.App.Composition;
using AravalsStream.Capture.Display;
using AravalsStream.Capture.Window;
using AravalsStream.Core.Models;

// Local, opt-in hardware regression probe. It owns both test windows and never
// loads or changes the user's scenes, devices, accounts or recording settings.
internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var target = new Window { Title = "Aravals capture probe — animated target", Width = 640, Height = 360, Left = 40, Top = 40 };
        var owner = new Window { Title = "Aravals capture probe — capture owner", Width = 700, Height = 430, Left = 30, Top = 30 };
        var failures = new ConcurrentQueue<string>();
        var windowColors = new ConcurrentDictionary<int, byte>();
        long windowFrames = 0, displayFrames = 0;
        var ticks = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            var color = Color.FromRgb((byte)(++ticks * 13 % 256), (byte)(ticks * 29 % 256), (byte)(ticks * 7 % 256));
            target.Background = new SolidColorBrush(color);
            owner.Background = new SolidColorBrush(color);
        };
        var passed = false;
        target.Loaded += async (_, _) =>
        {
            // The probe runs once and owns the animation and both test windows.
            if (ticks != 0) return;
            timer.Start();
            await Task.Delay(250);
            try
            {
                var hwnd = new WindowInteropHelper(target).Handle;
                using var window = new WindowCaptureService().Start(new WindowInfo(hwnd, target.Title, Process.GetCurrentProcess().ProcessName, 640, 360));
                window.FrameArrived += (_, frame) =>
                {
                    try
                    {
                        Interlocked.Increment(ref windowFrames);
                        var offset = frame.Height / 2 * frame.Stride + frame.Width / 2 * 4;
                        windowColors.TryAdd(frame.Pixels[offset] | frame.Pixels[offset + 1] << 8 | frame.Pixels[offset + 2] << 16, 0);
                    }
                    finally { frame.Dispose(); }
                };
                window.CaptureFailed += (_, error) => failures.Enqueue(error.Message);
                var displays = new DesktopDuplicationCaptureService();
                using var display = displays.Start(displays.EnumerateDisplays().First(d => d.IsPrimary));
                display.TargetFps = 15;
                display.FrameArrived += (_, frame) => { Interlocked.Increment(ref displayFrames); frame.Dispose(); };
                display.CaptureFailed += (_, error) => failures.Enqueue(error.Message);
                using var compositor = new SceneCompositor(Dispatcher.CurrentDispatcher);
                var scene = new Scene { Name = "Probe" };
                var source = new SceneSource { Name = "Performance Test Pattern", Type = SourceType.DisplayCapture, DisplayId = "performance_test_pattern" };
                scene.Sources.Add(source); compositor.SetScene(scene, [scene]);
                await Verify("foreground", 3);
                owner.Show(); owner.Activate();
                await Verify("covered target", 8);
                owner.WindowState = WindowState.Minimized;
                compositor.PreviewVisible = false; compositor.CaptureTargetFps = 15;
                await Verify("capture owner minimized", 16);
                owner.WindowState = WindowState.Normal;
                compositor.PreviewVisible = true; compositor.CaptureTargetFps = 60;
                if (compositor.FrameFor(source) is null) throw new Exception("Preview did not restore its latest frame.");
                for (var i = 0; i < 8; i++)
                { target.Width = 640 + (i % 2) * 100; target.Height = 360 + (i % 2) * 70; await Task.Delay(250); }
                await Verify("restored and resized", 4);
                passed = true;

                async Task Verify(string name, int seconds)
                {
                    var beforeWindow = Interlocked.Read(ref windowFrames); var beforeDisplay = Interlocked.Read(ref displayFrames);
                    windowColors.Clear();
                    using var previous = compositor.AcquireSourceFrame(source, OutputMode.Horizontal);
                    var version = previous?.Version ?? 0;
                    await Task.Delay(TimeSpan.FromSeconds(seconds));
                    using var latest = compositor.AcquireSourceFrame(source, OutputMode.Horizontal);
                    var capturedWindow = Interlocked.Read(ref windowFrames) - beforeWindow;
                    var capturedDisplay = Interlocked.Read(ref displayFrames) - beforeDisplay;
                    Console.WriteLine($"{name}: WGC={capturedWindow}, uniqueColors={windowColors.Count}, DXGI={capturedDisplay}, compositorVersions={(latest?.Version ?? 0) - version}, errors={failures.Count}");
                    if (capturedWindow < seconds * 5 || windowColors.Count < seconds * 3 || capturedDisplay < seconds * 5 || (latest?.Version ?? 0) <= version || !failures.IsEmpty)
                        throw new Exception($"Capture continuity failed during {name}. {string.Join("; ", failures)}");
                }
            }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { timer.Stop(); owner.Close(); target.Close(); app.Shutdown(); }
        };
        target.Show(); app.Run();
        return passed ? 0 : 1;
    }
}
