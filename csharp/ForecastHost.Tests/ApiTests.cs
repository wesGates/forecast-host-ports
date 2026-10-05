using System.Net;
using System.Text.Json;
using ForecastHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;

namespace ForecastHost.Tests;

// One test per API endpoint, against the demo database (Demo.cs) after the port has
// run on it. Needs MSSQL_PASSWORD and a reachable server; skips otherwise.
public class ApiTests
{
    private static readonly Lazy<bool> Seeded = new(() =>
    {
        Demo.Seed();
        using var c = new SqlConnection(HostTables.ConnectionString(Demo.Database));
        c.Open();
        Chain.Run(c, Demo.First, Demo.Last, Demo.Model);
        return true;
    });

    private static HttpClient Client()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MSSQL_PASSWORD")),
            "MSSQL_PASSWORD is not set");
        _ = Seeded.Value;
        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("MSSQL_DATABASE", Demo.Database));
        return factory.CreateClient();
    }

    private static async Task<JsonElement> GetJson(HttpClient client, string url)
    {
        var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return JsonDocument.Parse(body).RootElement;
    }

    [Fact]
    public async Task SeriesListsEveryItemAtEveryStore()
    {
        using var client = Client();
        var series = await GetJson(client, "/series");

        Assert.Equal(new[] { "DEMO_1", "DEMO_2", "DEMO_3" },
            series.EnumerateArray().Select(s => s.GetProperty("store_id").GetString()));
        Assert.All(series.EnumerateArray(),
            s => Assert.Equal("DEMO_ITEM", s.GetProperty("item_id").GetString()));
    }

    [Fact]
    public async Task SalesReturnsTheUnitsForEachDayInTheRange()
    {
        using var client = Client();
        var days = await GetJson(client, "/series/DEMO_ITEM_DEMO_1/sales?from=2024-01-01&to=2024-01-07");

        Assert.Equal(new[] { "2024-01-01", "2024-01-02", "2024-01-03", "2024-01-04",
                             "2024-01-05", "2024-01-06", "2024-01-07" },
            days.EnumerateArray().Select(d => d.GetProperty("date").GetString()));
        Assert.Equal(new[] { 25, 27, 29, 31, 33, 35, 28 },
            days.EnumerateArray().Select(d => d.GetProperty("units").GetInt32()));

        var missing = await client.GetAsync("/series/NO_SUCH_SERIES/sales",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task SuggestedSaysWhetherEachRowCameFromTheModelOrTheFallback()
    {
        using var client = Client();

        // Store 1 has model rows up to 2024-03-15, store 3 never has any.
        var withModel = await GetJson(client, "/series/DEMO_ITEM_DEMO_1/suggested?as_of=2024-03-01");
        var newStore = await GetJson(client, "/series/DEMO_ITEM_DEMO_3/suggested?as_of=2024-03-01");
        var latest = await GetJson(client, "/series/DEMO_ITEM_DEMO_1/suggested");

        Assert.Equal("2024-03-01", withModel.GetProperty("as_of").GetString());
        Assert.Equal(7, withModel.GetProperty("days").GetArrayLength());
        Assert.All(withModel.GetProperty("days").EnumerateArray(),
            d => Assert.Equal("model", d.GetProperty("source").GetString()));
        Assert.All(newStore.GetProperty("days").EnumerateArray(),
            d => Assert.Equal("fallback", d.GetProperty("source").GetString()));
        Assert.Equal("2024-03-30", latest.GetProperty("as_of").GetString());
        Assert.All(latest.GetProperty("days").EnumerateArray(),
            d => Assert.Equal("fallback", d.GetProperty("source").GetString()));
    }
}
