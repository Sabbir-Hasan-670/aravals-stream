using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AravalsStream.Core.Accounts;

namespace AravalsStream.App.Views;

public sealed class FacebookLiveDialog : Window
{
    private readonly ComboBox _pages = new();
    private readonly TextBox _title = new();
    private readonly TextBox _description = new();
    public string? PageId => (_pages.SelectedItem as FacebookPageIdentity)?.PageId;
    public string? PageName => (_pages.SelectedItem as FacebookPageIdentity)?.PageName;
    public string StreamTitle => _title.Text.Trim();
    public string Description => _description.Text.Trim();

    public FacebookLiveDialog(Window owner, FacebookAccount account, string? pageId = null,
        string? title = null, string? description = null)
    {
        Owner = owner; Title = "Facebook Page Live"; Width = 490; Height = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(27, 29, 36)); Foreground = Brushes.White;
        var panel = new StackPanel { Margin = new Thickness(22) }; Content = panel;
        void Add(string label, Control control)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 10, 0, 3) });
            control.MinHeight = 28; panel.Children.Add(control);
        }
        _pages.ItemsSource = account.Pages; _pages.DisplayMemberPath = nameof(FacebookPageIdentity.PageName);
        _pages.SelectedItem = account.Pages.FirstOrDefault(p => p.PageId == (pageId ?? account.SelectedPageId))
            ?? account.Pages.FirstOrDefault();
        Add("Authorized Page", _pages);
        _title.Text = title ?? ""; Add("Live title", _title);
        _description.Text = description ?? ""; _description.Height = 70;
        _description.TextWrapping = TextWrapping.Wrap; _description.AcceptsReturn = true;
        Add("Description", _description);
        panel.Children.Add(new TextBlock
        {
            Text = "A Page Live resource will be requested when you start this output. " +
                   "Meta's platform status is shown separately from the local encoder.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray,
            Margin = new Thickness(0, 12, 0, 0)
        });
        var save = new Button { Content = "Save", Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 18, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        save.Click += (_, _) =>
        {
            if (PageId == null || StreamTitle.Length == 0 || StreamTitle.Length > 254)
            { MessageBox.Show(this, "Choose a Page and enter a title up to 254 characters."); return; }
            DialogResult = true;
        };
        panel.Children.Add(save);
    }
}
