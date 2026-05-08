using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace StickerApp;

public partial class StartupPromptWindow : Window
{
    public string? ResultText { get; private set; }
    public bool CreateNew { get; private set; }
    public int? ExistingIndex { get; private set; }

    private readonly List<StickerWindow> _existing;

    public StartupPromptWindow(List<StickerWindow> existing)
    {
        _existing = existing;
        InitializeComponent();
        ExistingBtn.Visibility = existing.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnNewClick(object sender, RoutedEventArgs e)
    {
        ResultText = InputBox.Text.Trim();
        CreateNew = true;
        DialogResult = true;
    }

    private void OnExistingClick(object sender, RoutedEventArgs e)
    {
        if (_existing.Count == 0) return;

        if (_existing.Count == 1)
        {
            ResultText = InputBox.Text.Trim();
            ExistingIndex = 0;
            DialogResult = true;
            return;
        }

        // Show picker for multiple stickers
        var labels = _existing.Select(s =>
        {
            var firstTask = s.Data.Tasks.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Text));
            var preview = firstTask?.Text ?? "Пустой стикер";
            if (preview.Length > 24) preview = preview[..24] + "…";
            return preview;
        }).ToList();

        StickerPicker.ItemsSource = labels;
        ExistingPanel.Visibility = Visibility.Visible;
    }

    private void OnPickExisting(object sender, RoutedEventArgs e)
    {
        var btn = (Button)sender;
        int idx = StickerPicker.Items.IndexOf(btn.DataContext);
        if (idx < 0) return;
        ResultText = InputBox.Text.Trim();
        ExistingIndex = idx;
        DialogResult = true;
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(InputBox.Text))
        {
            OnNewClick(sender: this, e: new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            OnSkip(sender: this, e: new RoutedEventArgs());
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }
}
