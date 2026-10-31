using System.Globalization;
using System.IO;
using System.Text.Json;

namespace AgentOrchestrator;

// Lifetime usage for the profile: kept in its own file, apart from conversations, so deleting a chat never lowers it.
// Only Settings' "Reset usage stats" clears it.
internal sealed class UsageStats
{
    public long Lifetime { get; set; }
    public Dictionary<string, long> Daily { get; set; } = new(); // local day "yyyy-MM-dd" -> tokens (prompt + generated)
    public double LongestTaskSeconds { get; set; } // the longest chat turn or project run

    private static string FilePath => Path.Combine(AppSettings.DataRoot, "stats.json");

    public static UsageStats Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<UsageStats>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataRoot);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static string Key(DateTime day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public void Add(long tokens, DateTime now)
    {
        if (tokens <= 0)
            return;
        Lifetime += tokens;
        Daily[Key(now)] = Daily.GetValueOrDefault(Key(now)) + tokens;
    }

    public void AddTask(TimeSpan elapsed) => LongestTaskSeconds = Math.Max(LongestTaskSeconds, elapsed.TotalSeconds);

    public long Peak => Daily.Values.DefaultIfEmpty(0).Max();

    public int LongestStreak
    {
        get
        {
            int best = 0, run = 0;
            DateTime? last = null;
            foreach (var day in ActiveDays())
            {
                run = last is { } l && (day - l).Days == 1 ? run + 1 : 1;
                best = Math.Max(best, run);
                last = day;
            }
            return best;
        }
    }

    // Days in a row with use, ending today (or yesterday, while today has none yet).
    public int CurrentStreak(DateTime today)
    {
        var day = today.Date;
        if (Daily.GetValueOrDefault(Key(day)) <= 0)
            day = day.AddDays(-1);
        var n = 0;
        while (Daily.GetValueOrDefault(Key(day)) > 0)
        {
            n++;
            day = day.AddDays(-1);
        }
        return n;
    }

    private IEnumerable<DateTime> ActiveDays() => Daily.Where(d => d.Value > 0)
        .Select(d => DateTime.TryParseExact(d.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : (DateTime?)null)
        .OfType<DateTime>().OrderBy(d => d);

    // 5.7B, 419M, 12.3K, 950
    public static string Short(long n) => n switch
    {
        >= 1_000_000_000 => (n / 1e9).ToString("0.#", CultureInfo.InvariantCulture) + "B",
        >= 1_000_000 => (n / 1e6).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (n / 1e3).ToString("0.#", CultureInfo.InvariantCulture) + "K",
        _ => n.ToString(CultureInfo.InvariantCulture),
    };

    // 10h 21m, 4m 05s, 12s
    public static string Duration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Round(seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds:00}s" : $"{t.Seconds}s";
    }

    internal static void SelfTest()
    {
        var s = new UsageStats();
        var d = new DateTime(2026, 3, 10, 12, 0, 0);
        s.Add(100, d);
        s.Add(50, d);
        s.Add(500, d.AddDays(1));
        s.Add(10, d.AddDays(3));
        s.Add(0, d.AddDays(4)); // nothing used: not an active day
        s.AddTask(TimeSpan.FromMinutes(3));
        s.AddTask(TimeSpan.FromMinutes(1));
        if (s.Lifetime != 660 || s.Peak != 500 || s.LongestStreak != 2 || s.CurrentStreak(d.AddDays(3)) != 1 || s.CurrentStreak(d.AddDays(4)) != 1
            || s.CurrentStreak(d.AddDays(5)) != 0 || s.CurrentStreak(d.AddDays(1)) != 2 || s.LongestTaskSeconds != 180)
            throw new InvalidOperationException("UsageStats totals");
        if (Short(5_700_000_000) != "5.7B" || Short(419_000_000) != "419M" || Short(950) != "950" || Duration(37260) != "10h 21m" || Duration(245) != "4m 05s")
            throw new InvalidOperationException("UsageStats formatting");
    }
}
