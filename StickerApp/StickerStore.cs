using System.IO;
using System.Text.Json;

namespace StickerApp;

internal static class StickerStore
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "StickerApp", "stickers");

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static void Init() => Directory.CreateDirectory(Dir);

    public static IEnumerable<StickerData> LoadAll()
    {
        foreach (var file in Directory.GetFiles(Dir, "*.json"))
        {
            StickerData? data = null;
            try { data = JsonSerializer.Deserialize<StickerData>(File.ReadAllText(file), Opts); }
            catch { continue; }
            yield return data!;
        }
    }

    public static void Save(StickerData data)
    {
        var path = Path.Combine(Dir, $"{data.Id}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(data, Opts));
    }

    public static void Delete(string id)
    {
        var path = Path.Combine(Dir, $"{id}.json");
        if (File.Exists(path)) File.Delete(path);
    }
}
