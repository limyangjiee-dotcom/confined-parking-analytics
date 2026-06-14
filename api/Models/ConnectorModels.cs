using System.Text.RegularExpressions;
namespace ParkingApiPg.Models;

// Field mapping: for a DB source these are column names; for an API source
// these are JSON property names. SourceTable is only used by DB sources.
public record Mapping(string SourceTable = "", string Plate = "", string EntryTime = "",
                      string ExitTime = "", string Fee = "", string Level = "", string VehicleType = "");

// Direct-database source (PostgreSQL / MySQL / SQL Server).
public record SourceConn(string Engine = "postgresql", string Host = "localhost", int Port = 5432,
                         string Database = "", string Username = "postgres", string Password = "");

// REST-API source: the parking system exposes an endpoint returning JSON sessions.
public record ApiSource(string Url = "", string Method = "GET", string AuthHeader = "",
                        string AuthValue = "", string RecordsPath = "");

// SourceType: "database" or "api". Mapping is shared (column names or JSON keys).
public record DsConfig(SourceConn Connection, Mapping Mapping,
                       string SourceType = "database", ApiSource? Api = null);

public record SyncResult(bool Ok, int Read, int Matched, int Inserted, string Message);

// one normalized parking session, regardless of source
public record SessionRow(string Plate, DateTime Entry, DateTime? Exit, decimal Fee, string Level, string VehicleType);

public static class ConnectorSupport
{
    public static bool Ident(string s) =>
        Regex.IsMatch(s ?? "", "^[A-Za-z_][A-Za-z0-9_]*$") && s!.Length <= 63;

    // PostgreSQL connection string (other engines build their own in the connector).
    public static string ExtCs(SourceConn c) =>
        $"Host={c.Host};Port={c.Port};Database={c.Database};Username={c.Username};Password={c.Password};" +
        "Timeout=8;Command Timeout=30";
}
