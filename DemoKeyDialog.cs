using System.Windows;
using System.Windows.Controls;

namespace ConnectIVRTester
{
    /// <summary>Small code-built dialog that asks for the demo key.</summary>
    public class DemoKeyDialog : Window
    {
        private readonly System.Windows.Controls.TextBox _keyBox = new() { Padding = new Thickness(4), FontSize = 14, FontFamily = new System.Windows.Media.FontFamily("Consolas") };

        public string EnteredKey => _keyBox.Text.Trim().ToUpperInvariant();

        public DemoKeyDialog(string? message = null)
        {
            Title = "Enter your demo key";
            Width = 460;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;

            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock
            {
                Text = message ?? $"Enter the demo key you received (it looks like DS-XXXX-XXXX-XXXX). Need one? Email {UsageTracker.ContactEmail}.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });
            panel.Children.Add(_keyBox);

            var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var cancel = new System.Windows.Controls.Button { Content = "Cancel", Padding = new Thickness(12, 4, 12, 4), IsCancel = true };
            var ok = new System.Windows.Controls.Button { Content = "OK", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0), IsDefault = true };
            ok.Click += (_, _) =>
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(EnteredKey, @"^DS-[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$"))
                {
                    DialogResult = true;
                }
                else
                {
                    System.Windows.MessageBox.Show(this, "That doesn't look like a valid demo key. It should look like DS-XXXX-XXXX-XXXX.",
                        "Demo key", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            };
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            panel.Children.Add(buttons);
            Content = panel;

            Loaded += (_, _) => _keyBox.Focus();
        }
    }
}
