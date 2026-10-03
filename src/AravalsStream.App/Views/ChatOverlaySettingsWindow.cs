using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AravalsStream.Core.Alerts;

namespace AravalsStream.App.Views;

public sealed class ChatOverlaySettingsWindow : Window
{
    public ChatOverlaySettingsWindow(AlertSettings alertSettings)
    {
        var settings = alertSettings.ChatOverlay;
        Title = "Chat Overlay Settings"; Width = 480; Height = 570;
        Background = new SolidColorBrush(Color.FromRgb(18, 26, 38)); Foreground = Brushes.White;
        AravalsStream.App.Controls.DarkWindowChrome.Apply(this);
        var form = new StackPanel { Margin = new Thickness(20) };
        Content = new ScrollViewer { Content = form };
        var enabled = Check("Show chat overlay", settings.Enabled);
        var youtube = Check("YouTube", settings.Platforms.Contains("YouTube"));
        var twitch = Check("Twitch", settings.Platforms.Contains("Twitch"));
        var facebook = Check("Facebook", settings.Platforms.Contains("Facebook"));
        form.Children.Add(enabled);
        form.Children.Add(youtube); form.Children.Add(twitch); form.Children.Add(facebook);
        var max = Field("Max messages", settings.MaxMessages.ToString());
        var duration = Field("Message duration (seconds)", settings.MessageDurationSeconds.ToString());
        var font = Field("Font size", settings.FontSize.ToString());
        var opacity = Field("Background opacity (0-1)", settings.BackgroundOpacity.ToString("0.00"));
        var words = Field("Blocked words (comma separated)", string.Join(", ", settings.BlockedWords));
        var users = Field("Blocked user IDs (comma separated)", string.Join(", ", settings.BlockedUserIds));
        var links = Check("Hide links", settings.HideLinks);
        var badges = Check("Show badges", settings.ShowBadges);
        var timestamps = Check("Show timestamps", settings.ShowTimestamps);
        var icons = Check("Show platform names", settings.ShowPlatformIcon);
        var avatars = Check("Show author initials", settings.ShowAvatars);
        var saveLog = Check("Save chat log to local app data", alertSettings.SaveChatLog);
        foreach (var control in new Control[] { links, badges, timestamps, icons, avatars, saveLog }) form.Children.Add(control);
        form.Children.Add(new TextBlock { Text = "Kick receive needs a public webhook. TikTok LIVE chat has no official public API.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray, Margin = new Thickness(0, 10, 0, 10) });
        var save = new Button { Content = "Save", Padding = new Thickness(10) };
        save.Click += (_, _) =>
        {
            settings.Enabled = enabled.IsChecked == true;
            settings.Platforms = new[] { ("YouTube", youtube), ("Twitch", twitch), ("Facebook", facebook) }
                .Where(x => x.Item2.IsChecked == true).Select(x => x.Item1).ToList();
            if (int.TryParse(max.Text, out var maxValue)) settings.MaxMessages = Math.Clamp(maxValue, 1, 10);
            if (int.TryParse(duration.Text, out var seconds)) settings.MessageDurationSeconds = Math.Clamp(seconds, 1, 120);
            if (int.TryParse(font.Text, out var size)) settings.FontSize = Math.Clamp(size, 12, 44);
            if (double.TryParse(opacity.Text, out var alpha)) settings.BackgroundOpacity = Math.Clamp(alpha, 0, 1);
            settings.BlockedWords = Split(words.Text); settings.BlockedUserIds = Split(users.Text);
            settings.HideLinks = links.IsChecked == true; settings.ShowBadges = badges.IsChecked == true;
            settings.ShowTimestamps = timestamps.IsChecked == true;
            settings.ShowPlatformIcon = icons.IsChecked == true;
            settings.ShowAvatars = avatars.IsChecked == true;
            alertSettings.SaveChatLog = saveLog.IsChecked == true;
            DialogResult = true;
        };
        form.Children.Add(save);

        CheckBox Check(string label, bool value) => new() { Content = label, IsChecked = value, Margin = new Thickness(0, 5, 0, 5) };
        TextBox Field(string label, string value)
        {
            form.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 10, 0, 3) });
            var box = new TextBox { Text = value, Padding = new Thickness(5) };
            form.Children.Add(box);
            return box;
        }
    }

    private static List<string> Split(string text) => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(x => x.Length <= 60).Take(100).ToList();
}
