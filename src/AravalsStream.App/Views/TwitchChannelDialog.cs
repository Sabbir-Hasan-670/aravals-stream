using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AravalsStream.Core.Twitch;

namespace AravalsStream.App.Views;

public sealed class TwitchChannelDialog : Window
{
    private readonly TextBox _title = new();
    private readonly TextBox _language = new();
    private readonly TextBox _tags = new();
    private readonly TextBox _categoryQuery = new();
    private readonly ComboBox _categories = new();
    private readonly ComboBox _ingests = new();
    public string StreamTitle => _title.Text.Trim();
    public string? GameId => (_categories.SelectedItem as TwitchCategory)?.Id;
    public string? GameName => (_categories.SelectedItem as TwitchCategory)?.Name;
    public string ChannelLanguage => _language.Text.Trim();
    public List<string> Tags => _tags.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
    public TwitchIngest? Ingest => _ingests.SelectedItem as TwitchIngest;

    public TwitchChannelDialog(Window owner, TwitchApiClient api, string token, TwitchChannelInfo channel)
    {
        Owner = owner;
        Title = "Twitch Channel";
        Width = 500; Height = 530;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(27, 29, 36)); Foreground = Brushes.White;
        var panel = new StackPanel { Margin = new Thickness(22) };
        Content = new ScrollViewer { Content = panel };
        void Add(string label, Control control)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 9, 0, 3) });
            control.MinHeight = 28; panel.Children.Add(control);
        }
        _title.Text = channel.Title;
        _language.Text = channel.Language ?? "en";
        _tags.Text = string.Join(", ", channel.Tags);
        Add("Stream title", _title);
        Add("Category search", _categoryQuery);
        var search = new Button { Content = "Search categories", Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(0, 5, 0, 0) };
        search.Click += async (_, _) =>
        {
            try
            {
                var categories = await api.SearchCategoriesAsync(token, _categoryQuery.Text);
                _categories.ItemsSource = categories;
                _categories.SelectedIndex = categories.Count > 0 ? 0 : -1;
            }
            catch (Exception ex) { MessageBox.Show(this, $"Category search failed: {ex.Message}", "Twitch"); }
        };
        panel.Children.Add(search);
        _categories.DisplayMemberPath = nameof(TwitchCategory.Name);
        if (!string.IsNullOrEmpty(channel.GameId))
        {
            _categories.ItemsSource = new[] { new TwitchCategory(channel.GameId, channel.GameName ?? channel.GameId) };
            _categories.SelectedIndex = 0;
        }
        Add("Category", _categories);
        Add("Language code", _language);
        Add("Tags (comma separated)", _tags);
        _ingests.DisplayMemberPath = nameof(TwitchIngest.Name);
        Add("Ingest (Auto / Recommended selected by default)", _ingests);
        var save = new Button { Content = "Use Twitch account", Padding = new Thickness(12, 7, 12, 7),
            Margin = new Thickness(0, 18, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        save.Click += (_, _) => { if (StreamTitle.Length == 0) { MessageBox.Show(this, "Enter a stream title."); return; } DialogResult = true; };
        panel.Children.Add(save);
        Loaded += async (_, _) =>
        {
            try
            {
                var servers = await api.GetIngestServersAsync();
                _ingests.ItemsSource = servers;
                _ingests.SelectedItem = servers.FirstOrDefault(x => x.IsDefault) ?? servers.FirstOrDefault();
            }
            catch (Exception ex) { MessageBox.Show(this, $"Twitch ingest discovery failed: {ex.Message}", "Twitch"); }
        };
    }
}
