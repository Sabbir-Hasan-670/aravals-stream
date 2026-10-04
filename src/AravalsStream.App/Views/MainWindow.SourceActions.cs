using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AravalsStream.Core.Audio;
using AravalsStream.Core.Models;

namespace AravalsStream.App.Views;

public partial class MainWindow
{
    private Point _sourceDragStart;
    private SceneSource? _sourceDragCandidate;

    private void SourceDrag_Start(object sender, MouseButtonEventArgs e)
    {
        _sourceDragCandidate = null;
        for (var element = e.OriginalSource as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is ButtonBase) return;
            if (element is ListBoxItem { DataContext: SceneSource source })
            { _sourceDragCandidate = source; _sourceDragStart = e.GetPosition(SourceList); break; }
        }
    }
    private void SourceDrag_Move(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _sourceDragCandidate is not { } source) return;
        var position = e.GetPosition(SourceList);
        if (Math.Abs(position.X - _sourceDragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(position.Y - _sourceDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _sourceDragCandidate = null;
        DragDrop.DoDragDrop(SourceList, new DataObject(typeof(SceneSource), source), DragDropEffects.Move);
    }
    private void SourceDrag_Over(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetData(typeof(SceneSource)) is SceneSource source && ViewModel.SelectedScene?.Sources.Contains(source) == true ? DragDropEffects.Move : DragDropEffects.None;
        if (e.Effects == DragDropEffects.Move)
        {
            var scroll = FindScroll(SourceList);
            var y = e.GetPosition(SourceList).Y;
            if (y < 28) scroll?.LineUp(); else if (y > SourceList.ActualHeight - 28) scroll?.LineDown();
        }
        e.Handled = true;
    }
    private static ScrollViewer? FindScroll(DependencyObject root)
    {
        if (root is ScrollViewer scroll) return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScroll(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
    private void SourceDrag_Drop(object sender, DragEventArgs e)
    {
        if (ViewModel.SelectedScene is not { } scene || e.Data.GetData(typeof(SceneSource)) is not SceneSource source) return;
        var oldIndex = scene.Sources.IndexOf(source);
        if (oldIndex < 0) return;
        var target = ItemsControl.ContainerFromElement(SourceList, e.OriginalSource as DependencyObject) as ListBoxItem;
        var insertion = scene.Sources.Count;
        if (target?.DataContext is SceneSource targetSource)
        {
            insertion = scene.Sources.IndexOf(targetSource);
            if (e.GetPosition(target).Y > target.ActualHeight / 2) insertion++;
        }
        if (oldIndex < insertion) insertion--;
        var newIndex = Math.Clamp(insertion, 0, scene.Sources.Count - 1);
        if (newIndex != oldIndex) { scene.Sources.Move(oldIndex, newIndex); RefreshPreviews(); _ = SaveSettingsAsync(); }
        SourceList.SelectedItem = source; e.Handled = true;
    }
    private void AudioMixerAction_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not Button { DataContext: AudioChannel channel } button) return;
        if (button.Tag as string == "audio-filters") EditAudioFilters(channel.Id);
        else if (button.Tag as string == "audio-device")
        {
            var source = ViewModel.SelectedScene?.Sources.FirstOrDefault(s => s.SourceReference == channel.Id && s.Type is SourceType.AudioInput or SourceType.AudioOutput);
            if (source is not null) PromptChangeDevice(source);
        }
        e.Handled = true;
    }
    private void EditAudioFilters(Guid resourceId)
    {
        var resource = _captureResources.FirstOrDefault(r => r.Id == resourceId);
        if (resource is null) return;
        var dialog = new AudioFiltersDialog(this, resource.Name, resource.AudioFilters);
        if (dialog.ShowDialog() != true) return;
        resource.AudioFilters = dialog.Result.Copy();
        if (_audioEngine.Channels.FirstOrDefault(c => c.Id == resourceId) is { } channel) channel.Filters = resource.AudioFilters;
        _ = SaveSettingsAsync();
    }
}
