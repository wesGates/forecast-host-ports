// ForecastHost: the RPG program FCSTNAIVE ported to C# on SQL Server.
//
// Reads host.sales and host.forecast, writes the SEASONAL_NAIVE rows into
// host.forecast and the suggested rows into host.suggested for every origin from
// --from to --to, in one transaction. The defaults are the origins and the model
// RUNCHAIN uses on the IBM i host.
//
//   dotnet run --project csharp/ForecastHost -- [--from 2015-05-24] [--to 2016-05-15]
//                                               [--model xgboost_rel@item_id]
//
// The connection comes from the environment; see HostTables.ConnectionString.

using System.Globalization;
using ForecastHost;
using Microsoft.Data.SqlClient;

const string CodeDigest = "ForecastHost 1.0";

var first = new DateOnly(2015, 5, 24);
var last = new DateOnly(2016, 5, 15);
var model = "xgboost_rel@item_id";
for (var i = 0; i < args.Length; i += 2)
{
    var value = i + 1 < args.Length ? args[i + 1] : throw Usage($"{args[i]} needs a value");
    switch (args[i])
    {
        case "--from": first = ParseDate(value); break;
        case "--to": last = ParseDate(value); break;
        case "--model": model = value; break;
        default: throw Usage($"unknown option {args[i]}");
    }
}

using var connection = new SqlConnection(HostTables.ConnectionString());
connection.Open();
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

var fromModel = suggested.Count(r => r.Source == model);
Console.WriteLine($"origins {first:yyyy-MM-dd} to {last:yyyy-MM-dd}: " +
    $"{fallback.Count} {Rule.Fallback} rows, {suggested.Count} suggested rows " +
    $"({fromModel} from {model}, {suggested.Count - fromModel} from {Rule.Fallback})");
return 0;

static DateOnly ParseDate(string s) =>
    DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture);

static ArgumentException Usage(string message) =>
    new($"{message}. Options: --from yyyy-MM-dd --to yyyy-MM-dd --model <method>");
