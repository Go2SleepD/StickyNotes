using System.Windows;
using System.Windows.Media;
using WpfColor = System.Windows.Media.Color;

namespace StickerApp;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly List<SwatchItem> _swatches;

    public SettingsWindow(AppSettings settings)
    {
        _settings = settings;
        _swatches = BuildSwatches();
        InitializeComponent();
        SwatchList.ItemsSource = _swatches;
        RefreshHotkeyButtons();
        RefreshRubberBallButton();
    }

    private void RefreshRubberBallButton() =>
        RubberBallBtn.Content = _settings.RubberBallEnabled ? "Включён" : "Выключен";

    private void OnToggleRubberBall(object sender, RoutedEventArgs e)
    {
        _settings.RubberBallEnabled = !_settings.RubberBallEnabled;
        _settings.Save();
        RefreshRubberBallButton();
        if (_settings.RubberBallEnabled) RubberBallWindow.Spawn();
        else                             RubberBallWindow.Despawn();
    }

    private List<SwatchItem> BuildSwatches()
    {
        string[] colors = ["#5E6AD2", "#2D9CDB", "#00B4D8", "#27AE60",
                           "#F2C94C", "#F2994A", "#E95F8C", "#EB5757"];
        return colors.Select(hex => new SwatchItem(hex, hex == _settings.DefaultAccentColor)).ToList();
    }

    private void RefreshHotkeyButtons()
    {
        CreateHotkeyBtn.Content    = FormatCombo(_settings.HotkeyCreate);
        CompleteHotkeyBtn.Content  = FormatCombo(_settings.HotkeyComplete);
        ClearDeskHotkeyBtn.Content = FormatCombo(_settings.HotkeyClearDesk);
    }

    private static string FormatCombo(string combo) =>
        combo.Length == 0 ? "—" : combo.Replace("+", " + ");

    private void OnChangeCreate(object sender, RoutedEventArgs e)    => ChangeHotkey(CreateHotkeyBtn,    r => _settings.HotkeyCreate    = r);
    private void OnChangeComplete(object sender, RoutedEventArgs e)  => ChangeHotkey(CompleteHotkeyBtn,  r => _settings.HotkeyComplete  = r);
    private void OnChangeClearDesk(object sender, RoutedEventArgs e) => ChangeHotkey(ClearDeskHotkeyBtn, r => _settings.HotkeyClearDesk = r);

    private void ChangeHotkey(System.Windows.Controls.Button btn, Action<string> apply)
    {
        var app = (App)System.Windows.Application.Current;
        app.RecordingHotkey = true;
        try
        {
            var recorder = new HotkeyRecorderWindow { Owner = this };
            recorder.ShowDialog();
            if (recorder.Result is { Length: > 0 } result)
            {
                apply(result);
                btn.Content = FormatCombo(result);
                _settings.Save();
            }
        }
        finally
        {
            app.RecordingHotkey = false;
        }
    }

    private void OnSwatchClick(object sender, RoutedEventArgs e)
    {
        var hex = (string)((System.Windows.Controls.Button)sender).Tag;
        _settings.DefaultAccentColor = hex;
        _settings.Save();
        foreach (var s in _swatches) s.IsSelected = s.Hex == hex;
        SwatchList.ItemsSource = null;
        SwatchList.ItemsSource = _swatches;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}

public class SwatchItem(string hex, bool selected)
{
    public string Hex        { get; } = hex;
    public bool   IsSelected { get; set; } = selected;
    public SolidColorBrush Brush { get; } =
        new((WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(hex));
}
