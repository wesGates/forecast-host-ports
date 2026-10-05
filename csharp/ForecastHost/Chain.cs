using Microsoft.Data.SqlClient;

namespace ForecastHost;

// What FCSTNAIVE does on the IBM i host, on SQL Server: the fallback forecast into
// host.forecast, then the suggested rows into host.suggested, in one transaction.
public static class Chain
{
    public const string CodeDigest = "ForecastHost 1.0";

    public sealed record Result(int FallbackRows, int SuggestedRows, int FromModel);

    public static Result Run(SqlConnection connection, DateOnly first, DateOnly last, string model)
    {
        var tables = new HostTables(connection);
        tables.EnsureTables();

        var madeAt = DateTime.UtcNow;
        madeAt = madeAt.AddTicks(-(madeAt.Ticks % TimeSpan.TicksPerSecond));   // DATETIME2(0)
        var fallback = tables.ReadSales()
            .SelectMany(sales => Rule.SeasonalNaive(sales, first, last, CodeDigest, madeAt))
            .ToList();
        var suggested = Rule.Suggest(fallback, tables.ReadForecasts(model, first, last));

        using (var tx = connection.BeginTransaction())
        {
            tables.ReplaceForecasts(Rule.Fallback, first, last, fallback, tx);
            tables.ReplaceSuggested(first, last, suggested, tx);
            tx.Commit();
        }
        return new Result(fallback.Count, suggested.Count, suggested.Count(r => r.Source == model));
    }
}
