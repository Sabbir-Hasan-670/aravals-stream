using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace AravalsStream.Core.Models;

public sealed class Scene : INotifyPropertyChanged
{
    public Guid Id { get; init; } = Guid.NewGuid();

    private string _name = "New Scene";
    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    private bool _enabled = true;
    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }

    private bool _isActive;
    [JsonIgnore]
    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }

    public ObservableCollection<SceneSource> Sources { get; init; } = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    public Scene Clone(string? newName = null)
    {
        var copy = new Scene
        {
            Id = Guid.NewGuid(),
            Name = newName ?? $"{Name} Copy",
            Enabled = Enabled
        };
        foreach (var source in Sources)
        {
            copy.Sources.Add(source.Clone());
        }
        return copy;
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class SceneSource : INotifyPropertyChanged
{
    public Guid Id { get; init; } = Guid.NewGuid();

    private string _name = "Source";
    public string Name
    {
        get => _name;
        set { _name = value; PropertyChanged?.Invoke(this, new(nameof(Name))); }
    }

    public SourceType Type { get; set; }
    [JsonIgnore]
    public bool HasVideo => Type is not (SourceType.AudioInput or SourceType.AudioOutput);
    [JsonIgnore]
    public bool HasAudio => Type is SourceType.AudioInput or SourceType.AudioOutput or SourceType.Camera or SourceType.CaptureDevice or SourceType.RemotePc or SourceType.Video;
    [JsonIgnore]
    public bool CanTransform => HasVideo;
    public string? DisplayId { get; set; }
    public Guid SourceReference { get; set; } = Guid.NewGuid();

    private bool _visible = true;
    public bool Visible
    {
        get => _visible;
        set { _visible = value; PropertyChanged?.Invoke(this, new(nameof(Visible))); }
    }

    private string? _error;
    private CaptureState _state = CaptureState.Stopped;

    [JsonIgnore]
    public CaptureState State
    {
        get => _state;
        set { _state = value; PropertyChanged?.Invoke(this, new(nameof(State))); }
    }

    [JsonIgnore]
    public string? Error
    {
        get => _error;
        set { _error = value; PropertyChanged?.Invoke(this, new(nameof(Error))); }
    }

    private bool _locked;
    public bool Locked
    {
        get => _locked;
        set
        {
            if (_locked == value) return;
            _locked = value;
            HorizontalTransform.Locked = value;
            VerticalTransform.Locked = value;
            PropertyChanged?.Invoke(this, new(nameof(Locked)));
        }
    }

    public SourceTransform HorizontalTransform { get; set; } = new();
    public SourceTransform VerticalTransform { get; set; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public SceneSource Clone() => new()
    {
        Id = Guid.NewGuid(),
        Name = Name,
        Type = Type,
        DisplayId = DisplayId,
        SourceReference = SourceReference,
        Visible = Visible,
        Locked = Locked,
        HorizontalTransform = HorizontalTransform.Clone(),
        VerticalTransform = VerticalTransform.Clone()
    };
}
