using System.Windows;

namespace TiaMc.App.Views;

/// <summary>Small TIA styled prompt used for the offline account name.</summary>
public partial class TextInputWindow : Window
{
    public TextInputWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    public string Value
    {
        get => ValueBox.Text;
        set => ValueBox.Text = value;
    }

    public static string? Prompt(Window? owner, string title, string prompt, string initial)
    {
        var window = new TextInputWindow
        {
            Owner = owner,
            Title = title,
            TitleText = { Text = title },
            PromptText = { Text = prompt },
            Value = initial
        };

        return window.ShowDialog() == true ? window.Value.Trim() : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ValueBox.Text))
        {
            MessageBox.Show(this, "请输入内容。", "TIA-MC", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
