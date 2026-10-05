// ForecastHost.Api: a read-only JSON API over the host tables in SQL Server.
//
//   GET /series                               every series: id, item and store
//   GET /series/{id}/sales?from=&to=          daily units sold, dates as yyyy-MM-dd
//   GET /series/{id}/suggested?as_of=         the suggested rows for one origin
//                                             (the latest when as_of is left out),
//                                             each with source "model" or "fallback"
//
// No authentication; it listens on localhost only (http://localhost:5023 from
// dotnet run). The connection comes from the environment, as for the port; see
// HostTables.ConnectionString. Field names are the tables' column names, and a
// date that does not parse as yyyy-MM-dd gets 400.

using System.Text.Json;
using ForecastHost;
using ForecastHost.Api;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
// Built on first use, from configuration (environment variables included), so a test
// host can set MSSQL_DATABASE.
builder.Services.AddSingleton(sp =>
    new Queries(HostTables.ConnectionString(sp.GetRequiredService<IConfiguration>()["MSSQL_DATABASE"])));
var app = builder.Build();

app.MapGet("/series", (Queries q) => q.Series());

app.MapGet("/series/{id}/sales", (Queries q, string id, DateOnly? from, DateOnly? to) =>
    q.SeriesExists(id)
        ? Results.Ok(q.Sales(id, from, to))
        : Results.NotFound(new { error = $"no series {id}" }));

app.MapGet("/series/{id}/suggested",
    (Queries q, string id, [FromQuery(Name = "as_of")] DateOnly? asOf) =>
        q.Suggested(id, asOf) is { } s
            ? Results.Ok(s)
            : Results.NotFound(new { error = $"no suggested rows for {id}" +
                (asOf is null ? "" : $" from {asOf:yyyy-MM-dd}") }));

app.Run();

// Lets the tests start the API with WebApplicationFactory<Program>.
public partial class Program;
