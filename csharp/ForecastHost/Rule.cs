namespace ForecastHost;

// The host's fallback forecast and the suggested-order rule, as in the RPG program
// ibmi/rpg/fcstnaive.sqlrpgle. No database here, so the tests can call it directly.
public static class Rule
{
    public const string Fallback = "SEASONAL_NAIVE";
    public const int Season = 7;
    public const int Horizon = 7;

    // "This day last week" for one series and every origin T from first to last:
    // the forecast for T+h is the sales on T+h-7. sales holds one series, one row per
    // day, oldest first.
    public static List<ForecastRow> SeasonalNaive(
        IReadOnlyList<Sale> sales, DateOnly first, DateOnly last, string codeDigest, DateTime madeAt)
    {
        if (sales.Count == 0)
            throw new ArgumentException("no sales", nameof(sales));
        var s = sales[0];
        for (var i = 1; i < sales.Count; i++)
        {
            if (sales[i].Id != s.Id)
                throw new ArgumentException($"sales mix series {s.Id} and {sales[i].Id}", nameof(sales));
            if (sales[i].Date != s.Date.AddDays(i))
                throw new ArgumentException($"sales for {s.Id} skip a day before {sales[i].Date:yyyy-MM-dd}", nameof(sales));
        }
        if (last < first)
            throw new ArgumentException($"origin range {first:yyyy-MM-dd} to {last:yyyy-MM-dd} is empty");
        if (first.AddDays(-(Season - 1)) < s.Date || last > sales[^1].Date)
            throw new ArgumentException($"sales for {s.Id} do not cover the origins");

        var rows = new List<ForecastRow>();
        for (var asOf = first; asOf <= last; asOf = asOf.AddDays(1))
        {
            var t = asOf.DayNumber - s.Date.DayNumber;   // index of the origin day T
            for (var h = 1; h <= Horizon; h++)
            {
                rows.Add(new ForecastRow(asOf, s.Id, s.ItemId, s.StoreId, Fallback, h,
                    asOf.AddDays(h), sales[t + h - Season].Units, false, codeDigest, madeAt));
            }
        }
        return rows;
    }

    // One suggested row per fallback row: the model's forecast where the model has a
    // row for the same origin, series and target day, the fallback's otherwise.
    public static List<SuggestedRow> Suggest(
        IEnumerable<ForecastRow> fallback, IEnumerable<ForecastRow> model)
    {
        var byKey = model.ToDictionary(r => (r.AsOf, r.Id, r.TargetDate));
        var rows = new List<SuggestedRow>();
        foreach (var f in fallback)
        {
            var use = byKey.TryGetValue((f.AsOf, f.Id, f.TargetDate), out var m) ? m : f;
            rows.Add(new SuggestedRow(f.AsOf, f.Id, f.ItemId, f.StoreId, f.Horizon,
                f.TargetDate, use.Forecast, use.Method));
        }
        return rows;
    }
}
