using System.Globalization;
using ForecastHost;

namespace ForecastHost.Tests;

// The C# rule on the real data slice against the Python reference. Reads
// data/SALES.csv (tools/export_slice.py) and results/REFERENCE.csv
// (tools/reference.py); both hold M5 rows and stay on the owner's machine, so this
// test skips anywhere else, CI included.
public class ReconciliationTests
{
    private static readonly DateOnly First = new(2015, 5, 24);
    private static readonly DateOnly Last = new(2016, 5, 15);
    private const double Tolerance = 1e-6;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "tools", "reference.py")))
            dir = dir.Parent;
        return dir?.FullName ?? "";
    }

    // A plain CSV with a header and no quoted fields.
    private static IEnumerable<string[]> Rows(string path) =>
        File.ReadLines(path).Skip(1).Select(line => line.Split(','));

    private static DateOnly Date(string s) =>
        DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Fact]
    public void SeasonalNaiveEqualsThePythonReference()
    {
        var root = RepoRoot();
        var salesPath = Path.Combine(root, "data", "SALES.csv");
        var referencePath = Path.Combine(root, "results", "REFERENCE.csv");
        Assert.SkipUnless(File.Exists(salesPath) && File.Exists(referencePath),
            "needs data/SALES.csv and results/REFERENCE.csv; run tools/export_slice.py and tools/reference.py");

        var series = Rows(salesPath)
            .Select(f => new Sale(f[0], f[1], f[2], Date(f[3]), int.Parse(f[4], CultureInfo.InvariantCulture)))
            .GroupBy(s => s.Id)
            .Select(g => g.OrderBy(s => s.Date).ToList());
        var ours = series
            .SelectMany(s => Rule.SeasonalNaive(s, First, Last, "test", DateTime.UtcNow))
            .ToDictionary(r => (r.AsOf, r.Id, r.TargetDate));

        // as_of, id, target_date, horizon, forecast
        var reference = Rows(referencePath).ToList();
        Assert.Equal(reference.Count, ours.Count);
        var problems = new List<string>();
        foreach (var f in reference)
        {
            var key = (Date(f[0]), f[1], Date(f[2]));
            var expected = double.Parse(f[4], CultureInfo.InvariantCulture);
            if (!ours.TryGetValue(key, out var row))
                problems.Add($"missing {key}");
            else if (Math.Abs(row.Forecast - expected) > Tolerance || row.Horizon != int.Parse(f[3]))
                problems.Add($"{key}: {row.Horizon} {row.Forecast}, reference {f[3]} {expected}");
        }
        Assert.True(problems.Count == 0,
            $"{problems.Count} rows differ:\n{string.Join("\n", problems.Take(5))}");
    }
}
