namespace ForecastHost;

// One record per table, with the columns of the study's table definition
// (src/tables.py in demand-forecasting) and of the host's DB2 tables.

public sealed record Sale(string Id, string ItemId, string StoreId, DateOnly Date, int Units);

public sealed record ForecastRow(
    DateOnly AsOf,
    string Id,
    string ItemId,
    string StoreId,
    string Method,
    int Horizon,
    DateOnly TargetDate,
    double Forecast,
    bool Fallback,
    string CodeDigest,
    DateTime MadeAt);

public sealed record SuggestedRow(
    DateOnly AsOf,
    string Id,
    string ItemId,
    string StoreId,
    int Horizon,
    DateOnly TargetDate,
    double Forecast,
    string Source);
