// ForecastHost: the RPG program FCSTNAIVE ported to C# on SQL Server.
//
// Reads host.sales and host.forecast, writes the SEASONAL_NAIVE rows into
// host.forecast and the suggested rows into host.suggested for every origin from
// --from to --to, in one transaction. The defaults are the origins and the model
// RUNCHAIN uses on the IBM i host.
//
//   dotnet run --project csharp/ForecastHost -- [--from 2015-05-24] [--to 2016-05-15]
//                                               [--model xgboost_rel@item_id]
//   dotnet run --project csharp/ForecastHost -- --seed-demo
//
// --seed-demo fills the database forecast_host_demo with synthetic rows (Demo.cs) and
// runs the port there over the demo's origins. The connection comes from the
// environment; see HostTables.ConnectionString.

using System.Globalization;
using ForecastHost;
using Microsoft.Data.SqlClient;

var first = new DateOnly(2015, 5, 24);
var last = new DateOnly(2016, 5, 15);
var model = "xgboost_rel@item_id";
string? database = null;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--seed-demo")
    {
        Demo.Seed();
        (first, last, model, database) = (Demo.First, Demo.Last, Demo.Model, Demo.Database);
        continue;
    }
    var value = i + 1 < args.Length ? args[++i] : throw Usage($"{args[i]} needs a value");
    switch (args[i - 1])
    {
        case "--from": first = ParseDate(value); break;
        case "--to": last = ParseDate(value); break;
        case "--model": model = value; break;
        default: throw Usage($"unknown option {args[i - 1]}");
    }
}

using var connection = new SqlConnection(HostTables.ConnectionString(database));
connection.Open();
var result = Chain.Run(connection, first, last, model);

Console.WriteLine($"{connection.Database}, origins {first:yyyy-MM-dd} to {last:yyyy-MM-dd}: " +
    $"{result.FallbackRows} {Rule.Fallback} rows, {result.SuggestedRows} suggested rows " +
    $"({result.FromModel} from {model}, " +
    $"{result.SuggestedRows - result.FromModel} from {Rule.Fallback})");
return 0;

static DateOnly ParseDate(string s) =>
    DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture);

static ArgumentException Usage(string message) =>
    new($"{message}. Options: --from yyyy-MM-dd --to yyyy-MM-dd --model <method> --seed-demo");
