using System.Text.Json;

namespace EdifierManager;

public static class KnownHeadsets
{
    private static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdifierManager", "known-headsets.json");
    public static Dictionary<string, string> Load(string? path = null)
    {
        try
        {
            string file = path ?? DefaultPath;
            if (!File.Exists(file) || new FileInfo(file).Length > 64000) return [];
            var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? [];
            return entries.Where(x => x.Key.Length == 20 && x.Key.All(Uri.IsHexDigit) && x.Value is "3801" or "7901" or "8501" or "1001" or "4201").ToDictionary();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }
    public static void Remember(ulong address, HeadsetModel model, string? path = null)
    {
        string file = path ?? DefaultPath; var entries = Load(file); entries[HeadsetController.MakeDeviceKey(address)] = model.Code;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(entries, HeadsetController.JsonOptions)); File.Move(temporary, file, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
