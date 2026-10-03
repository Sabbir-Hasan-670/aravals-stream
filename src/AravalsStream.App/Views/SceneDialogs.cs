using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AravalsStream.App.Controls;
using AravalsStream.Core.Services;

namespace AravalsStream.App.Views;

public static class SceneDialogs
{
    private static T FindRes<T>(string key) => (T)Application.Current.FindResource(key);

    public static bool ShowCreateSceneDialog(Window owner, IEnumerable<string> existingNames, out string sceneName)
    {
        sceneName = string.Empty;

        var existingList = existingNames.ToList();
        var defaultName = SceneManager.GenerateDuplicateName("Scene", existingList);
        if (defaultName.EndsWith(" Copy"))
            defaultName = $"Scene {existingList.Count + 1}";

        var dialog = CreateBaseDialog(owner, "Create Scene", 400);

        var root = new Border
        {
            Background = FindRes<Brush>("CardBrush"),
            BorderBrush = FindRes<Brush>("BorderBrushDark"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(20)
        };

        var stack = new StackPanel();

        var header = new TextBlock
        {
            Text = "CREATE SCENE",
            Style = FindRes<Style>("SectionTitle"),
            Margin = new Thickness(0, 0, 0, 6)
        };

        var subtitle = new TextBlock
        {
            Text = "Enter a name for the new scene.",
            Style = FindRes<Style>("MutedText"),
            Margin = new Thickness(0, 0, 0, 16)
        };

        var label = new TextBlock
        {
            Text = "NAME",
            Style = FindRes<Style>("TinyText"),
            Margin = new Thickness(0, 0, 0, 6)
        };

        var textBox = new TextBox
        {
            Text = defaultName,
            MaxLength = SceneManager.MaxSceneNameLength,
            Margin = new Thickness(0, 0, 0, 8)
        };

        var errorText = new TextBlock
        {
            Foreground = FindRes<Brush>("DangerBrush"),
            FontSize = 11,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 0, 12)
        };

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };

        var cancelButton = new Button
        {
            Content = "Cancel",
            Style = FindRes<Style>("GhostButton"),
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(14, 6, 14, 6)
        };

        var createButton = new Button
        {
            Content = "Create",
            Style = FindRes<Style>("PrimaryButton"),
            Padding = new Thickness(18, 6, 18, 6),
            MinHeight = 36
        };

        void ValidateAndSubmit()
        {
            var text = textBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                errorText.Text = "Name cannot be empty.";
                errorText.Visibility = Visibility.Visible;
                textBox.Focus();
                return;
            }

            dialog.DialogResult = true;
            dialog.Close();
        }

        cancelButton.Click += (_, _) => { dialog.DialogResult = false; dialog.Close(); };
        createButton.Click += (_, _) => ValidateAndSubmit();

        buttonPanel.Children.Add(cancelButton);
        buttonPanel.Children.Add(createButton);

        stack.Children.Add(header);
        stack.Children.Add(subtitle);
        stack.Children.Add(label);
        stack.Children.Add(textBox);
        stack.Children.Add(errorText);
        stack.Children.Add(buttonPanel);

        root.Child = stack;
        dialog.Content = root;

        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                ValidateAndSubmit();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                dialog.DialogResult = false;
                dialog.Close();
            }
        };

        dialog.Loaded += (_, _) =>
        {
            textBox.Focus();
            textBox.SelectAll();
        };

        if (dialog.ShowDialog() == true)
        {
            sceneName = textBox.Text.Trim();
            return true;
        }

        return false;
    }

    public static bool ShowRenameSceneDialog(Window owner, string currentName, out string newName)
    {
        newName = currentName;

        var dialog = CreateBaseDialog(owner, "Rename Scene", 400);

        var root = new Border
        {
            Background = FindRes<Brush>("CardBrush"),
            BorderBrush = FindRes<Brush>("BorderBrushDark"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(20)
        };

        var stack = new StackPanel();

        var header = new TextBlock
        {
            Text = "RENAME SCENE",
            Style = FindRes<Style>("SectionTitle"),
            Margin = new Thickness(0, 0, 0, 6)
        };

        var subtitle = new TextBlock
        {
            Text = "Update the display name for this scene.",
            Style = FindRes<Style>("MutedText"),
            Margin = new Thickness(0, 0, 0, 16)
        };

        var label = new TextBlock
        {
            Text = "NAME",
            Style = FindRes<Style>("TinyText"),
            Margin = new Thickness(0, 0, 0, 6)
        };

        var textBox = new TextBox
        {
            Text = currentName,
            MaxLength = SceneManager.MaxSceneNameLength,
            Margin = new Thickness(0, 0, 0, 8)
        };

        var errorText = new TextBlock
        {
            Foreground = FindRes<Brush>("DangerBrush"),
            FontSize = 11,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 0, 12)
        };

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };

        var cancelButton = new Button
        {
            Content = "Cancel",
            Style = FindRes<Style>("GhostButton"),
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(14, 6, 14, 6)
        };

        var saveButton = new Button
        {
            Content = "Save",
            Style = FindRes<Style>("PrimaryButton"),
            Padding = new Thickness(18, 6, 18, 6),
            MinHeight = 36
        };

        void ValidateAndSubmit()
        {
            var text = textBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                errorText.Text = "Name cannot be empty.";
                errorText.Visibility = Visibility.Visible;
                textBox.Focus();
                return;
            }

            dialog.DialogResult = true;
            dialog.Close();
        }

        cancelButton.Click += (_, _) => { dialog.DialogResult = false; dialog.Close(); };
        saveButton.Click += (_, _) => ValidateAndSubmit();

        buttonPanel.Children.Add(cancelButton);
        buttonPanel.Children.Add(saveButton);

        stack.Children.Add(header);
        stack.Children.Add(subtitle);
        stack.Children.Add(label);
        stack.Children.Add(textBox);
        stack.Children.Add(errorText);
        stack.Children.Add(buttonPanel);

        root.Child = stack;
        dialog.Content = root;

        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                ValidateAndSubmit();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                dialog.DialogResult = false;
                dialog.Close();
            }
        };

        dialog.Loaded += (_, _) =>
        {
            textBox.Focus();
            textBox.SelectAll();
        };

        if (dialog.ShowDialog() == true)
        {
            newName = textBox.Text.Trim();
            return true;
        }

        return false;
    }

    public static bool ShowDeleteConfirmationDialog(Window owner, string sceneName)
    {
        var dialog = CreateBaseDialog(owner, "Delete Scene", 440);

        var root = new Border
        {
            Background = FindRes<Brush>("CardBrush"),
            BorderBrush = FindRes<Brush>("BorderBrushDark"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(22)
        };

        var stack = new StackPanel();

        var header = new TextBlock
        {
            Text = "DELETE SCENE",
            Style = FindRes<Style>("SectionTitle"),
            Margin = new Thickness(0, 0, 0, 10)
        };

        var question = new TextBlock
        {
            Text = $"Delete scene \"{sceneName}\"?",
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Foreground = FindRes<Brush>("TextBrush"),
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap
        };

        var explanation = new TextBlock
        {
            Text = "This removes the scene and its scene-specific layout. Shared capture resources used by other scenes will not be removed.",
            Foreground = FindRes<Brush>("SecondaryBrush"),
            FontSize = 12,
            LineHeight = 18,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 18)
        };

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var cancelButton = new Button
        {
            Content = "Cancel",
            Style = FindRes<Style>("GhostButton"),
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(14, 6, 14, 6)
        };

        var deleteButton = new Button
        {
            Content = "Delete",
            Style = FindRes<Style>("DangerButton"),
            Padding = new Thickness(18, 6, 18, 6),
            MinHeight = 36
        };

        cancelButton.Click += (_, _) => { dialog.DialogResult = false; dialog.Close(); };
        deleteButton.Click += (_, _) => { dialog.DialogResult = true; dialog.Close(); };

        buttonPanel.Children.Add(cancelButton);
        buttonPanel.Children.Add(deleteButton);

        stack.Children.Add(header);
        stack.Children.Add(question);
        stack.Children.Add(explanation);
        stack.Children.Add(buttonPanel);

        root.Child = stack;
        dialog.Content = root;

        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                dialog.DialogResult = true;
                dialog.Close();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                dialog.DialogResult = false;
                dialog.Close();
            }
        };

        dialog.Loaded += (_, _) => cancelButton.Focus();

        return dialog.ShowDialog() == true;
    }

    public static bool ShowRemoveSourceConfirmationDialog(Window owner, string sourceName)
    {
        var dialog = CreateBaseDialog(owner, "Remove Source", 440);

        var root = new Border
        {
            Background = FindRes<Brush>("CardBrush"),
            BorderBrush = FindRes<Brush>("BorderBrushDark"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(22)
        };

        var stack = new StackPanel();

        var header = new TextBlock
        {
            Text = "REMOVE SOURCE",
            Style = FindRes<Style>("SectionTitle"),
            Margin = new Thickness(0, 0, 0, 10)
        };

        var question = new TextBlock
        {
            Text = $"Remove \"{sourceName}\" from scene?",
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Foreground = FindRes<Brush>("TextBrush"),
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap
        };

        var explanation = new TextBlock
        {
            Text = "This will remove the source from the current scene. Shared capture resources used by other scenes will remain active.",
            Foreground = FindRes<Brush>("SecondaryBrush"),
            FontSize = 12,
            LineHeight = 18,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 18)
        };

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var cancelButton = new Button
        {
            Content = "Cancel",
            Style = FindRes<Style>("GhostButton"),
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(14, 6, 14, 6)
        };

        var removeButton = new Button
        {
            Content = "Remove",
            Style = FindRes<Style>("DangerButton"),
            Padding = new Thickness(18, 6, 18, 6),
            MinHeight = 36
        };

        cancelButton.Click += (_, _) => { dialog.DialogResult = false; dialog.Close(); };
        removeButton.Click += (_, _) => { dialog.DialogResult = true; dialog.Close(); };

        buttonPanel.Children.Add(cancelButton);
        buttonPanel.Children.Add(removeButton);

        stack.Children.Add(header);
        stack.Children.Add(question);
        stack.Children.Add(explanation);
        stack.Children.Add(buttonPanel);

        root.Child = stack;
        dialog.Content = root;

        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                dialog.DialogResult = true;
                dialog.Close();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                dialog.DialogResult = false;
                dialog.Close();
            }
        };

        dialog.Loaded += (_, _) => cancelButton.Focus();

        return dialog.ShowDialog() == true;
    }

    private static Window CreateBaseDialog(Window owner, string title, double width)
    {
        var dialog = new Window
        {
            Title = title,
            Owner = owner,
            Width = width,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = FindRes<Brush>("ShellBrush")
        };
        DarkWindowChrome.Apply(dialog);
        return dialog;
    }
}
