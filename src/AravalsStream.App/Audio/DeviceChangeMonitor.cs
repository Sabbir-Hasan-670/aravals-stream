using Microsoft.Win32;
using Windows.Devices.Enumeration;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AravalsStream.App.Audio;

public sealed class DeviceChangeMonitor : IDisposable
{
    private readonly List<DeviceWatcher> _watchers = [];
    private readonly MMDeviceEnumerator _audioEnumerator = new();
    private readonly AudioCallback _audioCallback;
    public event Action? Changed;
    public event Action? DefaultAudioChanged;

    private sealed class AudioCallback(DeviceChangeMonitor owner) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => owner.Changed?.Invoke();
        public void OnDeviceAdded(string deviceId) => owner.Changed?.Invoke();
        public void OnDeviceRemoved(string deviceId) => owner.Changed?.Invoke();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string deviceId)
        { owner.DefaultAudioChanged?.Invoke(); owner.Changed?.Invoke(); }
        public void OnPropertyValueChanged(string deviceId, PropertyKey key) { }
    }

    public DeviceChangeMonitor()
    {
        _audioCallback = new AudioCallback(this);
        _audioEnumerator.RegisterEndpointNotificationCallback(_audioCallback);
        foreach (var kind in new[] { DeviceClass.VideoCapture, DeviceClass.AudioCapture, DeviceClass.AudioRender })
        {
            var watcher = DeviceInformation.CreateWatcher(kind);
            watcher.Added += OnAdded;
            watcher.Removed += OnRemoved;
            watcher.Updated += OnUpdated;
            watcher.Start();
            _watchers.Add(watcher);
        }
        SystemEvents.DisplaySettingsChanged += OnDisplay;
    }

    private void OnAdded(DeviceWatcher sender, DeviceInformation info) => Changed?.Invoke();
    private void OnRemoved(DeviceWatcher sender, DeviceInformationUpdate info) => Changed?.Invoke();
    private void OnUpdated(DeviceWatcher sender, DeviceInformationUpdate info) => Changed?.Invoke();
    private void OnDisplay(object? sender, EventArgs args) => Changed?.Invoke();

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplay;
        _audioEnumerator.UnregisterEndpointNotificationCallback(_audioCallback);
        _audioEnumerator.Dispose();
        foreach (var watcher in _watchers)
        {
            watcher.Added -= OnAdded; watcher.Removed -= OnRemoved; watcher.Updated -= OnUpdated;
            if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) watcher.Stop();
        }
        _watchers.Clear();
    }
}
