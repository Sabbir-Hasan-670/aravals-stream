using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using AravalsStream.Core.Platforms;

namespace AravalsStream.App.Streaming;

public sealed class PlatformPickerDialog : Window
{
    public PlatformProfile? SelectedProfile { get; private set; }

    public PlatformPickerDialog(Window owner)
    {
        Owner = owner;
        Title = "Add Destination - Select Platform";
        Width = 560;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(27, 29, 36));
        Foreground = Brushes.White;

        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "Choose Platform",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 4)
        };
        Grid.SetRow(title, 0);
        root.Children.Add(title);

        var subtitle = new TextBlock
        {
            Text = "Select a streaming destination to configure with recommended presets and routing.",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
            Margin = new Thickness(0, 0, 0, 16)
        };
        Grid.SetRow(subtitle, 1);
        root.Children.Add(subtitle);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var listPanel = new StackPanel();
        scroll.Content = listPanel;
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);

        foreach (var profile in PlatformRegistry.GetAll())
        {
            var card = CreatePlatformCard(profile);
            listPanel.Children.Add(card);
        }

        var buttonBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        Grid.SetRow(buttonBar, 3);
        root.Children.Add(buttonBar);

        var cancelBtn = new Button
        {
            Content = "Cancel",
            Padding = new Thickness(16, 8, 16, 8),
            Background = new SolidColorBrush(Color.FromRgb(40, 44, 52)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(60, 66, 78))
        };
        cancelBtn.Click += (_, _) => DialogResult = false;
        buttonBar.Children.Add(cancelBtn);

        Content = root;
    }

    private Border CreatePlatformCard(PlatformProfile profile)
    {
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(35, 39, 48)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(55, 61, 75)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 10),
            Cursor = System.Windows.Input.Cursors.Hand
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Brand icon
        var iconColor = (Color)ColorConverter.ConvertFromString(profile.BrandColor);
        var iconBrush = new SolidColorBrush(iconColor);

        var iconBox = new Viewbox
        {
            Width = 28,
            Height = 28,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var path = new Path
        {
            Data = Geometry.Parse(profile.IconData),
            Fill = iconBrush
        };
        iconBox.Child = path;
        Grid.SetColumn(iconBox, 0);
        grid.Children.Add(iconBox);

        // Platform info
        var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var nameText = new TextBlock
        {
            Text = profile.DisplayName,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White
        };
        infoStack.Children.Add(nameText);

        var descText = new TextBlock
        {
            Text = profile.Description,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        infoStack.Children.Add(descText);

        Grid.SetColumn(infoStack, 1);
        grid.Children.Add(infoStack);

        // Capability badges
        var badgeStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (profile.SupportsHorizontal && profile.SupportsVertical)
        {
            badgeStack.Children.Add(CreateBadge("H + V", Color.FromRgb(59, 130, 246)));
        }
        else if (profile.SupportsHorizontal)
        {
            badgeStack.Children.Add(CreateBadge("HORIZONTAL", Color.FromRgb(16, 185, 129)));
        }
        else if (profile.SupportsVertical)
        {
            badgeStack.Children.Add(CreateBadge("VERTICAL", Color.FromRgb(245, 158, 11)));
        }

        Grid.SetColumn(badgeStack, 2);
        grid.Children.Add(badgeStack);

        border.Child = grid;

        // Hover styling
        border.MouseEnter += (_, _) =>
        {
            border.Background = new SolidColorBrush(Color.FromRgb(45, 51, 62));
            border.BorderBrush = iconBrush;
        };
        border.MouseLeave += (_, _) =>
        {
            border.Background = new SolidColorBrush(Color.FromRgb(35, 39, 48));
            border.BorderBrush = new SolidColorBrush(Color.FromRgb(55, 61, 75));
        };
        border.MouseLeftButtonDown += (_, _) =>
        {
            SelectedProfile = profile;
            DialogResult = true;
        };

        return border;
    }

    private static Border CreateBadge(string text, Color color)
    {
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(40, color.R, color.G, color.B)),
            BorderBrush = new SolidColorBrush(color),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(4, 0, 0, 0),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(color)
            }
        };
    }
}
