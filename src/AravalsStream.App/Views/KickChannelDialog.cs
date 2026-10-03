using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AravalsStream.Core.Kick;

namespace AravalsStream.App.Views;

public sealed class KickChannelDialog : Window
{
    private readonly TextBox _title = new();
    private readonly TextBox _query = new();
    private readonly ComboBox _categories = new();
    private readonly TextBox _tags = new();
    public string StreamTitle => _title.Text.Trim();
    public long? CategoryId => (_categories.SelectedItem as KickCategory)?.Id;
    public string? CategoryName => (_categories.SelectedItem as KickCategory)?.Name;
    public List<string> Tags => _tags.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public KickChannelDialog(Window owner, KickApiClient api, string token, KickChannel channel)
    {
        Owner = owner;
        Title = "Kick Channel"; Width = 490; Height = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(27, 29, 36)); Foreground = Brushes.White;
        var panel = new StackPanel { Margin = new Thickness(22) };
        Content = panel;
        void Add(string label, Control control)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 10, 0, 3) });
            control.MinHeight = 28; panel.Children.Add(control);
        }
        _title.Text = channel.Title;
        Add("Stream title", _title);
        Add("Category search", _query);
        var search = new Button { Content = "Search categories", Padding = new Thickness(10, 4, 10, 4),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 5, 0, 0) };
        search.Click += async (_, _) =>
        {
            try
            {
                var items = await api.SearchCategoriesAsync(token, _query.Text);
                _categories.ItemsSource = items;
                _categories.SelectedIndex = items.Count > 0 ? 0 : -1;
            }
            catch (Exception ex) { MessageBox.Show(this, $"Category search failed: {ex.Message}", "Kick"); }
        };
        panel.Children.Add(search);
        _categories.DisplayMemberPath = nameof(KickCategory.Name);
        if (channel.CategoryId is { } categoryId)
        {
            _categories.ItemsSource = new[] { new KickCategory(categoryId, channel.CategoryName ?? categoryId.ToString()) };
            _categories.SelectedIndex = 0;
        }
        Add("Category", _categories);
        _tags.Text = string.Join(", ", channel.Tags);
        Add("Custom tags (comma separated)", _tags);
        var save = new Button { Content = "Save", Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 20, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        save.Click += (_, _) =>
        {
            if (StreamTitle.Length == 0) { MessageBox.Show(this, "Enter a stream title."); return; }
            if (Tags.Count > 10) { MessageBox.Show(this, "Kick allows at most 10 custom tags."); return; }
            DialogResult = true;
        };
        panel.Children.Add(save);
    }
}
