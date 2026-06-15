using System.Collections.Concurrent;
using Npgsql;
namespace ParkingApiPg.Services;

// Platform settings the operator configures on THEIR side (no writes to the
// external parking system): analytics capacity and how often the connector
// auto-syncs. Cached in memory, refreshed on save.
public class SettingsService
{
    private readonly string _cs;
    private readonly ConcurrentDictionary<string, string> _s = new();

    public static readonly Dictionary<string, string> Defaults = new()
    {
        ["capacity"] = "11000",
        ["sync_interval_seconds"] = "0",       // 0 = manual only
    };

    public SettingsService(IConfiguration cfg)
    {
        _cs = cfg.GetConnectionString("Default")!;
        EnsureSeed();
        Reload();
    }

    private void EnsureSeed()
    {
        using var c = new NpgsqlConnection(_cs);
        c.Open();
        foreach (var (k, v) in Defaults)
            new NpgsqlCommand(@"INSERT INTO ""App_Settings"" (""Key"",""Value"") VALUES (@k,@v)
                                ON CONFLICT (""Key"") DO NOTHING", c)
                { Parameters = { new("k", k), new("v", v) } }.ExecuteNonQuery();
    }

    public void Reload()
    {
        using var c = new NpgsqlConnection(_cs);
        c.Open();
        using var rd = new NpgsqlCommand(@"SELECT ""Key"",""Value"" FROM ""App_Settings""", c).ExecuteReader();
        while (rd.Read()) _s[rd.GetString(0)] = rd.GetString(1);
    }

    public void Save(Dictionary<string, string> incoming)
    {
        using var c = new NpgsqlConnection(_cs);
        c.Open();
        foreach (var (k, v) in incoming)
            if (Defaults.ContainsKey(k))
                new NpgsqlCommand(@"INSERT INTO ""App_Settings"" (""Key"",""Value"") VALUES (@k,@v)
                                    ON CONFLICT (""Key"") DO UPDATE SET ""Value""=@v", c)
                    { Parameters = { new("k", k), new("v", v) } }.ExecuteNonQuery();
        Reload();
    }

    private string Get(string k) => _s.TryGetValue(k, out var v) ? v : Defaults.GetValueOrDefault(k, "");
    private int GetInt(string k) => int.TryParse(Get(k), out var v) ? v : int.Parse(Defaults[k]);

    public int Capacity => Math.Max(1, GetInt("capacity"));
    public int SyncIntervalSeconds => Math.Max(0, GetInt("sync_interval_seconds"));
    public Dictionary<string, string> All() => Defaults.Keys.ToDictionary(k => k, Get);
}
