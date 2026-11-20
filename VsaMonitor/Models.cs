using System.Text.Json.Serialization;

namespace VsaMonitor;

public sealed class AppConfig
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "4.0";

    [JsonPropertyName("market")]
    public string Market { get; set; } = "spot";

    [JsonPropertyName("favorites")]
    public List<string> Favorites { get; set; } = new() { "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT", "XRPUSDT" };

    [JsonPropertyName("symbols")]
    public List<string> Symbols { get; set; } = new() { "BTCUSDT", "ETHUSDT", "SOLUSDT" };

    [JsonPropertyName("timeframes")]
    public List<string> Timeframes { get; set; } = new() { "1h", "4h" };

    [JsonPropertyName("hist")]
    public int Hist { get; set; } = 3000;

    [JsonPropertyName("Kfine")]
    public int Kfine { get; set; } = 4000;

    [JsonPropertyName("hardRegimeFilter")]
    public bool HardRegimeFilter { get; set; } = true;

    [JsonPropertyName("regimeHtf")]
    public string RegimeHtf { get; set; } = "auto";

    [JsonPropertyName("strictLateral")]
    public bool StrictLateral { get; set; } = false;

    [JsonPropertyName("adaptiveTriggers")]
    public bool AdaptiveTriggers { get; set; } = true;

    [JsonPropertyName("paperTrading")]
    public PaperConfig PaperTrading { get; set; } = new();

    [JsonPropertyName("sigAlertOn")]
    public bool SigAlertOn { get; set; } = true;

    [JsonPropertyName("sigAlert")]
    public string SigAlert { get; set; } = "BOTH";

    [JsonPropertyName("priceAlerts")]
    public List<PriceAlert> PriceAlerts { get; set; } = new();
}

public sealed class PaperConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("initialCapital")]
    public double InitialCapital { get; set; } = 10000.0;

    [JsonPropertyName("riskPct")]
    public double RiskPct { get; set; } = 1.0;

    [JsonPropertyName("leverage")]
    public double Leverage { get; set; } = 2.0;

    [JsonPropertyName("maxOpenPositions")]
    public int MaxOpenPositions { get; set; } = 5;
}

public sealed class PriceAlert
{
    [JsonPropertyName("sym")]
    public string Sym { get; set; } = string.Empty;

    [JsonPropertyName("market")]
    public string? Market { get; set; }

    [JsonPropertyName("dir")]
    public string Dir { get; set; } = "up";

    [JsonPropertyName("target")]
    public double Target { get; set; }

    [JsonPropertyName("repeat")]
    public bool Repeat { get; set; }

    public string Key(string defaultMarket) =>
        $"{(string.IsNullOrWhiteSpace(Market) ? defaultMarket : Market)}|{Sym.ToUpperInvariant()}|{Dir}|{Target:G17}";
}

public readonly record struct Candle(
    long OpenTime,
    long CloseTime,
    double Open,
    double High,
    double Low,
    double Close,
    double Volume,
    double QuoteVolume,
    int Trades,
    double TakerBuyBase,
    double TakerBuyQuote
);

public readonly record struct TickerUpdate(
    string Symbol,
    double LastPrice,
    double PriceChangePercent,
    double QuoteVolume,
    long EventTime
);

public sealed class MonitorState
{
    [JsonPropertyName("candles")]
    public Dictionary<string, long> Candles { get; set; } = new();

    [JsonPropertyName("firedAlerts")]
    public Dictionary<string, long> FiredAlerts { get; set; } = new();
}

public sealed class PaperPosition
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("openedAt")]
    public long OpenedAt { get; set; }

    [JsonPropertyName("sym")]
    public string Sym { get; set; } = string.Empty;

    [JsonPropertyName("market")]
    public string Market { get; set; } = "spot";

    [JsonPropertyName("tf")]
    public string Tf { get; set; } = "1h";

    [JsonPropertyName("rule")]
    public string Rule { get; set; } = string.Empty;

    [JsonPropertyName("side")]
    public int Side { get; set; }

    [JsonPropertyName("entry")]
    public double Entry { get; set; }

    [JsonPropertyName("lastPrice")]
    public double LastPrice { get; set; }

    [JsonPropertyName("sl")]
    public double Sl { get; set; }

    [JsonPropertyName("tp")]
    public double Tp { get; set; }

    [JsonPropertyName("risk")]
    public double Risk { get; set; }

    [JsonPropertyName("qty")]
    public double Qty { get; set; }

    [JsonPropertyName("costSide")]
    public double CostSide { get; set; }

    [JsonPropertyName("expiresAt")]
    public long ExpiresAt { get; set; }

    [JsonPropertyName("unrealizedUsd")]
    public double UnrealizedUsd { get; set; }
}

public sealed class PaperClosedTrade
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("openedAt")]
    public long OpenedAt { get; set; }

    [JsonPropertyName("closedAt")]
    public long ClosedAt { get; set; }

    [JsonPropertyName("sym")]
    public string Sym { get; set; } = string.Empty;

    [JsonPropertyName("market")]
    public string Market { get; set; } = "spot";

    [JsonPropertyName("tf")]
    public string Tf { get; set; } = "1h";

    [JsonPropertyName("rule")]
    public string Rule { get; set; } = string.Empty;

    [JsonPropertyName("side")]
    public int Side { get; set; }

    [JsonPropertyName("entry")]
    public double Entry { get; set; }

    [JsonPropertyName("exit")]
    public double Exit { get; set; }

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonPropertyName("netUsd")]
    public double NetUsd { get; set; }

    [JsonPropertyName("netPct")]
    public double NetPct { get; set; }

    [JsonPropertyName("rMultiple")]
    public double RMultiple { get; set; }
}

public sealed class PaperState
{
    [JsonPropertyName("initialCapital")]
    public double InitialCapital { get; set; } = 10000.0;

    [JsonPropertyName("cash")]
    public double Cash { get; set; } = 10000.0;

    [JsonPropertyName("open")]
    public List<PaperPosition> Open { get; set; } = new();

    [JsonPropertyName("closed")]
    public List<PaperClosedTrade> Closed { get; set; } = new();

    public double TotalEquity => Cash + Open.Sum(p => p.UnrealizedUsd);
}

public readonly record struct RegimeResult(int Direction, string Label, double SpreadPct, bool Ready);

public readonly record struct AdaptiveBounds(double VolCompression, double RsiLow, double RsiHigh, double ZTrigger);

public sealed record SignalHit(
    string Code,
    string Name,
    string Category,
    int Bias,
    int Horizon,
    int SampleCount,
    double HitRate,
    double NetReturn,
    double PValue,
    double TNet,
    double Atr,
    double Hurst,
    AdaptiveBounds Adaptive,
    RegimeResult HtfRegime
);
