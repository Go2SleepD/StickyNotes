using System.IO;
using System.Text.Json;

namespace StickerApp;

public class AppSettings
{
    public string            DefaultAccentColor { get; set; } = "#5E6AD2";
    public int               TotalAbsorbed      { get; set; }
    public List<StickerData> ArchivedStickers   { get; set; } = [];
    public bool              RubberBallEnabled  { get; set; } = true;

    // Full hotkey combos: any combination of MB3/MB4/MB5 + Ctrl/Shift/Alt
    public string HotkeyCreate    { get; set; } = "MB5+Ctrl+Shift";
    public string HotkeyComplete  { get; set; } = "MB5";
    public string HotkeyClearDesk { get; set; } = "MB3+Ctrl+Shift+Alt";

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "StickerApp", "settings.json");

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Opts)!; }
        catch { return new AppSettings(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opts));
    }

    public static (bool Mb2, bool Mb3, bool Mb4, bool Mb5, bool Ctrl, bool Shift, bool Alt) ParseHotkey(string combo) => (
        combo.Contains("MB2"),
        combo.Contains("MB3"),
        combo.Contains("MB4"),
        combo.Contains("MB5"),
        combo.Contains("Ctrl"),
        combo.Contains("Shift"),
        combo.Contains("Alt")
    );

}
