using Microsoft.Data.SqlClient;

namespace ForecastHost;

// Synthetic rows for running the port where the M5 data is not available, such as
// the compose setup. Three stores sell one item for 90 days. The model's rows stop
// after 2024-03-15, and store DEMO_3 never has any, so the suggested rows show both
// sources. Everything goes into its own database, never the forecasting database.
public static class Demo
{
    public const string Database = "forecast_host_demo";
    public const string Model = "demo_model";
    public static readonly DateOnly First = new(2024, 1, 7);
    public static readonly DateOnly Last = new(2024, 3, 30);
    private static readonly DateOnly Day1 = new(2024, 1, 1);
    private static readonly DateOnly ModelLast = new(2024, 3, 15);
    private const int Days = 90;
    private static readonly int[] Weekday = [0, 2, 4, 6, 8, 10, 3];

    // Create the database if needed and replace its rows with the demo's.
    public static void Seed()
    {
        using (var master = new SqlConnection(HostTables.ConnectionString("master")))
        {
            master.Open();
            using var create = new SqlCommand(
                $"IF DB_ID('{Database}') IS NULL CREATE DATABASE [{Database}]", master);
            create.ExecuteNonQuery();
        }

        using var connection = new SqlConnection(HostTables.ConnectionString(Database));
        connection.Open();
        var tables = new HostTables(connection);
        tables.EnsureTables();

        var sales = new List<Sale>();
        for (var s = 1; s <= 3; s++)
        {
            for (var d = 0; d < Days; d++)
            {
                var units = 20 + 5 * s + Weekday[d % 7] + d / 30;
                sales.Add(new Sale($"DEMO_ITEM_DEMO_{s}", "DEMO_ITEM", $"DEMO_{s}", Day1.AddDays(d), units));
            }
        }

        // The model forecasts a tenth above this day last week, for stores 1 and 2 only.
        var model = sales
            .Where(x => x.StoreId != "DEMO_3")
            .GroupBy(x => x.Id)
            .SelectMany(g => Rule.SeasonalNaive(g.ToList(), First, ModelLast, "demo", DateTime.UtcNow))
            .Select(r => r with { Method = Model, Forecast = Math.Round(r.Forecast * 1.1, 2) });

        using var tx = connection.BeginTransaction();
        using (var clear = new SqlCommand("DELETE FROM host.suggested; DELETE FROM host.forecast;", connection, tx))
            clear.ExecuteNonQuery();
        tables.ReplaceSales(sales, tx);
        tables.ReplaceForecasts(Model, First, Last, model, tx);
        tx.Commit();
    }
}
