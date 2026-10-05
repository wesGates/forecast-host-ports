using System.Data;
using ForecastHost;
using Microsoft.Data.SqlClient;

namespace ForecastHost.Api;

public sealed record SeriesInfo(string Id, string ItemId, string StoreId);

public sealed record SalesDay(DateOnly Date, int Units);

// source is "model" when the row came from a model's forecast and "fallback" when it
// came from the host's own SEASONAL_NAIVE row; method names which.
public sealed record SuggestedDay(
    int Horizon, DateOnly TargetDate, double Forecast, string Source, string Method);

public sealed record Suggested(string Id, DateOnly AsOf, List<SuggestedDay> Days);

// The API's reads from the host tables. Each call opens its own connection.
public sealed class Queries(string connectionString)
{
    public List<SeriesInfo> Series() =>
        Read("SELECT DISTINCT id, item_id, store_id FROM host.sales ORDER BY id", _ => { },
            r => new SeriesInfo(r.GetString(0), r.GetString(1), r.GetString(2)));

    public bool SeriesExists(string id) =>
        Read("SELECT TOP 1 1 FROM host.sales WHERE id = @id", c => AddId(c, id), _ => 1).Count > 0;

    public List<SalesDay> Sales(string id, DateOnly? from, DateOnly? to) =>
        Read("""
            SELECT date, units FROM host.sales
             WHERE id = @id
               AND (@from IS NULL OR date >= @from) AND (@to IS NULL OR date <= @to)
             ORDER BY date
            """,
            c =>
            {
                AddId(c, id);
                AddDate(c, "@from", from);
                AddDate(c, "@to", to);
            },
            r => new SalesDay(DateOnly.FromDateTime(r.GetDateTime(0)), r.GetInt32(1)));

    // The suggested rows for one origin; the latest origin when asOf is null.
    public Suggested? Suggested(string id, DateOnly? asOf)
    {
        var origin = asOf ?? Read("SELECT MAX(as_of) FROM host.suggested WHERE id = @id",
            c => AddId(c, id),
            r => r.IsDBNull(0) ? (DateOnly?)null : DateOnly.FromDateTime(r.GetDateTime(0)))[0];
        if (origin is null)
            return null;

        var days = Read("""
            SELECT horizon, target_date, forecast, source FROM host.suggested
             WHERE id = @id AND as_of = @asOf
             ORDER BY horizon
            """,
            c =>
            {
                AddId(c, id);
                AddDate(c, "@asOf", origin);
            },
            r => new SuggestedDay(r.GetInt32(0), DateOnly.FromDateTime(r.GetDateTime(1)),
                r.GetDouble(2), r.GetString(3) == Rule.Fallback ? "fallback" : "model",
                r.GetString(3)));
        return days.Count == 0 ? null : new Suggested(id, origin.Value, days);
    }

    private List<T> Read<T>(string sql, Action<SqlCommand> bind, Func<SqlDataReader, T> row)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var cmd = new SqlCommand(sql, connection);
        bind(cmd);
        using var reader = cmd.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
            rows.Add(row(reader));
        return rows;
    }

    private static void AddId(SqlCommand c, string id) =>
        c.Parameters.Add("@id", SqlDbType.VarChar, 40).Value = id;

    private static void AddDate(SqlCommand c, string name, DateOnly? d) =>
        c.Parameters.Add(name, SqlDbType.Date).Value =
            d is null ? DBNull.Value : d.Value.ToDateTime(TimeOnly.MinValue);
}
