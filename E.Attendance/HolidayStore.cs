using System.Text.Json;

namespace E.Attendance;

public sealed class HolidayItem
{
    public string Name { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public bool IsActive { get; set; } = true;
}

public static class HolidayStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static List<HolidayItem> Load()
    {
        var path = GetStoragePath();
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(path);
            var items = JsonSerializer.Deserialize<List<HolidayItem>>(json, JsonOptions);
            return items ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static void Save(IEnumerable<HolidayItem> items)
    {
        var path = GetStoragePath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var ordered = items
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .OrderBy(x => x.Date)
            .ToList();
        var json = JsonSerializer.Serialize(ordered, JsonOptions);
        File.WriteAllText(path, json);
    }

    public static Dictionary<DateOnly, string> LoadActiveMap()
    {
        var result = new Dictionary<DateOnly, string>();
        foreach (var item in Load().Where(x => x.IsActive))
        {
            if (!result.ContainsKey(item.Date))
            {
                result[item.Date] = item.Name;
            }
        }

        return result;
    }

    private static string GetStoragePath()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "E_Attendance");
        return Path.Combine(root, "holidays.json");
    }
}

