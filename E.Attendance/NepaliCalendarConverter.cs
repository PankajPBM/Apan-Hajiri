using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace E.Attendance;

public readonly record struct BsDate(int Year, int Month, int Day, DayOfWeek DayOfWeek);

public static class NepaliCalendarConverter
{
    private static readonly DateTime EpochAd = new(1943, 4, 13, 0, 0, 0, DateTimeKind.Local);
    private const int MinSupportedDay = 1;

    private static readonly int MinBsYear;
    private static readonly int MaxBsYear;
    private static readonly int MaxSupportedDay;
    private static readonly int[][] YearMonthDays;
    private static readonly int[][] YearDaysMapping;
    private static readonly int[][][] MonthDaysMappings;
    private static readonly Dictionary<int, int> YearIndexMap = [];

    static NepaliCalendarConverter()
    {
        YearMonthDays = LoadYearMonthDays();
        if (YearMonthDays.Length == 0)
        {
            throw new InvalidOperationException("No BS date mappings loaded.");
        }

        MinBsYear = LoadedYears.Min();
        MaxBsYear = LoadedYears.Max();
        YearDaysMapping = new int[YearMonthDays.Length][];
        MonthDaysMappings = new int[YearMonthDays.Length][][];

        var daysPassed = 0;
        for (var yearIndex = 0; yearIndex < YearMonthDays.Length; yearIndex++)
        {
            var year = YearMonthDays[yearIndex];
            var daysInYear = year.Sum();
            YearDaysMapping[yearIndex] = [daysInYear, daysPassed];

            var monthMap = new int[12][];
            var monthPassed = 0;
            for (var month = 0; month < 12; month++)
            {
                monthMap[month] = [year[month], monthPassed];
                monthPassed += year[month];
            }

            MonthDaysMappings[yearIndex] = monthMap;
            daysPassed += daysInYear;
        }
        MaxSupportedDay = daysPassed;
        for (var i = 0; i < LoadedYears.Length; i++)
        {
            YearIndexMap[LoadedYears[i]] = i;
        }

        if (!YearIndexMap.ContainsKey(2000))
        {
            throw new InvalidOperationException("BS date map must include year 2000.");
        }
    }

    private static int[] LoadedYears { get; set; } = [];

    public static int SupportedMinBsYear => MinBsYear;
    public static int SupportedMaxBsYear => MaxBsYear;
    public static DateTime SupportedMinAdDate => EpochAd.Date.AddDays(MinSupportedDay);
    public static DateTime SupportedMaxAdDate => EpochAd.Date.AddDays(MaxSupportedDay);

    public static BsDate ConvertAdToBs(DateTime adDate)
    {
        var localDate = adDate.Date;
        var daysPassed = (localDate - EpochAd.Date).Days;
        if (daysPassed < MinSupportedDay || daysPassed > MaxSupportedDay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(adDate),
                $"Supported AD range is {SupportedMinAdDate:yyyy-MM-dd} to {SupportedMaxAdDate:yyyy-MM-dd}.");
        }

        var (year, month, day) = MapDaysToBs(daysPassed);
        return new BsDate(year, month + 1, day, localDate.DayOfWeek);
    }

    public static DateTime ConvertBsToAd(int bsYear, int bsMonth, int bsDay)
    {
        var monthIndex = bsMonth - 1;
        var daysPassed = FindPassedDays(bsYear, monthIndex, bsDay);
        return EpochAd.Date.AddDays(daysPassed);
    }

    public static int GetDaysInMonth(int bsYear, int bsMonth)
    {
        if (!YearIndexMap.TryGetValue(bsYear, out var yearIndex) || bsMonth < 1 || bsMonth > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(bsYear), "BS date is out of supported range.");
        }

        return YearMonthDays[yearIndex][bsMonth - 1];
    }

    private static int FindPassedDays(int year, int month, int date)
    {
        try
        {
            if (!YearIndexMap.TryGetValue(year, out var yearIndex))
            {
                throw new ArgumentOutOfRangeException(nameof(year));
            }

            var pastYearDays = YearDaysMapping[yearIndex][1];
            var extraMonth = Mod(12, month);
            var extraYear = (int)Math.Floor(month / 12d);
            var resolvedYearIndex = yearIndex + extraYear;
            if (resolvedYearIndex < 0 || resolvedYearIndex >= YearDaysMapping.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(year));
            }

            var pastMonthDays =
                YearDaysMapping[resolvedYearIndex][1] -
                pastYearDays +
                MonthDaysMappings[resolvedYearIndex][extraMonth][1];

            var daysPassed = pastYearDays + pastMonthDays + date;
            if (daysPassed < MinSupportedDay || daysPassed > MaxSupportedDay)
            {
                throw new ArgumentOutOfRangeException(nameof(date));
            }

            return daysPassed;
        }
        catch
        {
            throw new ArgumentOutOfRangeException(nameof(year), $"Supported BS range is {MinBsYear}/01/01 to {MaxBsYear}/12/30.");
        }
    }

    private static (int Year, int MonthIndex, int Day) MapDaysToBs(int daysPassed)
    {
        if (daysPassed < MinSupportedDay || daysPassed > MaxSupportedDay)
        {
            throw new ArgumentOutOfRangeException(nameof(daysPassed));
        }

        var yearIndex = Array.FindIndex(
            YearDaysMapping,
            y => daysPassed > y[1] && daysPassed <= y[1] + y[0]);
        if (yearIndex < 0)
        {
            throw new InvalidOperationException("Unable to map day to BS year.");
        }

        var monthRemainder = daysPassed - YearDaysMapping[yearIndex][1];
        var monthIndex = Array.FindIndex(
            MonthDaysMappings[yearIndex],
            m => monthRemainder > m[1] && monthRemainder <= m[1] + m[0]);
        if (monthIndex < 0)
        {
            throw new InvalidOperationException("Unable to map day to BS month.");
        }

        var day = monthRemainder - MonthDaysMappings[yearIndex][monthIndex][1];
        return (LoadedYears[yearIndex], monthIndex, day);
    }

    private static int Mod(int m, int value)
    {
        while (value < 0)
        {
            value += m;
        }

        return value % m;
    }

    private static int[][] LoadYearMonthDays()
    {
        var external = TryLoadExternalMap();
        if (external is not null)
        {
            return external;
        }

        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(x => x.EndsWith("NepaliDateConfig.ts.txt", StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
        {
            throw new InvalidOperationException("Missing embedded NepaliDateConfig.ts.txt resource.");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Unable to load Nepali date config resource stream.");
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();

        var yearBlockRegex = new Regex("'(?<year>\\d{4})'\\s*:\\s*\\{(?<body>.*?)\\}", RegexOptions.Singleline);
        var dayRegex = new Regex(":\\s*(?<day>\\d+),");
        var entries = new SortedDictionary<int, int[]>();

        foreach (Match yearMatch in yearBlockRegex.Matches(text))
        {
            if (!int.TryParse(yearMatch.Groups["year"].Value, out var year))
            {
                continue;
            }

            var monthDays = dayRegex.Matches(yearMatch.Groups["body"].Value)
                .Cast<Match>()
                .Select(x => int.Parse(x.Groups["day"].Value))
                .ToArray();

            if (monthDays.Length == 12)
            {
                entries[year] = monthDays;
            }
        }

        if (entries.Count < 50 || !entries.ContainsKey(2000))
        {
            throw new InvalidOperationException("Failed to parse Nepali date configuration.");
        }

        LoadedYears = entries.Keys.ToArray();
        return entries.Values.ToArray();
    }

    private static int[][]? TryLoadExternalMap()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "bs-calendar-map.json"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PBAttendance",
                "bs-calendar-map.json")
        };

        foreach (var path in candidates)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var json = File.ReadAllText(path);
                var data = JsonSerializer.Deserialize<Dictionary<string, int[]>>(json);
                if (data is null || data.Count == 0)
                {
                    continue;
                }

                var entries = new SortedDictionary<int, int[]>();
                foreach (var (k, value) in data)
                {
                    if (!int.TryParse(k, out var year) || value.Length != 12)
                    {
                        continue;
                    }

                    if (value.Any(x => x < 28 || x > 32))
                    {
                        continue;
                    }

                    entries[year] = value;
                }

                if (entries.Count > 0 && entries.ContainsKey(2000))
                {
                    LoadedYears = entries.Keys.ToArray();
                    return entries.Values.ToArray();
                }
            }
            catch
            {
                // Fallback to embedded map.
            }
        }

        return null;
    }
}
