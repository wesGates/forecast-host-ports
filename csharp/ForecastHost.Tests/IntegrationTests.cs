using ForecastHost;
using Microsoft.Data.SqlClient;

namespace ForecastHost.Tests;

// The port against a real SQL Server with synthetic rows, in its own database
// (forecast_host_test), so the forecasting database is never touched. Needs
// MSSQL_PASSWORD and a reachable server; skips otherwise.
public class IntegrationTests
{
    private const string Database = "forecast_host_test";
    private const string Model = "test_model";
    private static readonly DateOnly Day1 = new(2020, 1, 1);
    private static readonly DateOnly First = Day1.AddDays(9);    // 2020-01-10
    private static readonly DateOnly Last = Day1.AddDays(19);    // 2020-01-20
    private const int Days = 40;
    private static readonly string[] Ids = ["A", "B"];

    // The synthetic sales: different for each series and day.
    private static int Units(int series, int day) => (day * 3 + series * 5) % 11;

    private static SqlConnection Open()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MSSQL_PASSWORD")),
            "MSSQL_PASSWORD is not set");
        using (var master = new SqlConnection(HostTables.ConnectionString("master")))
        {
            master.Open();
            using var create = new SqlCommand(
                $"IF DB_ID('{Database}') IS NULL CREATE DATABASE [{Database}]", master);
            create.ExecuteNonQuery();
        }
        var connection = new SqlConnection(HostTables.ConnectionString(Database));
        connection.Open();
        return connection;
    }

    private static void Execute(SqlConnection c, string sql, params (string, object)[] parameters)
    {
        using var cmd = new SqlCommand(sql, c);
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    // Empty tables, sales for two series, and model rows for series A only.
    private static void Seed(SqlConnection c)
    {
        new HostTables(c).EnsureTables();
        Execute(c, "DELETE FROM host.suggested; DELETE FROM host.forecast; DELETE FROM host.sales;");
        for (var s = 0; s < Ids.Length; s++)
        {
            for (var d = 0; d < Days; d++)
            {
                Execute(c, "INSERT INTO host.sales VALUES (@id, 'ITEM', @store, @date, @units)",
                    ("@id", Ids[s]), ("@store", $"S{s}"),
                    ("@date", Day1.AddDays(d).ToDateTime(TimeOnly.MinValue)), ("@units", Units(s, d)));
            }
        }
        for (var asOf = First; asOf <= Last; asOf = asOf.AddDays(1))
        {
            for (var h = 1; h <= 7; h++)
            {
                Execute(c, """
                    INSERT INTO host.forecast
                    VALUES (@asOf, 'A', 'ITEM', 'S0', @model, @h, @target, @f, 0, 'test', SYSUTCDATETIME())
                    """,
                    ("@asOf", asOf.ToDateTime(TimeOnly.MinValue)), ("@model", Model), ("@h", h),
                    ("@target", asOf.AddDays(h).ToDateTime(TimeOnly.MinValue)), ("@f", 100.0 + h));
            }
        }
    }

    private static List<(DateOnly AsOf, string Id, int Horizon, DateOnly Target, double Forecast, string Source)>
        ReadSuggested(SqlConnection c)
    {
        using var cmd = new SqlCommand(
            "SELECT as_of, id, horizon, target_date, forecast, source FROM host.suggested", c);
        using var r = cmd.ExecuteReader();
        var rows = new List<(DateOnly, string, int, DateOnly, double, string)>();
        while (r.Read())
        {
            rows.Add((DateOnly.FromDateTime(r.GetDateTime(0)), r.GetString(1), r.GetInt32(2),
                DateOnly.FromDateTime(r.GetDateTime(3)), r.GetDouble(4), r.GetString(5)));
        }
        return rows;
    }

    private static int Count(SqlConnection c, string sql)
    {
        using var cmd = new SqlCommand(sql, c);
        return (int)cmd.ExecuteScalar();
    }

    [Fact]
    public void WritesTheFallbackAndPrefersTheModel()
    {
        using var c = Open();
        Seed(c);

        var result = Chain.Run(c, First, Last, Model);

        const int expected = 2 * 11 * 7;   // two series, eleven origins, seven days
        Assert.Equal(new Chain.Result(expected, expected, expected / 2), result);
        Assert.Equal(expected,
            Count(c, "SELECT COUNT(*) FROM host.forecast WHERE method = 'SEASONAL_NAIVE'"));

        var rows = ReadSuggested(c);
        Assert.Equal(expected, rows.Count);
        Assert.All(rows.Where(r => r.Id == "A"), r =>
        {
            Assert.Equal(Model, r.Source);
            Assert.Equal(100.0 + r.Horizon, r.Forecast);
        });
        Assert.All(rows.Where(r => r.Id == "B"), r =>
        {
            Assert.Equal(Rule.Fallback, r.Source);
            var lastWeek = r.Target.AddDays(-7).DayNumber - Day1.DayNumber;
            Assert.Equal(Units(1, lastWeek), r.Forecast);
        });
    }

    [Fact]
    public void ARerunReplacesTheRows()
    {
        using var c = Open();
        Seed(c);

        Chain.Run(c, First, Last, Model);
        Chain.Run(c, First, Last, Model);

        // the fallback rows once, plus the model rows the seed wrote
        Assert.Equal(2 * 11 * 7 + 11 * 7, Count(c, "SELECT COUNT(*) FROM host.forecast"));
        Assert.Equal(2 * 11 * 7, Count(c, "SELECT COUNT(*) FROM host.suggested"));
    }
}
