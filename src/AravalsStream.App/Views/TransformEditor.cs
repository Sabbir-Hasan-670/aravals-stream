using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using AravalsStream.Core.Models;
using AravalsStream.App.Controls;

namespace AravalsStream.App.Views;

public sealed class TransformEditor : Window
{
    private readonly Dictionary<string, TextBox> _fields = [];
    private readonly SourceTransform _transform;

    public TransformEditor(SourceTransform transform, OutputMode mode)
    {
        DarkWindowChrome.Apply(this);
        _transform = transform;
        Title = $"Source Transform — Editing: {mode}";
        Width = 380; Height = 580; MinWidth = 340; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(18) };
        var save = new Button { Content = "Apply", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
        save.Click += Save_Click;
        DockPanel.SetDock(save, Dock.Bottom); root.Children.Add(save);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var panel = new StackPanel(); scroll.Content = panel; root.Children.Add(scroll);
        Add(panel, "Position X", transform.X); Add(panel, "Position Y", transform.Y);
        Add(panel, "Width", transform.Width); Add(panel, "Height", transform.Height);
        Add(panel, "Rotation", transform.Rotation); Add(panel, "Opacity (0–1)", transform.Opacity);
        Add(panel, "Crop Left", transform.CropLeft); Add(panel, "Crop Top", transform.CropTop);
        Add(panel, "Crop Right", transform.CropRight); Add(panel, "Crop Bottom", transform.CropBottom);
        if (mode == OutputMode.Vertical)
        {
            Add(panel, "Focus X (0–1)", transform.FocusX);
        }
        Content = root;
    }

    private void Add(Panel panel, string label, double value)
    {
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        var text = new TextBlock { Text = label, Width = 140, VerticalAlignment = VerticalAlignment.Center };
        var input = new TextBox { Text = value.ToString(CultureInfo.CurrentCulture), MinWidth = 130 };
        DockPanel.SetDock(text, Dock.Left); row.Children.Add(text); row.Children.Add(input);
        _fields[label] = input; panel.Children.Add(row);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var values = new Dictionary<string, double>();
        foreach (var (label, input) in _fields)
        {
            if (!double.TryParse(input.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) || !double.IsFinite(value))
            { MessageBox.Show(this, $"Enter a valid number for {label}."); input.Focus(); return; }
            values[label] = value;
        }
        if (values["Width"] <= 0 || values["Height"] <= 0 || values["Opacity (0–1)"] is < 0 or > 1 ||
            values["Crop Left"] < 0 || values["Crop Top"] < 0 || values["Crop Right"] < 0 || values["Crop Bottom"] < 0)
        { MessageBox.Show(this, "Width and height must be positive; opacity must be 0–1; crops cannot be negative."); return; }
        if (values.TryGetValue("Focus X (0–1)", out var focusVal) && (focusVal < 0 || focusVal > 1))
        { MessageBox.Show(this, "Focus X must be between 0 and 1."); return; }

        _transform.X = values["Position X"]; _transform.Y = values["Position Y"];
        _transform.Width = values["Width"]; _transform.Height = values["Height"];
        _transform.Rotation = values["Rotation"]; _transform.Opacity = values["Opacity (0–1)"];
        _transform.CropLeft = values["Crop Left"]; _transform.CropTop = values["Crop Top"];
        _transform.CropRight = values["Crop Right"]; _transform.CropBottom = values["Crop Bottom"];
        if (values.TryGetValue("Focus X (0–1)", out var fVal))
        {
            _transform.FocusX = fVal;
        }
        _transform.LayoutMode = VerticalLayoutMode.Manual;
        DialogResult = true;
    }
}
