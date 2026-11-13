using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace CrushGheChoi.UI;

/// <summary>Hộp thoại hỏi/báo đơn giản (Avalonia không có sẵn MessageBox).</summary>
internal static class Dialogs
{
    public static Task<bool> ConfirmAsync(Window owner, string message, string yes, string no) => ShowAsync(owner, message, yes, no);

    public static Task InfoAsync(Window owner, string message) => ShowAsync(owner, message, "Đã hiểu", null);

    private static async Task<bool> ShowAsync(Window owner, string message, string yes, string? no)
    {
        var dialog = new Window
        {
            Title = "Crush ghé chơi",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#FFF7FA")),
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var yesButton = new Button { Content = yes, IsDefault = true };
        yesButton.Classes.Add("accent");
        yesButton.Click += (_, _) => dialog.Close(true);
        buttons.Children.Add(yesButton);
        if (no is not null)
        {
            var noButton = new Button { Content = no, IsCancel = true };
            noButton.Classes.Add("soft");
            noButton.Click += (_, _) => dialog.Close(false);
            buttons.Children.Add(noButton);
        }

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(22, 20, 22, 18),
            Spacing = 18,
            Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 14 }, buttons },
        };

        return await dialog.ShowDialog<bool?>(owner) == true;
    }
}
