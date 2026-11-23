using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace VsaMonitor;

/// <summary>
/// VSA &amp; Orderflow v6.0 · Edge Lab Backend (.NET 8 Self-Contained)
/// - v6: si config.json trae el libro de ventajas (edgeBook) del Laboratorio de la web, las alertas y el Paper salen SOLO de
///   reglas validadas fuera de muestra en el universo (candidatas: solo Paper). Sin libro vigente, modo clásico por par.
/// - v6: el Paper se concilia al inicio de cada corrida con velas de 1m/5m/15m (stops y objetivos tocados entre corridas).
/// - WebSocket 1: Binance !ticker@arr (precios en tiempo real, cero HTTP 429).
/// - WebSocket 2: Binance @depth20@1000ms combinado sobre Favoritas (Order Book Imbalance + Paredes de liquidez).
/// - Microestructura de Derivados: Funding Rate, Basis %, Open Interest (Δ 24h) y Long/Short Ratio.
/// - Auditoría en vivo del Simulador Aislado de Paper Trading por regla y temporalidad.
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
    private static readonly ConcurrentDictionary<string, OrderBookSnapshot> LiveBooks = new(StringComparer.OrdinalIgnoreCase);

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

        bool useBook = BookUsable(cfg);
        Console.WriteLine($"[VSA Edge Lab v6.0 .NET] Mercado={cfg.Market.ToUpperInvariant()} | Pares={cfg.Symbols.Count} | Favoritas={cfg.Favorites.Count} | TFs={string.Join(",", cfg.Timeframes)} | FiltroHTF={cfg.HardRegimeFilter} | DepthWS={cfg.OrderBookAlerts} | Libro={(useBook ? $"SÍ ({cfg.EdgeBook!.Edges.Count(e => e.Label == "validada")} validadas, {cfg.EdgeBook.Edges.Count(e => e.Label == "candidata")} candidatas)" : "no (modo clásico por par)")}");
        if (!cfg.Thr.Equals("esc", StringComparison.OrdinalIgnoreCase)) Console.WriteLine("[Aviso] El backend solo replica umbrales 'esc'; la web usa '" + cfg.Thr + "'.");

        // v6: conciliar el Paper con lo que pasó mientras el backend no corría (mechas que tocaron stop/objetivo, vencimientos)
        try { await ReconcilePaperAsync(paper, tgToken, tgChat, CancellationToken.None); }
        catch (Exception ex) { Console.Error.WriteLine($"[Paper conciliación] {ex.Message}"); }

        if (testNotify)
        {
            string audit = BuildPaperAuditSummary(paper);
            string bookTxt = useBook
                ? $"Libro de ventajas: {cfg.EdgeBook!.Edges.Count(e => e.Label == "validada")} validadas · {cfg.EdgeBook.Edges.Count(e => e.Label == "candidata")} candidatas · {cfg.EdgeBook.Symbols.Count} pares · {string.Join("+", cfg.EdgeBook.Timeframes).ToUpperInvariant()}"
                : "Sin libro vigente: modo clásico por par";
            await SendTelegramAsync(tgToken, tgChat,
                $"✅ VSA & Orderflow v6.0 Edge Lab (.NET 8 WebSockets) operativo.\nMercado: {cfg.Market.ToUpperInvariant()} · {bookTxt}\nWebSockets: !ticker@arr + @depth20@1000ms · Filtro HTF: {(cfg.HardRegimeFilter ? "ON" : "OFF")}\n{audit}");
        }

        using var cts = loopSeconds > 0
            ? new CancellationTokenSource(TimeSpan.FromSeconds(loopSeconds))
            : new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        // 1. WebSocket de Profundidad de Libro (@depth20@1000ms) para Favoritas
        var depthTask = Task.Run(async () =>
        {
            try
            {
                await RunOrderBookWebSocketLoopAsync(cfg, state, tgToken, tgChat, cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Console.Error.WriteLine($"[DepthWS error] {ex.Message}"); }
        }, cts.Token);

        // 2. Evaluación de señales confirmadas (con HTF + Funding/OI + OBI) en paralelo
        var signalTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(2500, cts.Token); // dar margen a que lleguen los primeros frames de @depth20
                if (useBook) await CheckEdgeBookSignalsAsync(cfg, state, paper, tgToken, tgChat, cts.Token);
                else await CheckConfirmedSignalsAsync(cfg, state, paper, tgToken, tgChat, cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Console.Error.WriteLine($"[Señales error] {ex.Message}"); }
        }, cts.Token);

        // 3. WebSocket principal de precios (!ticker@arr) para alertas y Paper Trading sub-segundo
        try
        {
            await RunTickerWebSocketLoopAsync(cfg, state, paper, tgToken, tgChat, cts.Token);
        }
        catch (OperationCanceledException) { }

        await Task.WhenAny(Task.WhenAll(signalTask, depthTask), Task.Delay(4000));

        lock (paper)
        {
            paper.Curve.Add(new EquityPoint { T = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Eq = paper.TotalEquity });
            if (paper.Curve.Count > 2000) paper.Curve.RemoveRange(0, paper.Curve.Count - 2000);
        }
        SaveJson(statePath, state);
        SaveJson(paperPath, paper);
        Console.WriteLine($"[VSA Edge Lab v6.0 .NET] Finalizado. {BuildPaperAuditSummary(paper)}");
        return 0;
    }

    private static bool BookUsable(AppConfig cfg)
    {
        var b = cfg.EdgeBook;
        if (b == null || b.At <= 0) return false;
        long age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - b.At;
        return b.Market.Equals(cfg.Market, StringComparison.OrdinalIgnoreCase) && age < 14L * 86_400_000L && b.Timeframes.Count > 0 && b.Symbols.Count > 0;
    }

    private static string BuildPaperAuditSummary(PaperState paper)
    {
        int n = paper.Closed.Count;
        int wins = paper.Closed.Count(x => x.NetUsd > 0);
        double wr = n > 0 ? (double)wins / n * 100.0 : 0.0;
        double avgR = n > 0 ? paper.Closed.Average(x => x.RMultiple) : 0.0;
        double sdR = n > 1 ? Math.Sqrt(paper.Closed.Sum(x => (x.RMultiple - avgR) * (x.RMultiple - avgR)) / (n - 1)) : double.NaN;
        double tR = n > 1 && sdR > 0 ? avgR / (sdR / Math.Sqrt(n)) : double.NaN;
        double gw = paper.Closed.Where(x => x.NetUsd > 0).Sum(x => x.NetUsd), gl = -paper.Closed.Where(x => x.NetUsd < 0).Sum(x => x.NetUsd);
        double pf = gl > 0 ? gw / gl : (gw > 0 ? double.PositiveInfinity : double.NaN);
        double pk = paper.InitialCapital, mdd = 0;
        foreach (var pt in paper.Curve) { if (pt.Eq > pk) pk = pt.Eq; if (pk > 0) mdd = Math.Min(mdd, pt.Eq / pk - 1); }
        double pnl = paper.TotalEquity - paper.InitialCapital;
        // v6: con N=20 el error estándar de la media en R es ~0,25R; se exige N ≥ 30 y t ≥ 1,645 (95 % unilateral)
        bool ready = n >= 30 && double.IsFinite(tR) && tR >= 1.645 && avgR > 0 && (double.IsPositiveInfinity(pf) || pf >= 1.2) && mdd > -0.15;
        string readiness = ready ? "APTA REAL" : $"Incubación ({n}/30)";
        string tTxt = double.IsFinite(tR) ? tR.ToString("0.00", CultureInfo.InvariantCulture) : "—";
        return $"🧪 Paper Audit: {paper.TotalEquity:N2} USDT ({pnl:+0.00;-0.00}) | Abiertas={paper.Open.Count} | Cerradas={n} (WR {wr:0.0}%, Exp {avgR:+0.00;-0.00}R, t {tTxt}, MDD {mdd * 100:0.0}%) | Estado: {readiness}";
    }

    private static async Task RunOrderBookWebSocketLoopAsync(
        AppConfig cfg, MonitorState state, string tgToken, string tgChat, CancellationToken ct)
    {
        var favs = cfg.Favorites.Take(12).Select(s => s.ToLowerInvariant()).Distinct().ToList();
        if (favs.Count == 0) return;

        bool isFutures = cfg.Market.Equals("futures", StringComparison.OrdinalIgnoreCase);
        string streams = string.Join("/", favs.Select(s => $"{s}@depth20@1000ms"));
        var wsUri = new Uri(isFutures
            ? $"wss://fstream.binance.com/stream?streams={streams}"
            : $"wss://stream.binance.com:9443/stream?streams={streams}");

        int backoffMs = 1500;
        var buffer = new byte[65536];

        while (!ct.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            try
            {
                Console.WriteLine($"[DepthWS] Conectando a @depth20@1000ms ({favs.Count} favoritas)...");
                await ws.ConnectAsync(wsUri, ct);
                backoffMs = 1500;

                using var ms = new MemoryStream();
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    ms.SetLength(0);
                    WebSocketReceiveResult res;
                    do
                    {
                        res = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                        if (res.MessageType == WebSocketMessageType.Close) break;
                        ms.Write(buffer, 0, res.Count);
                    } while (!res.EndOfMessage);

                    if (res.MessageType == WebSocketMessageType.Close) break;
                    if (ms.Length == 0) continue;

                    await ProcessDepthFrameAsync(ms.ToArray(), cfg, state, tgToken, tgChat);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DepthWS reconexión en {backoffMs}ms] {ex.Message}");
                await Task.Delay(backoffMs, ct);
                backoffMs = Math.Min(30_000, backoffMs * 2);
            }
        }
    }

    private static async Task ProcessDepthFrameAsync(
        byte[] payload, AppConfig cfg, MonitorState state, string tgToken, string tgChat)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        if (!root.TryGetProperty("stream", out var stEl) || !root.TryGetProperty("data", out var data)) return;
        string streamName = stEl.GetString() ?? "";
        int atIdx = streamName.IndexOf('@');
        if (atIdx <= 0) return;
        string sym = streamName[..atIdx].ToUpperInvariant();

        JsonElement bidsEl = data.TryGetProperty("bids", out var b1) ? b1 : (data.TryGetProperty("b", out var b2) ? b2 : default);
        JsonElement asksEl = data.TryGetProperty("asks", out var a1) ? a1 : (data.TryGetProperty("a", out var a2) ? a2 : default);
        if (bidsEl.ValueKind != JsonValueKind.Array || asksEl.ValueKind != JsonValueKind.Array) return;

        double bidUsd = 0, askUsd = 0, bestBid = 0, bestAsk = 0, bwPx = 0, bwUsd = 0, awPx = 0, awUsd = 0;
        foreach (var row in bidsEl.EnumerateArray())
        {
            double p = ParseD(row[0]), q = ParseD(row[1]), u = p * q;
            if (bestBid == 0) bestBid = p;
            bidUsd += u;
            if (u > bwUsd) { bwUsd = u; bwPx = p; }
        }
        foreach (var row in asksEl.EnumerateArray())
        {
            double p = ParseD(row[0]), q = ParseD(row[1]), u = p * q;
            if (bestAsk == 0) bestAsk = p;
            askUsd += u;
            if (u > awUsd) { awUsd = u; awPx = p; }
        }
        double tot = bidUsd + askUsd;
        if (tot <= 0 || bestBid <= 0 || bestAsk <= 0) return;

        double obi = (bidUsd - askUsd) / tot;
        double mid = (bestBid + bestAsk) / 2.0;
        double spreadBps = (bestAsk - bestBid) / mid * 10000.0;
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var snap = new OrderBookSnapshot(sym, obi, spreadBps, bidUsd, askUsd, bwPx, bwUsd, awPx, awUsd, nowMs);
        LiveBooks[sym] = snap;

        // Alerta institucional de desequilibrio extremo en libro (cooldown 30 min por símbolo)
        if (cfg.OrderBookAlerts && Math.Abs(obi) >= cfg.ObiAlertThreshold && tot >= 250_000)
        {
            long prev = state.ObiAlerts.GetValueOrDefault(sym, 0);
            if (nowMs - prev >= 30 * 60 * 1000)
            {
                state.ObiAlerts[sym] = nowMs;
                bool bull = obi > 0;
                string wallTxt = bull
                    ? $"Pared Bid: {QuantEngine.FormatPrice(bwPx)} ({bwUsd / 1e6:0.00}M $)"
                    : $"Pared Ask: {QuantEngine.FormatPrice(awPx)} ({awUsd / 1e6:0.00}M $)";
                Console.WriteLine($"[OBI EXTREMO] {sym} OBI={obi * 100:+0.0;-0.0}%");
                await SendTelegramAsync(tgToken, tgChat,
                    $"🏛️ LIBRO DE ÓRDENES EXTREMO · ★ {sym} ({cfg.Market.ToUpperInvariant()})\nOBI Top 20: {obi * 100:+0.0;-0.0}% ({(bull ? "presión compradora" : "presión vendedora")})\n{wallTxt} · Spread: {spreadBps:0.00} bps");
            }
        }
    }

    private static async Task<DerivativesSnapshot?> FetchDerivativesAsync(string sym, CancellationToken ct)
    {
        try
        {
            using var premResp = await Http.GetAsync($"https://fapi.binance.com/fapi/v1/premiumIndex?symbol={Uri.EscapeDataString(sym)}", ct);
            if (!premResp.IsSuccessStatusCode) return null;
            using var premDoc = await JsonDocument.ParseAsync(await premResp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var pr = premDoc.RootElement;
            double funding = pr.TryGetProperty("lastFundingRate", out var frEl) ? ParseD(frEl) : 0;
            double mark = pr.TryGetProperty("markPrice", out var mkEl) ? ParseD(mkEl) : 0;
            double idxP = pr.TryGetProperty("indexPrice", out var ixEl) ? ParseD(ixEl) : 0;
            double basis = (mark > 0 && idxP > 0) ? (mark - idxP) / idxP : 0;

            double oiUsd = 0, oiChg24h = 0, lsRatio = 0;
            try
            {
                using var oiResp = await Http.GetAsync($"https://fapi.binance.com/futures/data/openInterestHist?symbol={Uri.EscapeDataString(sym)}&period=1h&limit=25", ct);
                if (oiResp.IsSuccessStatusCode)
                {
                    using var oiDoc = await JsonDocument.ParseAsync(await oiResp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                    var arr = oiDoc.RootElement;
                    int len = arr.GetArrayLength();
                    if (len > 0)
                    {
                        oiUsd = ParseD(arr[len - 1].GetProperty("sumOpenInterestValue"));
                        if (len >= 24)
                        {
                            double oi0 = ParseD(arr[0].GetProperty("sumOpenInterestValue"));
                            if (oi0 > 0) oiChg24h = oiUsd / oi0 - 1.0;
                        }
                    }
                }
            }
            catch { }

            try
            {
                using var lsResp = await Http.GetAsync($"https://fapi.binance.com/futures/data/globalLongShortAccountRatio?symbol={Uri.EscapeDataString(sym)}&period=1h&limit=1", ct);
                if (lsResp.IsSuccessStatusCode)
                {
                    using var lsDoc = await JsonDocument.ParseAsync(await lsResp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                    var arr = lsDoc.RootElement;
                    if (arr.GetArrayLength() > 0)
                        lsRatio = ParseD(arr[0].GetProperty("longShortRatio"));
                }
            }
            catch { }

            string squeeze = (funding <= -0.0002 && oiChg24h > 0.02) ? "⚡ Short Squeeze"
                           : (funding >= 0.0004 && oiChg24h > 0.02) ? "⚠ Long Squeeze"
                           : "Equilibrio";

            return new DerivativesSnapshot(funding, basis, oiUsd, oiChg24h, lsRatio, squeeze);
        }
        catch { return null; }
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
                if (!(up ? px >= alert.Target : px <= alert.Target)) continue;

                state.FiredAlerts[key] = nowMs;
                string msg = $"{(up ? "🚨📈 SUBIÓ" : "🚨📉 BAJÓ")} {sym} ({cfg.Market.ToUpperInvariant()})\nPrecio WebSocket: {QuantEngine.FormatPrice(px)}\nObjetivo {(up ? "≥" : "≤")} {QuantEngine.FormatPrice(alert.Target)}";
                Console.WriteLine($"[ALERTA PRECIO WS] {sym} @ {px}");
                await SendTelegramAsync(tgToken, tgChat, msg);
            }

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
                if (hitSl || hitTp || nowMs >= pos.ExpiresAt) toClose.Add(pos);
            }
        }

        foreach (var pos in toClose)
        {
            bool hitSl = pos.Side > 0 ? px <= pos.Sl : px >= pos.Sl;
            bool hitTp = pos.Side > 0 ? px >= pos.Tp : px <= pos.Tp;
            string reason = hitSl ? "STOP" : hitTp ? "OBJETIVO" : "TIEMPO";
            double exitPx = hitSl ? pos.Sl : hitTp ? pos.Tp : px;
            await ClosePaperAsync(paper, pos, reason, exitPx, nowMs, tgToken, tgChat);
        }
    }

    private static async Task ClosePaperAsync(PaperState paper, PaperPosition pos, string reason, double exitPx, long closedAt, string tgToken, string tgChat)
    {
        double grossUsd = pos.Qty * pos.Side * (exitPx - pos.Entry);
        double feesUsd = pos.Qty * pos.CostSide * (pos.Entry + exitPx);
        double netUsd = grossUsd - feesUsd;
        double netPct = pos.Side * (exitPx - pos.Entry) / pos.Entry - 2.0 * pos.CostSide;
        double rMult = pos.Risk > 0 ? netPct / (pos.Risk / pos.Entry) : 0;
        lock (paper)
        {
            if (!paper.Open.Remove(pos)) return;
            paper.Cash += netUsd;
            paper.Closed.Insert(0, new PaperClosedTrade
            {
                Id = pos.Id, OpenedAt = pos.OpenedAt, ClosedAt = closedAt,
                Sym = pos.Sym, Market = pos.Market, Tf = pos.Tf, Rule = pos.Rule, Side = pos.Side,
                Entry = pos.Entry, Exit = exitPx, Reason = reason, NetUsd = netUsd, NetPct = netPct, RMultiple = rMult, Src = pos.Src
            });
            if (paper.Closed.Count > 500) paper.Closed.RemoveRange(500, paper.Closed.Count - 500);
            paper.Curve.Add(new EquityPoint { T = closedAt, Eq = paper.TotalEquity });
        }
        Console.WriteLine($"[PAPER CERRADO] {pos.Sym} ({reason}) {netUsd:+0.00;-0.00} USDT ({rMult:+0.00;-0.00}R)");
        await SendTelegramAsync(tgToken, tgChat,
            $"🧪 PAPER CERRADO ({reason}) · {pos.Sym} {pos.Tf.ToUpperInvariant()} · {pos.Src}\n{(pos.Side > 0 ? "LONG" : "SHORT")} {pos.Rule}: {QuantEngine.FormatPrice(pos.Entry)} → {QuantEngine.FormatPrice(exitPx)}\nResultado: {netUsd:+0.00;-0.00} USDT ({rMult:+0.00;-0.00}R)\n{BuildPaperAuditSummary(paper)}");
    }

    /// <summary>
    /// v6: concilia cada posición abierta con velas de 1m (5m/15m si el hueco es largo) desde la última vela revisada.
    /// Detecta stops/objetivos tocados por mechas entre corridas de GitHub Actions, gaps a través del stop y vencimientos.
    /// Si una vela toca stop y objetivo a la vez, cuenta el stop (conservador, igual que el backtest).
    /// </summary>
    private static async Task ReconcilePaperAsync(PaperState paper, string tgToken, string tgChat, CancellationToken ct)
    {
        List<PaperPosition> open;
        lock (paper) open = paper.Open.ToList();
        foreach (var pos in open)
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long from = Math.Max(pos.Chk > 0 ? pos.Chk : pos.OpenedAt, pos.OpenedAt);
            bool done = false;
            for (int page = 0; page < 6 && !done; page++)
            {
                long gap = nowMs - from;
                string iv = gap <= 16 * 3_600_000L ? "1m" : gap <= (long)(3.4 * 86_400_000L) ? "5m" : "15m";
                long ivMs = QuantEngine.TfToMs(iv);
                var rows = await FetchKlinesFromAsync(pos.Market, pos.Sym, iv, from / ivMs * ivMs, 1000, ct);
                if (rows.Count == 0) break;
                foreach (var k in rows)
                {
                    if (k.OpenTime < pos.OpenedAt) continue; // la vela que contiene la entrada tiene precios previos
                    bool closed = k.CloseTime < nowMs, lng = pos.Side > 0;
                    if (lng ? k.Open <= pos.Sl : k.Open >= pos.Sl) { await ClosePaperAsync(paper, pos, "STOP", k.Open, k.OpenTime, tgToken, tgChat); done = true; break; }
                    if (lng ? k.Open >= pos.Tp : k.Open <= pos.Tp) { await ClosePaperAsync(paper, pos, "OBJETIVO", k.Open, k.OpenTime, tgToken, tgChat); done = true; break; }
                    bool hitSl = lng ? k.Low <= pos.Sl : k.High >= pos.Sl, hitTp = lng ? k.High >= pos.Tp : k.Low <= pos.Tp;
                    if (hitSl) { await ClosePaperAsync(paper, pos, "STOP", pos.Sl, k.OpenTime, tgToken, tgChat); done = true; break; }
                    if (hitTp) { await ClosePaperAsync(paper, pos, "OBJETIVO", pos.Tp, k.OpenTime, tgToken, tgChat); done = true; break; }
                    if (closed && pos.ExpiresAt > 0 && k.CloseTime + 1 >= pos.ExpiresAt) { await ClosePaperAsync(paper, pos, "TIEMPO", k.Close, k.CloseTime + 1, tgToken, tgChat); done = true; break; }
                    if (closed) pos.Chk = k.CloseTime + 1;
                    pos.LastPrice = k.Close;
                    pos.UnrealizedUsd = pos.Qty * (pos.Side * (k.Close - pos.Entry) - pos.CostSide * (pos.Entry + k.Close));
                }
                if (rows.Count < 1000) break;
                from = Math.Max(from + ivMs, pos.Chk);
            }
        }
    }

    private static async Task<List<Candle>> FetchKlinesFromAsync(string market, string sym, string tf, long startTime, int limit, CancellationToken ct)
    {
        var hosts = market == "futures" ? FuturesHosts : SpotHosts;
        string path = market == "futures" ? "/fapi/v1/klines" : "/api/v3/klines";
        foreach (string host in hosts)
        {
            string url = $"{host}{path}?symbol={Uri.EscapeDataString(sym)}&interval={Uri.EscapeDataString(tf)}&limit={limit}&startTime={startTime}";
            try
            {
                using var resp = await Http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode) continue;
                using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                return ParseKlines(doc.RootElement);
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }
        return new List<Candle>();
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

                    string htf = QuantEngine.HigherTimeframeFor(tf, cfg.RegimeHtf);
                    string htfKey = $"{sym}|{htf}";
                    if (!htfCache.TryGetValue(htfKey, out var htfRegime))
                    {
                        var htfCandles = htf == tf ? candles : await FetchHistoryAsync(market, sym, htf, 300, ct);
                        htfRegime = QuantEngine.EvaluateRegime(htfCandles.Where(k => k.CloseTime < nowMs).ToList());
                        htfCache[htfKey] = htfRegime;
                    }

                    var hit = QuantEngine.EvaluateLatestConfirmed(
                        closed, htfRegime, sym, tf, costRt, cfg.Kfine,
                        cfg.HardRegimeFilter, cfg.StrictLateral, cfg.SigAlert, cfg.Rvol);

                    if (hit == null) continue;

                    double px = LivePrices.TryGetValue(sym, out double livePx) ? livePx : lastCandle.Close;
                    double risk = Math.Max(1.3 * hit.Atr, px * 0.0035);
                    double sl = px - hit.Bias * risk;
                    double tp = px + hit.Bias * 2.0 * risk;
                    bool up = hit.Bias > 0;
                    bool isFav = cfg.Favorites.Any(f => f.Equals(sym, StringComparison.OrdinalIgnoreCase));

                    // Enriquecer señal con Microestructura (Funding/OI + Order Book OBI)
                    var deriv = await FetchDerivativesAsync(sym, ct);
                    string microLine = deriv.HasValue
                        ? $"\n🏛️ Derivados: Funding {deriv.Value.FundingRate * 100:+0.000;-0.000}% · OI 24h {deriv.Value.OiChange24hPct * 100:+0.0;-0.0}% · L/S {deriv.Value.LongShortRatio:0.00} ({deriv.Value.SqueezeStatus})"
                        : "";
                    string obLine = LiveBooks.TryGetValue(sym, out var ob)
                        ? $"\n📚 Libro Top 20: OBI {ob.Obi * 100:+0.0;-0.0}% · BidWall {QuantEngine.FormatPrice(ob.BidWallPrice)} · AskWall {QuantEngine.FormatPrice(ob.AskWallPrice)}"
                        : "";

                    string paperNote = "";
                    if (cfg.PaperTrading.Enabled && (up || market == "futures"))
                    {
                        bool opened = TryOpenPaperPosition(paper, cfg.PaperTrading, sym, market, tf, hit.Code, hit.Bias, px, sl, tp, risk, costSide, Math.Max(12, hit.Horizon), "par");
                        if (opened) paperNote = $"\n🧪 Paper Trading: Ejecutado (Balance {paper.TotalEquity:N2} USDT)";
                    }

                    string msg =
                        $"{(up ? "🚨📈 COMPRA CONFIRMADA" : "🚨📉 VENTA CONFIRMADA")} {(isFav ? "★ " : "")}· {sym} ({market.ToUpperInvariant()}) · {tf.ToUpperInvariant()}\n" +
                        $"Regla: {hit.Code} {hit.Name}\n" +
                        $"Régimen HTF ({htf.ToUpperInvariant()}): {hit.HtfRegime.Label} ({hit.HtfRegime.SpreadPct * 100:+0.00;-0.00}%) · Hurst: {(double.IsFinite(hit.Hurst) ? hit.Hurst.ToString("0.00", CultureInfo.InvariantCulture) : "—")}\n" +
                        $"Evidencia (h={hit.Horizon}v): n={hit.SampleCount} · acierto {hit.HitRate * 100:0.0}% · neto {hit.NetReturn * 100:+0.00;-0.00}% · p={hit.PValue:0.0000} (BH✓)\n" +
                        $"Ref: {QuantEngine.FormatPrice(px)} · SL: {QuantEngine.FormatPrice(sl)} · Obj 2R: {QuantEngine.FormatPrice(tp)}{microLine}{obLine}{paperNote}";

                    Console.WriteLine($"[SEÑAL CONFIRMADA v5] {sym} {tf} {hit.Code}");
                    await SendTelegramAsync(tgToken, tgChat, msg);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Señal {sym} {tf}] {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// v6 · modo libro: recorre los pares y TFs del libro y, cuando cierra una vela nueva, evalúa SOLO las reglas validadas
    /// fuera de muestra en el universo (alerta + Paper) y, si el libro lo permite, las candidatas (solo Paper, sin alerta).
    /// Usa la salida exacta con la que se validó cada ventaja (stop ATR, objetivo R, tope de velas).
    /// </summary>
    private static async Task CheckEdgeBookSignalsAsync(AppConfig cfg, MonitorState state, PaperState paper, string tgToken, string tgChat, CancellationToken ct)
    {
        var book = cfg.EdgeBook!;
        string market = book.Market.Equals("futures", StringComparison.OrdinalIgnoreCase) ? "futures" : "spot";
        double baseFee = market == "futures" ? 0.0004 : 0.0010, slip = 0.0002;
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int nVal = book.Edges.Count(e => e.Label == "validada"), nCand = book.Edges.Count(e => e.Label == "candidata");
        if (nVal == 0 && !(book.PaperCandidates && nCand > 0))
        {
            Console.WriteLine("[Libro] Sin ventajas validadas (ni candidatas para Paper): no se emiten señales. No operar también protege el capital.");
            return;
        }
        var syms = book.Symbols.Distinct(StringComparer.OrdinalIgnoreCase).Take(60).ToList();
        var htfCache = new Dictionary<string, RegimeResult>(StringComparer.OrdinalIgnoreCase);
        int checkedN = 0, hitsN = 0;
        foreach (string sym in syms)
        {
            if (ct.IsCancellationRequested) break;
            foreach (string tf in book.Timeframes)
            {
                if (ct.IsCancellationRequested) break;
                if (!book.Edges.Any(e => e.Tf == tf && (e.Label == "validada" || (book.PaperCandidates && e.Label == "candidata")))) continue;
                try
                {
                    var candles = await FetchHistoryAsync(market, sym, tf, tf == "15m" ? 2000 : 1000, ct);
                    var closed = candles.Where(k => k.CloseTime < nowMs).ToList();
                    if (closed.Count < 200) continue;
                    var last = closed[^1];
                    string key = $"book|{market}|{sym}|{tf}";
                    if (state.Candles.TryGetValue(key, out long prevT) && prevT == last.OpenTime) continue;
                    state.Candles[key] = last.OpenTime;
                    long tfMs = QuantEngine.TfToMs(tf), entryT = last.CloseTime + 1;
                    if (nowMs - entryT > tfMs / 2) continue; // la vela de entrada ya pasó la mitad: señal tardía (la evidencia supone entrar en su apertura)
                    checkedN++;
                    var fa = QuantEngine.Analyze(closed, tf, book.Rvol);
                    string htf = QuantEngine.HigherTimeframeFor(tf, book.RegimeHtf);
                    RegimeResult reg = new(0, "SIN FILTRO", double.NaN, false);
                    if (book.HardRegime)
                    {
                        string hk = $"{sym}|{htf}";
                        if (!htfCache.TryGetValue(hk, out reg))
                        {
                            var hc = await FetchHistoryAsync(market, sym, htf, 1000, ct);
                            reg = QuantEngine.EvaluateRegime(hc.Where(k => k.CloseTime < nowMs).ToList());
                            htfCache[hk] = reg;
                        }
                    }
                    var hits = QuantEngine.EvaluateEdgeBook(fa, book, tf, reg, book.PaperCandidates);
                    if (hits.Count == 0) continue;
                    hitsN++;
                    var top = hits[0];
                    bool isVal = top.Edge.Label == "validada", up = top.Bias > 0, tradable = up || market == "futures";
                    double px = LivePrices.TryGetValue(sym, out double livePx) ? livePx : last.Close;
                    double half = LiveBooks.TryGetValue(sym, out var obk) && double.IsFinite(obk.SpreadBps) ? obk.SpreadBps / 2.0 / 10000.0 : 0;
                    double costSide = baseFee + Math.Max(slip, half);
                    double atr = fa.Atr[^1], risk = Math.Max(book.SlAtr * atr, px * 0.0035);
                    if (!(risk > 0) || !(px > 0)) continue;
                    double sl = px - top.Bias * risk, tp = px + top.Bias * book.Rr * risk;
                    bool isFav = cfg.Favorites.Any(f => f.Equals(sym, StringComparison.OrdinalIgnoreCase));

                    string paperNote = "";
                    if (cfg.PaperTrading.Enabled && tradable && (isVal || book.PaperCandidates))
                    {
                        bool opened = TryOpenPaperPosition(paper, cfg.PaperTrading, sym, market, tf, top.Code, top.Bias, px, sl, tp, risk, costSide, book.MaxBars,
                            isVal ? "universo" : "candidata", entryT + book.MaxBars * tfMs);
                        if (opened) paperNote = $"\n🧪 Paper ({(isVal ? "validada" : "candidata")}): ejecutado · Balance {paper.TotalEquity:N2} USDT";
                    }
                    Console.WriteLine($"[LIBRO {(isVal ? "VALIDADA" : "candidata")}] {sym} {tf} {top.Code}{paperNote.Replace("\n", " ")}");
                    if (!isVal || !cfg.SigAlertOn) continue;
                    if (cfg.SigAlert == "BUY" && !up) continue;
                    if (cfg.SigAlert == "SELL" && up) continue;

                    var e = top.Edge;
                    string f2(double? v) => v.HasValue && double.IsFinite(v.Value) ? v.Value.ToString("+0.00;-0.00", CultureInfo.InvariantCulture) : "—";
                    string headTxt = !tradable ? "🚪 SALIDA VALIDADA (si tenés " + sym.Replace("USDT", "") + ")" : up ? "🎯📈 COMPRA VALIDADA (universo)" : "🎯📉 VENTA VALIDADA (universo)";
                    string msg =
                        $"{headTxt} {(isFav ? "★ " : "")}· {sym} ({market.ToUpperInvariant()}) · {tf.ToUpperInvariant()}\n" +
                        $"Regla: {top.Code} {top.Name}\n" +
                        $"Universo ({e.NSym} pares): fuera de muestra {f2(e.E)}R/op (t {(e.TTest ?? double.NaN).ToString("0.0", CultureInfo.InvariantCulture)}, n {e.NTest}) · entren. {f2(e.ETrain)}R (n {e.NTrain})" + (e.Hit.HasValue ? $" · acierto {e.Hit.Value * 100:0}%" : "") + "\n" +
                        $"Régimen HTF ({htf.ToUpperInvariant()}): {reg.Label}" + (double.IsFinite(reg.SpreadPct) ? $" ({reg.SpreadPct * 100:+0.00;-0.00}%)" : "") + "\n" +
                        (tradable ? $"Ref: {QuantEngine.FormatPrice(px)} · SL: {QuantEngine.FormatPrice(sl)} · Obj {book.Rr:0.0}R: {QuantEngine.FormatPrice(tp)} · tope {book.MaxBars} velas" : "Spot: la evidencia sugiere salir o reducir; si no tenés la moneda, no comprar ahora.") + paperNote;
                    await SendTelegramAsync(tgToken, tgChat, msg);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Console.Error.WriteLine($"[Libro {sym} {tf}] {ex.Message}"); }
            }
        }
        Console.WriteLine($"[Libro] Velas nuevas evaluadas: {checkedN} · con señal del libro: {hitsN}");
    }

    private static bool TryOpenPaperPosition(
        PaperState paper, PaperConfig pcfg, string sym, string market, string tf,
        string ruleCode, int side, double entry, double sl, double tp, double risk, double costSide, int maxBars, string src = "par", long expiresAt = 0)
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
            // v6: tope AGREGADO (antes cada posición podía usar el 100 % del capital: 5 posiciones Spot = 5× el capital)
            double used = paper.Open.Sum(p => p.Market == "futures" ? p.Qty * p.Entry / Math.Max(1.0, pcfg.Leverage) : p.Qty * p.Entry);
            double maxNot = Math.Max(0, eq - used) * lev;
            if (maxNot < eq * 0.02) return false;
            double qty = riskUsd / perUnit;
            if (qty * entry > maxNot) qty = maxNot / entry;

            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            paper.Open.Insert(0, new PaperPosition
            {
                Id = $"{nowMs}-{sym}-{tf}", OpenedAt = nowMs, Sym = sym, Market = market, Tf = tf,
                Rule = ruleCode, Side = side, Entry = entry, LastPrice = entry, Sl = sl, Tp = tp,
                Risk = risk, Qty = qty, CostSide = costSide,
                ExpiresAt = expiresAt > 0 ? expiresAt : nowMs + maxBars * QuantEngine.TfToMs(tf),
                UnrealizedUsd = -qty * costSide * entry, Chk = nowMs, Src = src
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
                catch { }
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
            double o = ParseD(row[1]), h = ParseD(row[2]), l = ParseD(row[3]), c = ParseD(row[4]), v = ParseD(row[5]);
            long ct = row[6].GetInt64();
            double qv = ParseD(row[7]);
            int tr = row[8].GetInt32();
            double tb = ParseD(row[9]), tq = ParseD(row[10]);
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
            if (!resp.IsSuccessStatusCode) Console.Error.WriteLine($"[Telegram HTTP {(int)resp.StatusCode}]");
        }
        catch (Exception ex) { Console.Error.WriteLine($"[Telegram error] {ex.Message}"); }
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
