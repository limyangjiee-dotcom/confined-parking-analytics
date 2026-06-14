using System.Data.Common;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using ParkingApiPg.Models;
namespace ParkingApiPg.Services;

// Engine-specific bits for connecting to an external parking database.
// PostgreSQL, MySQL and SQL Server share the same downstream flow — only the
// connection string, identifier quoting and schema-discovery query differ.
public static class ConnectorDb
{
    public static readonly string[] Engines = { "postgresql", "mysql", "sqlserver" };

    public static DbConnection Create(SourceConn c) => c.Engine switch
    {
        "mysql" => new MySqlConnection(
            $"Server={c.Host};Port={c.Port};Database={c.Database};Uid={c.Username};Pwd={c.Password};" +
            "Connection Timeout=8;Default Command Timeout=30"),
        "sqlserver" => new SqlConnection(
            $"Server={c.Host},{c.Port};Database={c.Database};User Id={c.Username};Password={c.Password};" +
            "TrustServerCertificate=true;Encrypt=false;Connect Timeout=8"),
        _ => new NpgsqlConnection(ConnectorSupport.ExtCs(c))
    };

    // quote an identifier for the engine
    public static string Q(string engine, string id) => engine switch
    {
        "mysql" => $"`{id}`",
        "sqlserver" => $"[{id}]",
        _ => $"\"{id}\""
    };

    public static string VersionSql(string engine) => engine == "sqlserver" ? "SELECT @@VERSION" : "SELECT version()";

    public static string DiscoverSql(string engine) => engine switch
    {
        "mysql" => "SELECT table_name, column_name, data_type FROM information_schema.columns " +
                   "WHERE table_schema = DATABASE() ORDER BY table_name, ordinal_position",
        "sqlserver" => "SELECT TABLE_NAME, COLUMN_NAME, DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS " +
                       "ORDER BY TABLE_NAME, ORDINAL_POSITION",
        _ => "SELECT table_name, column_name, data_type FROM information_schema.columns " +
             "WHERE table_schema='public' ORDER BY table_name, ordinal_position"
    };

    public static int DefaultPort(string engine) => engine switch
    {
        "mysql" => 3306, "sqlserver" => 1433, _ => 5432
    };
}
