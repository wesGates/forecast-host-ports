using ForecastHost;

namespace ForecastHost.Tests;

// The rule on synthetic sales: no database, no M5 data.
public class RuleTests
{
    private static readonly DateOnly Day1 = new(2020, 1, 1);
    private static readonly DateTime MadeAt = new(2020, 2, 1, 0, 0, 0, DateTimeKind.Utc);

    // units[i] is the sales on Day1 + i.
    private static List<Sale> Series(string id, params int[] units) =>
        units.Select((u, i) => new Sale(id, "ITEM", "S1", Day1.AddDays(i), u)).ToList();

    private static List<Sale> Counting(string id, int days) =>
        Series(id, Enumerable.Range(1, days).ToArray());

    private static ForecastRow Row(DateOnly asOf, string id, int h, double forecast, string method) =>
        new(asOf, id, "ITEM", "S1", method, h, asOf.AddDays(h), forecast, false, "test", MadeAt);

    [Fact]
    public void ForecastIsTheSalesOneWeekBeforeTheTarget()
    {
        var sales = Counting("A", 21);   // the sales on day n are n
        var origin = Day1.AddDays(20);   // day 21

        var rows = Rule.SeasonalNaive(sales, origin, origin, "test", MadeAt);

        Assert.Equal(new double[] { 15, 16, 17, 18, 19, 20, 21 }, rows.Select(r => r.Forecast));
        Assert.Equal(Enumerable.Range(1, 7), rows.Select(r => r.Horizon));
        Assert.Equal(Enumerable.Range(1, 7).Select(h => origin.AddDays(h)), rows.Select(r => r.TargetDate));
        Assert.All(rows, r =>
        {
            Assert.Equal(origin, r.AsOf);
            Assert.Equal(Rule.Fallback, r.Method);
            Assert.False(r.Fallback);
        });
    }

    [Fact]
    public void SevenRowsForEveryOrigin()
    {
        var sales = Counting("A", 21);
        var rows = Rule.SeasonalNaive(sales, Day1.AddDays(6), Day1.AddDays(20), "test", MadeAt);

        Assert.Equal(15 * 7, rows.Count);
        // The earliest origin, day 7, forecasts from days 1 to 7.
        Assert.Equal(new double[] { 1, 2, 3, 4, 5, 6, 7 }, rows.Take(7).Select(r => r.Forecast));
    }

    [Fact]
    public void RefusesSalesThatSkipADay()
    {
        var sales = Counting("A", 21);
        sales.RemoveAt(10);
        var e = Assert.Throws<ArgumentException>(
            () => Rule.SeasonalNaive(sales, Day1.AddDays(20), Day1.AddDays(20), "test", MadeAt));
        Assert.Contains("skip a day", e.Message);
    }

    [Fact]
    public void RefusesOriginsTheSalesDoNotCover()
    {
        var sales = Counting("A", 21);
        Assert.Throws<ArgumentException>(   // needs days 0..6
            () => Rule.SeasonalNaive(sales, Day1.AddDays(5), Day1.AddDays(20), "test", MadeAt));
        Assert.Throws<ArgumentException>(   // past the last day of sales
            () => Rule.SeasonalNaive(sales, Day1.AddDays(20), Day1.AddDays(21), "test", MadeAt));
    }

    [Fact]
    public void RefusesAnEmptyRangeAndMixedSeries()
    {
        var sales = Counting("A", 21);
        Assert.Throws<ArgumentException>(
            () => Rule.SeasonalNaive(sales, Day1.AddDays(20), Day1.AddDays(19), "test", MadeAt));
        sales[^1] = sales[^1] with { Id = "B" };
        Assert.Throws<ArgumentException>(
            () => Rule.SeasonalNaive(sales, Day1.AddDays(20), Day1.AddDays(20), "test", MadeAt));
    }

    [Fact]
    public void SuggestTakesTheModelWherePresentAndTheFallbackOtherwise()
    {
        var origin = Day1.AddDays(20);
        var fallback = Enumerable.Range(1, 7).Select(h => Row(origin, "A", h, h, Rule.Fallback)).ToList();
        var model = new[] { Row(origin, "A", 2, 102.5, "m"), Row(origin, "A", 5, 105.5, "m") };

        var rows = Rule.Suggest(fallback, model);

        Assert.Equal(new double[] { 1, 102.5, 3, 4, 105.5, 6, 7 }, rows.Select(r => r.Forecast));
        Assert.Equal(new[] { "SEASONAL_NAIVE", "m", "SEASONAL_NAIVE", "SEASONAL_NAIVE", "m",
                            "SEASONAL_NAIVE", "SEASONAL_NAIVE" }, rows.Select(r => r.Source));
    }

    [Fact]
    public void SuggestIgnoresModelRowsWithoutAFallbackRow()
    {
        var origin = Day1.AddDays(20);
        var fallback = new[] { Row(origin, "A", 1, 1, Rule.Fallback) };
        var model = new[] { Row(origin, "A", 1, 9, "m"), Row(origin, "B", 1, 9, "m") };

        var rows = Rule.Suggest(fallback, model);

        Assert.Single(rows);
        Assert.Equal("m", rows[0].Source);
    }
}
