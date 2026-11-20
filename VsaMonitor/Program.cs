using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace VsaMonitor;

/// <summary>
/// VSA &amp; Orderflow v4.0 · QuantPulse Backend (.NET 8 Self-Contained)
/// - Suscripción en tiempo real a Binance WebSockets (wss://stream.binance.com:9443/ws/!ticker@arr)
///   y Futuros USDT-M (wss://fstream.binance.com/ws/!ticker@arr): cero polling REST para precios, cero HTTP 429.
/// - Evalúa alertas de precio y SL/TP del Simulador Aislado de Paper Trading con latencia sub-segundo.
/// - Evalúa señales confirmadas (con respaldo BH + Filtro Duro de Régimen HTF EMA50/200 + Triggers Adaptativos)
///   sobre velas cerradas y notifica vía Telegram.
/// </summary>
public static class Program
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private static readonly string[] SpotHosts =
    {
        "https://api.binance.com",
        "https://api-gcp.binance.com",
        "https://api1.binance.com",
        "https://api2.binance.com",
        "https://api3.binance.com",
        "https://data-api.binance.vision"
    };
    private static readonly string[] FuturesHosts = { "https://fapi.binance.com" };

    private static readonly ConcurrentDictionary<string, double> LivePrices = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<int> Main(string[] args)
    {
        string baseDir = Environment.GetEnvironmentVariable("VSA_WORKDIR") ?? Directory.GetCurrentDirectory();
        string cfgPath = Path.Combine(baseDir, "config.json");
        string statePath = Path.Combine(baseDir, "state.json");
        string paperPath = Path.Combine(baseDir, "paper_state.json");

        int loopSeconds = int.TryParse(Environment.GetEnvironmentVariable("LOOP_SECONDS"), out int ls) ? ls : 260;
        bool testNotify = Environment.GetEnvironmentVariable("TEST_NOTIFY") == "1";
        string tgToken = (Environment.GetEnvironmentVariable("TELEGRAM_TOKEN") ?? "").Trim();
        string tgChat = (Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID") ?? "").Trim();

        var cfg = LoadJson(cfgPath, new AppConfig());
        var state = LoadJson(statePath, new MonitorState());
        var paper = LoadJson(paperPath, new PaperState
        {
            InitialCapital = cfg.PaperTrading.InitialCapital,
            Cash = cfg.PaperTrading.InitialCapital
        });

        Console.WriteLine($"[VSA QuantPulse v4.0 .NET] Mercado={cfg.Market.ToUpperInvariant()} | Pares={cfg.Symbols.Count} | Favoritas={cfg.Favorites.Count} | FiltroHTF={cfg.HardRegimeFilter} | Paper={cfg.PaperTrading.Enabled} | Loop={loopSeconds}s");

        if (testNotify)
        {
            await SendTelegramAsync(tgToken, tgChat,
                $"✅ VSA & Orderflow v4.0 (.NET 8 WebSocket) operativo.\nMercado: {cfg.Market.ToUpperInvariant()} · Pares: {cfg.Symbols.Count} · Favoritas: {cfg.Favorites.Count}\nFiltro Duro HTF: {(cfg.HardRegimeFilter ? "ON" : "OFF")} · Paper Balance: {paper.TotalEquity:N2} USDT");
        }

        using var cts = loopSeconds > 0
            ? new CancellationTokenSource(TimeSpan.FromSeconds(loopSeconds))
            : new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        // 1. Lanzar evaluación de velas cerradas y señales confirmadas en paralelo con el stream WebSocket
        var signalTask = Task.Run(async () =>
        {
            try
            {
                await CheckConfirmedSignalsAsync(cfg, state, paper, tgToken, tgChat, cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Console.Error.WriteLine($"[Señales error] {ex.Message}"); }
        }, cts.Token);

        // 2. Suscribirse a Binance WebSocket (!ticker@arr) para alertas de precio y Paper Trading en tiempo real
        try
        {
            await RunTickerWebSocketLoopAsync(cfg, state, paper, tgToken, tgChat, cts.Token);
        }
        catch (OperationCanceledException) { }

        await Task.WhenAny(signalTask, Task.Delay(5000));

        SaveJson(statePath, state);
        SaveJson(paperPath, paper);
        Console.WriteLine($"[VSA QuantPulse v4.0 .NET] Ciclo finalizado. Paper Equity={paper.TotalEquity:N2} USDT (Abiertas={paper.Open.Count}, Cerradas={paper.Closed.Count}).");
        return 0;
    }

    private static async Task RunTickerWebSocketLoopAsync(
        AppConfig cfg, MonitorState state, PaperState paper,
        string tgToken, string tgChat, CancellationToken ct)
    {
        bool isFutures = cfg.Market.Equals("futures", StringComparison.OrdinalIgnoreCase);
        var wsUri = new Uri(isFutures
            ? "wss://fstream.binance.com/ws/!ticker@arr"
            : "wss://stream.binance.com:9443/ws/!ticker@arr");

        var watchedSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in cfg.Symbols) watchedSymbols.Add(s);
        foreach (var f in cfg.Favorites) watchedSymbols.Add(f);
        foreach (var a in cfg.PriceAlerts) if (!string.IsNullOrWhiteSpace(a.Sym)) watchedSymbols.Add(a.Sym);
        foreach (var p in paper.Open) watchedSymbols.Add(p.Sym);

        int backoffMs = 1000;
        var buffer = new byte[65536];

        while (!ct.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            try
            {
                Console.WriteLine($"[WebSocket] Conectando a {wsUri} ...");
                await ws.ConnectAsync(wsUri, ct);
                Console.WriteLine("[WebSocket] Conectado en tiempo real (!ticker@arr).");
                backoffMs = 1000;

                using var ms = new MemoryStream();
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    ms.SetLength(0);
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close) break;
                    if (ms.Length == 0) continue;

                    await ProcessTickerFrameAsync(ms.ToArray(), watchedSymbols, cfg, state, paper, tgToken, tgChat);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WebSocket reconexión en {backoffMs}ms] {ex.Message}");
                await Task.Delay(backoffMs, ct);
                backoffMs = Math.Min(30_000, backoffMs * 2);
            }
        }
    }

    private static async Task ProcessTickerFrameAsync(
        byte[] payload, HashSet<string> watched,
        AppConfig cfg, MonitorState state, PaperState paper,
        string tgToken, string tgChat)
    {
        using var doc = JsonDocument.Parse(payload);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("s", out var sEl) || !item.TryGetProperty("c", out var cEl)) continue;
            string? sym = sEl.GetString();
            if (string.IsNullOrEmpty(sym) || !watched.Contains(sym)) continue;
            if (!double.TryParse(cEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double px) || px <= 0) continue;

            LivePrices[sym] = px;

            // A) Evaluar alertas de precio en tiempo real
            foreach (var alert in cfg.PriceAlerts)
            {
                if (!sym.Equals(alert.Sym, StringComparison.OrdinalIgnoreCase)) continue;
                string alertMarket = string.IsNullOrWhiteSpace(alert.Market) ? cfg.Market : alert.Market!;
                if (!alertMarket.Equals(cfg.Market, StringComparison.OrdinalIgnoreCase)) continue;

                string key = alert.Key(cfg.Market);
                long prev = state.FiredAlerts.GetValueOrDefault(key, 0);
                if (!alert.Repeat && prev > 0) continue;
                if (alert.Repeat && prev > 0 && nowMs - prev < 60_000) continue;

                bool up = alert.Dir.Equals("up", StringComparison.OrdinalIgnoreCase);
                bool hit = up ? px >= alert.Target : px <= alert.Target;
                if (!hit) continue;

                state.FiredAlerts[key] = nowMs;
                string msg = $"{(up ? "🚨📈 SUBIÓ" : "🚨📉 BAJÓ")} {sym} ({cfg.Market.ToUpperInvariant()})\nPrecio WebSocket: {QuantEngine.FormatPrice(px)}\nObjetivo {(up ? "≥" : "≤")} {QuantEngine.FormatPrice(alert.Target)}";
                Console.WriteLine($"[ALERTA PRECIO WS] {sym} @ {px}");
                await SendTelegramAsync(tgToken, tgChat, msg);
            }

            // B) Evaluar posiciones abiertas del Simulador Aislado de Paper Trading
            await UpdatePaperOnTickAsync(paper, sym, px, nowMs, tgToken, tgChat);
        }
    }

    private static async Task UpdatePaperOnTickAsync(
        PaperState paper, string sym, double px, long nowMs, string tgToken, string tgChat)
    {
        List<PaperPosition> toClose = new();
        lock (paper)
        {
            foreach (var pos in paper.Open)
            {
                if (!pos.Sym.Equals(sym, StringComparison.OrdinalIgnoreCase)) continue;
                pos.LastPrice = px;
                pos.UnrealizedUsd = pos.Qty * (pos.Side * (px - pos.Entry) - pos.CostSide * (pos.Entry + px));

                bool hitSl = pos.Side > 0 ? px <= pos.Sl : px >= pos.Sl;
                bool hitTp = pos.Side > 0 ? px >= pos.Tp : px <= pos.Tp;
                if (hitSl || hitTp || nowMs >= pos.ExpiresAt)
                {
                    toClose.Add(pos);
                }
            }
        }

        foreach (var pos in toClose)
        {
            bool hitSl = pos.Side > 0 ? px <= pos.Sl : px >= pos.Sl;
            bool hitTp = pos.Side > 0 ? px >= pos.Tp : px <= pos.Tp;
            string reason = hitSl ? "STOP" : hitTp ? "OBJETIVO" : "TIEMPO";
            double exitPx = hitSl ? pos.Sl : hitTp ? pos.Tp : px;

            double grossUsd = pos.Qty * pos.Side * (exitPx - pos.Entry);
            double feesUsd = pos.Qty * pos.CostSide * (pos.Entry + exitPx);
            double netUsd = grossUsd - feesUsd;
            double netPct = pos.Side * (exitPx - pos.Entry) / pos.Entry - 2.0 * pos.CostSide;
            double rMult = pos.Risk > 0 ? netPct / (pos.Risk / pos.Entry) : 0;

            lock (paper)
            {
                if (!paper.Open.Remove(pos)) continue;
                paper.Cash += netUsd;
                paper.Closed.Insert(0, new PaperClosedTrade
                {
                    Id = pos.Id,
                    OpenedAt = pos.OpenedAt,
                    ClosedAt = nowMs,
                    Sym = pos.Sym,
                    Market = pos.Market,
                    Tf = pos.Tf,
                    Rule = pos.Rule,
                    Side = pos.Side,
                    Entry = pos.Entry,
                    Exit = exitPx,
                    Reason = reason,
                    NetUsd = netUsd,
                    NetPct = netPct,
                    RMultiple = rMult
                });
                if (paper.Closed.Count > 500) paper.Closed.RemoveRange(500, paper.Closed.Count - 500);
            }

            Console.WriteLine($"[PAPER CERRADO] {pos.Sym} ({reason}) {netUsd:+0.00;-0.00} USDT ({rMult:+0.00;-0.00}R)");
            await SendTelegramAsync(tgToken, tgChat,
                $"🧪 PAPER CERRADO ({reason}) · {pos.Sym} {pos.Tf.ToUpperInvariant()}\n{(pos.Side > 0 ? "LONG" : "SHORT")} {pos.Rule}: {QuantEngine.FormatPrice(pos.Entry)} → {QuantEngine.FormatPrice(exitPx)}\nResultado: {netUsd:+0.00;-0.00} USDT ({rMult:+0.00;-0.00}R) · Balance Paper: {paper.TotalEquity:N2} USDT");
        }
    }

    private static async Task CheckConfirmedSignalsAsync(
        AppConfig cfg, MonitorState state, PaperState paper,
        string tgToken, string tgChat, CancellationToken ct)
    {
        if (!cfg.SigAlertOn) return;
        string market = cfg.Market.Equals("futures", StringComparison.OrdinalIgnoreCase) ? "futures" : "spot";
        double costSide = market == "futures" ? 0.0006 : 0.0012;
        double costRt = costSide * 2.0;
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Priorizar favoritas al inicio de la lista de escaneo
        var orderedSymbols = cfg.Favorites
            .Concat(cfg.Symbols)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToList();

        var htfCache = new Dictionary<string, RegimeResult>(StringComparer.OrdinalIgnoreCase);

        foreach (string sym in orderedSymbols)
        {
            if (ct.IsCancellationRequested) break;
            foreach (string tf in cfg.Timeframes)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    var candles = await FetchHistoryAsync(market, sym, tf, Math.Clamp(cfg.Hist, 300, 3000), ct);
                    var closed = candles.Where(k => k.CloseTime < nowMs).ToList();
                    if (closed.Count < 100) continue;

                    var lastCandle = closed[^1];
                    string key = $"{market}|{sym}|{tf}";
                    if (state.Candles.TryGetValue(key, out long prevT) && prevT == lastCandle.OpenTime) continue;

                    bool firstSeen = !state.Candles.ContainsKey(key);
                    state.Candles[key] = lastCandle.OpenTime;
                    if (firstSeen && (nowMs - (lastCandle.CloseTime + 1)) > 20 * 60 * 1000) continue;

                    // Evaluar Régimen Duro HTF (EMA 50 vs EMA 200)
                    string htf = QuantEngine.HigherTimeframeFor(tf, cfg.RegimeHtf);
                    string htfKey = $"{sym}|{htf}";
                    if (!htfCache.TryGetValue(htfKey, out var htfRegime))
                    {
                        var htfCandles = htf == tf ? candles : await FetchHistoryAsync(market, sym, htf, 300, ct);
                        var htfClosed = htfCandles.Where(k => k.CloseTime < nowMs).ToList();
                        htfRegime = QuantEngine.EvaluateRegime(htfClosed);
                        htfCache[htfKey] = htfRegime;
                    }

                    var hit = QuantEngine.EvaluateLatestConfirmed(
                        closed, htfRegime, sym, tf, costRt, cfg.Kfine,
                        cfg.HardRegimeFilter, cfg.StrictLateral, cfg.SigAlert);

                    if (hit == null) continue;

                    double px = LivePrices.TryGetValue(sym, out double livePx) ? livePx : lastCandle.Close;
                    double risk = Math.Max(1.3 * hit.Atr, px * 0.0035);
                    double sl = px - hit.Bias * risk;
                    double tp = px + hit.Bias * 2.0 * risk;
                    bool up = hit.Bias > 0;
                    bool isFav = cfg.Favorites.Any(f => f.Equals(sym, StringComparison.OrdinalIgnoreCase));

                    // Ejecutar en Simulador Aislado de Paper Trading si corresponde
                    string paperNote = "";
                    if (cfg.PaperTrading.Enabled && (up || market == "futures"))
                    {
                        bool opened = TryOpenPaperPosition(paper, cfg.PaperTrading, sym, market, tf, hit.Code, hit.Bias, px, sl, tp, risk, costSide, Math.Max(12, hit.Horizon));
                        if (opened) paperNote = $"\n🧪 Paper Trading: Ejecutado (Balance virtual {paper.TotalEquity:N2} USDT)";
                    }

                    string msg =
                        $"{(up ? "🚨📈 COMPRA CONFIRMADA" : "🚨📉 VENTA CONFIRMADA")} {(isFav ? "★ " : "")}· {sym} ({market.ToUpperInvariant()}) · {tf.ToUpperInvariant()}\n" +
                        $"Regla: {hit.Code} {hit.Name}\n" +
                        $"Régimen HTF ({htf.ToUpperInvariant()}): {hit.HtfRegime.Label} ({hit.HtfRegime.SpreadPct * 100:+0.00;-0.00}%) · Hurst: {(double.IsFinite(hit.Hurst) ? hit.Hurst.ToString("0.00", CultureInfo.InvariantCulture) : "—")}\n" +
                        $"Evidencia (h={hit.Horizon}v): n={hit.SampleCount} · acierto {hit.HitRate * 100:0.0}% · neto {hit.NetReturn * 100:+0.00;-0.00}% · p={hit.PValue:0.0000} (BH✓)\n" +
                        $"Ref: {QuantEngine.FormatPrice(px)} · SL: {QuantEngine.FormatPrice(sl)} · Obj 2R: {QuantEngine.FormatPrice(tp)}{paperNote}";

                    Console.WriteLine($"[SEÑAL CONFIRMADA] {sym} {tf} {hit.Code} (HTF={hit.HtfRegime.Label})");
                    await SendTelegramAsync(tgToken, tgChat, msg);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Señal {sym} {tf}] {ex.Message}");
                }
            }
        }
    }

    private static bool TryOpenPaperPosition(
        PaperState paper, PaperConfig pcfg, string sym, string market, string tf,
        string ruleCode, int side, double entry, double sl, double tp, double risk, double costSide, int maxBars)
    {
        lock (paper)
        {
            if (paper.Open.Count >= pcfg.MaxOpenPositions) return false;
            if (paper.Open.Any(p => p.Sym.Equals(sym, StringComparison.OrdinalIgnoreCase) && p.Tf == tf && p.Side == side)) return false;

            double eq = paper.TotalEquity;
            double riskUsd = eq * (pcfg.RiskPct / 100.0);
            double perUnit = Math.Abs(entry - sl) + costSide * (entry + sl);
            if (perUnit <= 0 || entry <= 0) return false;

            double lev = market == "futures" ? pcfg.Leverage : 1.0;
            double qty = riskUsd / perUnit;
            double notional = qty * entry;
            if (notional > eq * lev) qty = (eq * lev) / entry;

            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            paper.Open.Insert(0, new PaperPosition
            {
                Id = $"{nowMs}-{sym}-{tf}",
                OpenedAt = nowMs,
                Sym = sym,
                Market = market,
                Tf = tf,
                Rule = ruleCode,
                Side = side,
                Entry = entry,
                LastPrice = entry,
                Sl = sl,
                Tp = tp,
                Risk = risk,
                Qty = qty,
                CostSide = costSide,
                ExpiresAt = nowMs + maxBars * QuantEngine.TfToMs(tf),
                UnrealizedUsd = -qty * costSide * entry
            });
            return true;
        }
    }

    private static async Task<List<Candle>> FetchHistoryAsync(string market, string sym, string tf, int total, CancellationToken ct)
    {
        var hosts = market == "futures" ? FuturesHosts : SpotHosts;
        string path = market == "futures" ? "/fapi/v1/klines" : "/api/v3/klines";
        var all = new List<Candle>(total);
        long? endTime = null;
        int pages = (int)Math.Ceiling(total / 1000.0);

        for (int p = 0; p < pages && all.Count < total; p++)
        {
            int limit = Math.Min(1000, total - all.Count);
            List<Candle>? page = null;
            foreach (string host in hosts)
            {
                string url = $"{host}{path}?symbol={Uri.EscapeDataString(sym)}&interval={Uri.EscapeDataString(tf)}&limit={limit}" +
                             (endTime.HasValue ? $"&endTime={endTime.Value}" : "");
                try
                {
                    using var resp = await Http.GetAsync(url, ct);
                    if (!resp.IsSuccessStatusCode) continue;
                    using var stream = await resp.Content.ReadAsStreamAsync(ct);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                    page = ParseKlines(doc.RootElement);
                    break;
                }
                catch { /* probar siguiente host */ }
            }
            if (page == null || page.Count == 0) break;
            if (all.Count > 0)
            {
                long first = all[0].OpenTime;
                page = page.Where(k => k.OpenTime < first).ToList();
            }
            all.InsertRange(0, page);
            if (page.Count < limit) break;
            endTime = page[0].OpenTime - 1;
        }
        return all;
    }

    private static List<Candle> ParseKlines(JsonElement arr)
    {
        var list = new List<Candle>();
        if (arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var row in arr.EnumerateArray())
        {
            if (row.GetArrayLength() < 11) continue;
            long ot = row[0].GetInt64();
            double o = ParseD(row[1]);
            double h = ParseD(row[2]);
            double l = ParseD(row[3]);
            double c = ParseD(row[4]);
            double v = ParseD(row[5]);
            long ct = row[6].GetInt64();
            double qv = ParseD(row[7]);
            int tr = row[8].GetInt32();
            double tb = ParseD(row[9]);
            double tq = ParseD(row[10]);
            if (o > 0 && c > 0 && h >= l)
                list.Add(new Candle(ot, ct, o, h, l, c, v, qv, tr, tb, tq));
        }
        return list;
    }

    private static double ParseD(JsonElement el) =>
        el.ValueKind == JsonValueKind.Number
            ? el.GetDouble()
            : double.Parse(el.GetString() ?? "0", NumberStyles.Float, CultureInfo.InvariantCulture);

    private static async Task SendTelegramAsync(string token, string chatId, string text)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(chatId))
        {
            Console.WriteLine($"[Sin Telegram Secrets]\n{text}");
            return;
        }
        try
        {
            string url = $"https://api.telegram.org/bot{Uri.EscapeDataString(token)}/sendMessage";
            var payload = new { chat_id = chatId, text, disable_web_page_preview = true };
            using var resp = await Http.PostAsJsonAsync(url, payload);
            if (!resp.IsSuccessStatusCode)
                Console.Error.WriteLine($"[Telegram HTTP {(int)resp.StatusCode}]");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Telegram error] {ex.Message}");
        }
    }

    private static T LoadJson<T>(string path, T fallback)
    {
        try
        {
            if (!File.Exists(path)) return fallback;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOpts) ?? fallback;
        }
        catch { return fallback; }
    }

    private static void SaveJson<T>(string path, T obj)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(obj, JsonOpts) + "\n", Encoding.UTF8);
    }
}
