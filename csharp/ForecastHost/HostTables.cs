using System.Data;
using Microsoft.Data.SqlClient;

namespace ForecastHost;

// Reads and writes the host tables in SQL Server: host.sales, host.forecast and
// host.suggested in the forecasting database, the same tables as on the IBM i host.
public sealed class HostTables(SqlConnection connection)
{
    // The connection from the environment, with the defaults of the study's compose
    // file: MSSQL_HOST (localhost), MSSQL_PORT (1433), MSSQL_USER (sa),
    // MSSQL_PASSWORD (required), MSSQL_DATABASE (forecasting).
    public static string ConnectionString()
    {
        var password = Environment.GetEnvironmentVariable("MSSQL_PASSWORD");
        if (string.IsNullOrEmpty(password))
            throw new InvalidOperationException("set MSSQL_PASSWORD");
        var host = Environment.GetEnvironmentVariable("MSSQL_HOST") ?? "localhost";
        var port = Environment.GetEnvironmentVariable("MSSQL_PORT") ?? "1433";
        return new SqlConnectionStringBuilder
        {
            DataSource = $"{host},{port}",
            InitialCatalog = Environment.GetEnvironmentVariable("MSSQL_DATABASE") ?? "forecasting",
            UserID = Environment.GetEnvironmentVariable("MSSQL_USER") ?? "sa",
            Password = password,
            TrustServerCertificate = true,   // the local container has a self-signed certificate
        }.ConnectionString;
    }

    // The schema and the three tables, created when missing. The columns follow the
    // study's table definition rendered for SQL Server.
    public void EnsureTables()
    {
        Execute("""
            IF SCHEMA_ID('host') IS NULL EXEC('CREATE SCHEMA host');
            IF OBJECT_ID('host.sales') IS NULL CREATE TABLE host.sales (
                id VARCHAR(40) NOT NULL,
                item_id VARCHAR(20) NOT NULL,
                store_id VARCHAR(8) NOT NULL,
                date DATE NOT NULL,
                units INT NOT NULL,
                PRIMARY KEY (id, date));
            IF OBJECT_ID('host.forecast') IS NULL CREATE TABLE host.forecast (
                as_of DATE NOT NULL,
                id VARCHAR(40) NOT NULL,
                item_id VARCHAR(20) NOT NULL,
                store_id VARCHAR(8) NOT NULL,
                method VARCHAR(40) NOT NULL,
                horizon INT NOT NULL,
                target_date DATE NOT NULL,
                forecast FLOAT NOT NULL,
                fallback BIT NOT NULL,
                code_digest VARCHAR(64) NOT NULL,
                made_at DATETIME2(0) NOT NULL,
                PRIMARY KEY (as_of, id, method, target_date));
            IF OBJECT_ID('host.suggested') IS NULL CREATE TABLE host.suggested (
                as_of DATE NOT NULL,
                id VARCHAR(40) NOT NULL,
                item_id VARCHAR(20) NOT NULL,
                store_id VARCHAR(8) NOT NULL,
                horizon INT NOT NULL,
                target_date DATE NOT NULL,
                forecast FLOAT NOT NULL,
                source VARCHAR(40) NOT NULL,
                PRIMARY KEY (as_of, id, target_date));
            """);
    }

    // Every series' sales, grouped by series, oldest day first.
    public List<List<Sale>> ReadSales()
    {
        using var cmd = new SqlCommand(
            "SELECT id, item_id, store_id, date, units FROM host.sales ORDER BY id, date",
            connection);
        using var reader = cmd.ExecuteReader();
        var series = new List<List<Sale>>();
        while (reader.Read())
        {
            var sale = new Sale(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                DateOnly.FromDateTime(reader.GetDateTime(3)), reader.GetInt32(4));
            if (series.Count == 0 || series[^1][0].Id != sale.Id)
                series.Add([]);
            series[^1].Add(sale);
        }
        return series;
    }

    public List<ForecastRow> ReadForecasts(string method, DateOnly first, DateOnly last)
    {
        using var cmd = new SqlCommand("""
            SELECT as_of, id, item_id, store_id, method, horizon, target_date, forecast,
                   fallback, code_digest, made_at
              FROM host.forecast
             WHERE method = @method AND as_of BETWEEN @first AND @last
            """, connection);
        cmd.Parameters.Add("@method", SqlDbType.VarChar, 40).Value = method;
        AddRange(cmd, first, last);
        using var reader = cmd.ExecuteReader();
        var rows = new List<ForecastRow>();
        while (reader.Read())
        {
            rows.Add(new ForecastRow(DateOnly.FromDateTime(reader.GetDateTime(0)),
                reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetInt32(5),
                DateOnly.FromDateTime(reader.GetDateTime(6)), reader.GetDouble(7),
                reader.GetBoolean(8), reader.GetString(9), reader.GetDateTime(10)));
        }
        return rows;
    }

    // Replace one method's forecast rows for the origin range.
    public void ReplaceForecasts(
        string method, DateOnly first, DateOnly last, IEnumerable<ForecastRow> rows,
        SqlTransaction tx)
    {
        using (var cmd = new SqlCommand(
            "DELETE FROM host.forecast WHERE method = @method AND as_of BETWEEN @first AND @last",
            connection, tx))
        {
            cmd.Parameters.Add("@method", SqlDbType.VarChar, 40).Value = method;
            AddRange(cmd, first, last);
            cmd.ExecuteNonQuery();
        }

        var table = new DataTable();
        foreach (var (name, type) in new[]
        {
            ("as_of", typeof(DateTime)), ("id", typeof(string)), ("item_id", typeof(string)),
            ("store_id", typeof(string)), ("method", typeof(string)), ("horizon", typeof(int)),
            ("target_date", typeof(DateTime)), ("forecast", typeof(double)),
            ("fallback", typeof(bool)), ("code_digest", typeof(string)),
            ("made_at", typeof(DateTime)),
        })
            table.Columns.Add(name, type);
        foreach (var r in rows)
        {
            table.Rows.Add(Day(r.AsOf), r.Id, r.ItemId, r.StoreId, r.Method, r.Horizon,
                Day(r.TargetDate), r.Forecast, r.Fallback, r.CodeDigest, r.MadeAt);
        }
        BulkInsert("host.forecast", table, tx);
    }

    // Replace the suggested rows for the origin range.
    public void ReplaceSuggested(
        DateOnly first, DateOnly last, IEnumerable<SuggestedRow> rows, SqlTransaction tx)
    {
        using (var cmd = new SqlCommand(
            "DELETE FROM host.suggested WHERE as_of BETWEEN @first AND @last", connection, tx))
        {
            AddRange(cmd, first, last);
            cmd.ExecuteNonQuery();
        }

        var table = new DataTable();
        foreach (var (name, type) in new[]
        {
            ("as_of", typeof(DateTime)), ("id", typeof(string)), ("item_id", typeof(string)),
            ("store_id", typeof(string)), ("horizon", typeof(int)),
            ("target_date", typeof(DateTime)), ("forecast", typeof(double)),
            ("source", typeof(string)),
        })
            table.Columns.Add(name, type);
        foreach (var r in rows)
        {
            table.Rows.Add(Day(r.AsOf), r.Id, r.ItemId, r.StoreId, r.Horizon,
                Day(r.TargetDate), r.Forecast, r.Source);
        }
        BulkInsert("host.suggested", table, tx);
    }

    private void BulkInsert(string tableName, DataTable table, SqlTransaction tx)
    {
        using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.CheckConstraints, tx)
        {
            DestinationTableName = tableName,
            BatchSize = 10_000,
        };
        foreach (DataColumn c in table.Columns)
            bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);
        bulk.WriteToServer(table);
    }

    private void Execute(string sql)
    {
        using var cmd = new SqlCommand(sql, connection);
        cmd.ExecuteNonQuery();
    }

    private static void AddRange(SqlCommand cmd, DateOnly first, DateOnly last)
    {
        cmd.Parameters.Add("@first", SqlDbType.Date).Value = Day(first);
        cmd.Parameters.Add("@last", SqlDbType.Date).Value = Day(last);
    }

    private static DateTime Day(DateOnly d) => d.ToDateTime(TimeOnly.MinValue);
}
