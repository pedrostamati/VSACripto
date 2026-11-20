using System.Globalization;

namespace VsaMonitor;

/// <summary>
/// Motor cuantitativo QuantPulse + VSA &amp; Orderflow v4.0 en C# .NET 8.
/// Garantías estadísticas:
/// - Solo velas cerradas, indicadores estrictamente causales (cero lookahead).
/// - Filtro Duro de Régimen HTF (EMA 50 vs EMA 200 en temporalidad superior).
/// - Triggers adaptativos por compresión de volatilidad (RSI dinámico y z-score de flujo).
/// - Permutación circular adaptativa en 2 etapas + control Benjamini-Hochberg (FDR 10%).
/// </summary>
public static class QuantEngine
{
    public static readonly int[] Horizons = { 1, 3, 6, 12, 24 };

    public static long TfToMs(string tf) => tf switch
    {
        "1m" => 60_000L,
        "3m" => 180_000L,
        "5m" => 300_000L,
        "15m" => 900_000L,
        "30m" => 1_800_000L,
        "1h" => 3_600_000L,
        "4h" => 14_400_000L,
        "12h" => 43_200_000L,
        "1d" => 86_400_000L,
        "1w" => 604_800_000L,
        "1M" => 2_592_000_000L,
        _ => 3_600_000L
    };

    public static string HigherTimeframeFor(string tf, string overrideHtf)
    {
        if (!string.IsNullOrWhiteSpace(overrideHtf) &&
            !overrideHtf.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return overrideHtf;
        }
        return tf switch
        {
            "1m" => "15m",
            "3m" => "30m",
            "5m" => "1h",
            "15m" => "4h",
            "30m" => "4h",
            "1h" => "4h",
            "4h" => "1d",
            "12h" => "1w",
            "1d" => "1w",
            "1w" => "1M",
            _ => "4h"
        };
    }

    public static AdaptiveBounds ComputeAdaptiveBounds(double volCompression)
    {
        double vc = double.IsFinite(volCompression) && volCompression > 0
            ? Math.Clamp(volCompression, 0.55, 1.60)
            : 1.0;
        double rsiLow = Math.Clamp(30.0 + (1.0 - vc) * 20.0, 20.0, 42.0);
        double rsiHigh = Math.Clamp(70.0 - (1.0 - vc) * 20.0, 58.0, 80.0);
        double zTrig = Math.Clamp(1.0 * vc, 0.65, 1.55);
        return new AdaptiveBounds(vc, rsiLow, rsiHigh, zTrig);
    }

    public static double[] EmaSeries(IReadOnlyList<double> x, int period)
    {
        int n = x.Count;
        var o = new double[n];
        Array.Fill(o, double.NaN);
        if (n < period) return o;
        double sum = 0;
        for (int i = 0; i < period; i++) sum += x[i];
        double e = sum / period;
        double k = 2.0 / (period + 1.0);
        o[period - 1] = e;
        for (int i = period; i < n; i++)
        {
            e = x[i] * k + e * (1.0 - k);
            o[i] = e;
        }
        return o;
    }

    public static double[] SmaSeries(double[] x, int period)
    {
        int n = x.Length;
        var o = new double[n];
        Array.Fill(o, double.NaN);
        double sum = 0;
        int cnt = 0;
        for (int i = 0; i < n; i++)
        {
            if (double.IsFinite(x[i])) { sum += x[i]; cnt++; }
            if (i >= period && double.IsFinite(x[i - period])) { sum -= x[i - period]; cnt--; }
            if (i >= period - 1 && cnt == period) o[i] = sum / period;
        }
        return o;
    }

    public static double[] AtrSeries(IReadOnlyList<Candle> c, int period)
    {
        int n = c.Count;
        var o = new double[n];
        Array.Fill(o, double.NaN);
        if (n < period) return o;
        var tr = new double[n];
        for (int i = 0; i < n; i++)
        {
            double hl = c[i].High - c[i].Low;
            tr[i] = i == 0
                ? hl
                : Math.Max(hl, Math.Max(Math.Abs(c[i].High - c[i - 1].Close), Math.Abs(c[i].Low - c[i - 1].Close)));
        }
        double sum = 0;
        for (int i = 0; i < period; i++) sum += tr[i];
        double a = sum / period;
        o[period - 1] = a;
        for (int i = period; i < n; i++)
        {
            a = (a * (period - 1) + tr[i]) / period;
            o[i] = a;
        }
        return o;
    }

    public static double[] RsiSeries(IReadOnlyList<double> cl, int period)
    {
        int n = cl.Count;
        var o = new double[n];
        Array.Fill(o, double.NaN);
        if (n <= period) return o;
        double g = 0, l = 0;
        for (int i = 1; i <= period; i++)
        {
            double d = cl[i] - cl[i - 1];
            if (d >= 0) g += d; else l -= d;
        }
        double ag = g / period, al = l / period;
        o[period] = al == 0 ? 100.0 : 100.0 - 100.0 / (1.0 + ag / al);
        for (int i = period + 1; i < n; i++)
        {
            double d = cl[i] - cl[i - 1];
            ag = (ag * (period - 1) + (d > 0 ? d : 0)) / period;
            al = (al * (period - 1) + (d < 0 ? -d : 0)) / period;
            o[i] = al == 0 ? 100.0 : 100.0 - 100.0 / (1.0 + ag / al);
        }
        return o;
    }

    public static double HurstRS(IReadOnlyList<double> cl, int idx, int window = 100)
    {
        if (idx < window) return double.NaN;
        var rets = new List<double>(window);
        for (int j = idx - window + 1; j <= idx; j++)
        {
            if (cl[j] > 0 && cl[j - 1] > 0)
                rets.Add(Math.Log(cl[j] / cl[j - 1]));
        }
        if (rets.Count < 40) return double.NaN;
        int[] sizes = { 10, 20, 40, 80 };
        var lx = new List<double>();
        var ly = new List<double>();
        foreach (int sz in sizes)
        {
            int chunks = rets.Count / sz;
            if (chunks < 1) continue;
            double rsSum = 0;
            int rsCnt = 0;
            for (int ch = 0; ch < chunks; ch++)
            {
                int off = ch * sz;
                double m = 0;
                for (int k = 0; k < sz; k++) m += rets[off + k];
                m /= sz;
                double cum = 0, mn = 0, mx = 0, v2 = 0;
                for (int k = 0; k < sz; k++)
                {
                    double d = rets[off + k] - m;
                    cum += d;
                    v2 += d * d;
                    if (cum < mn) mn = cum;
                    if (cum > mx) mx = cum;
                }
                double sd = Math.Sqrt(v2 / sz);
                if (sd > 1e-12 && mx > mn)
                {
                    rsSum += (mx - mn) / sd;
                    rsCnt++;
                }
            }
            if (rsCnt > 0)
            {
                lx.Add(Math.Log(sz));
                ly.Add(Math.Log(rsSum / rsCnt));
            }
        }
        if (lx.Count < 2) return double.NaN;
        int n = lx.Count;
        double sx = 0, sy = 0, sxy = 0, sxx = 0;
        for (int i = 0; i < n; i++) { sx += lx[i]; sy += ly[i]; sxy += lx[i] * ly[i]; sxx += lx[i] * lx[i]; }
        double den = n * sxx - sx * sx;
        return den != 0 ? Math.Clamp((n * sxy - sx * sy) / den, 0.1, 0.95) : double.NaN;
    }

    public static RegimeResult EvaluateRegime(IReadOnlyList<Candle> htfClosed)
    {
        if (htfClosed.Count < 52) return new RegimeResult(0, "SIN DATOS", double.NaN, false);
        var cl = htfClosed.Select(k => k.Close).ToArray();
        var e50 = EmaSeries(cl, 50);
        var e200 = EmaSeries(cl, 200);
        int last = cl.Length - 1;
        if (double.IsFinite(e50[last]) && double.IsFinite(e200[last]) && e200[last] > 0)
        {
            double sp = (e50[last] - e200[last]) / e200[last];
            int dir = sp > 0.0025 ? 1 : sp < -0.0025 ? -1 : 0;
            return new RegimeResult(dir, dir > 0 ? "ALCISTA" : dir < 0 ? "BAJISTA" : "LATERAL", sp, true);
        }
        var e21 = EmaSeries(cl, 21);
        if (double.IsFinite(e21[last]) && double.IsFinite(e50[last]) && e50[last] > 0)
        {
            double sp = (e21[last] - e50[last]) / e50[last];
            int dir = sp > 0.0025 ? 1 : sp < -0.0025 ? -1 : 0;
            return new RegimeResult(dir, dir > 0 ? "ALCISTA" : dir < 0 ? "BAJISTA" : "LATERAL", sp, true);
        }
        return new RegimeResult(0, "LATERAL", double.NaN, false);
    }

    public static bool RegimeAllows(int bias, RegimeResult regime, string category, bool strictLateral)
    {
        if (bias == 0 || !regime.Ready) return true;
        if (regime.Direction > 0) return bias > 0;
        if (regime.Direction < 0) return bias < 0;
        return !strictLateral && (category == "PROFILE" || category == "ADAPTIVE");
    }

    private sealed class BarFeature
    {
        public double C, O, H, L, V, Atr, DP, DPa, Rvol, SpreadAtr, Clv, UpW, LoW;
        public bool Green, Red, Green1, Red1, Green2, Red2;
        public double O1, C1, C3, V1, V2, E9, E21, E50, Rsi;
        public double AtrRatioPrev, VolComp, RsiLow, RsiHigh, ZTrig;
        public double Move8a, Lo15, Hi15, Lo10, Hi10;
        public double Vwap, VwapU1, VwapD1, VwapU2, VwapD2;
        public int VwapN;
        public double Share, Share1, Share2, ShareZ, Delta, Dr10Z, Ob10Z, PMove10;
    }

    private sealed record RuleDef(int Id, string Code, string Name, string Category, int Bias, Func<BarFeature, bool> Match);

    private static readonly RuleDef[] Rules =
    {
        new(1, "V01", "Deriva bajista por falta de compradores", "LOWVOL", -1, x => x.DPa < -0.25 && x.Rvol < 0.75 && x.Clv < -0.20),
        new(2, "V02", "Subida silenciosa sin oposición", "LOWVOL", 1, x => x.DPa > 0.25 && x.Rvol < 0.75 && x.Clv > 0.20),
        new(3, "V03", "Secado de oferta (No Supply)", "LOWVOL", 1, x => x.Red && x.SpreadAtr < 0.75 && x.Rvol < 0.68 && x.C > x.E21),
        new(4, "V04", "Falta de demanda (No Demand)", "LOWVOL", -1, x => x.Green && x.SpreadAtr < 0.75 && x.Rvol < 0.68 && x.C < x.E21),
        new(5, "V05", "Retroceso con volumen decreciente", "LOWVOL", 1, x => x.E9 > x.E21 && x.DP < 0 && x.V < x.V1 && x.V1 < x.V2),
        new(6, "V06", "Rebote débil con volumen decreciente", "LOWVOL", -1, x => x.E9 < x.E21 && x.DP > 0 && x.V < x.V1 && x.V1 < x.V2),
        new(7, "V07", "Ruptura alcista con volumen", "HIGHVOL", 1, x => x.SpreadAtr > 1.25 && x.Rvol > 1.85 && x.Clv > 0.55),
        new(8, "V08", "Ruptura bajista con volumen", "HIGHVOL", -1, x => x.SpreadAtr > 1.25 && x.Rvol > 1.85 && x.Clv < -0.55),
        new(9, "V09", "Clímax vendedor / capitulación", "HIGHVOL", 1, x => x.Rvol > 2.5 && x.LoW > 0.42 && x.Move8a < -1.0),
        new(10, "V10", "Clímax comprador / euforia", "HIGHVOL", -1, x => x.Rvol > 2.5 && x.UpW > 0.42 && x.Move8a > 1.0),
        new(13, "V13", "Esfuerzo sin resultado alcista", "HIGHVOL", -1, x => x.Rvol > 1.75 && x.Green && Math.Abs(x.DPa) < 0.35),
        new(14, "V14", "Esfuerzo sin resultado bajista", "HIGHVOL", 1, x => x.Rvol > 1.75 && x.Red && Math.Abs(x.DPa) < 0.35),
        new(15, "V15", "Aceleración de volumen alcista (3v)", "HIGHVOL", 1, x => x.Green && x.Green1 && x.Green2 && x.V > x.V1 && x.V1 > x.V2 && x.Rvol > 1.2),
        new(16, "V16", "Aceleración de volumen bajista (3v)", "HIGHVOL", -1, x => x.Red && x.Red1 && x.Red2 && x.V > x.V1 && x.V1 > x.V2 && x.Rvol > 1.2),
        new(17, "V17", "Spring (barrida de soporte)", "WYCKOFF", 1, x => x.L < x.Lo15 && x.C > x.Lo15 && x.Rvol > 1.4),
        new(18, "V18", "Upthrust (barrida de techo)", "WYCKOFF", -1, x => x.H > x.Hi15 && x.C < x.Hi15 && x.Rvol > 1.4),
        new(19, "V19", "Prueba de oferta exitosa", "WYCKOFF", 1, x => x.Rvol < 0.65 && x.C > x.E9 && x.Move8a > 1.25),
        new(20, "V20", "Prueba de demanda fallida", "WYCKOFF", -1, x => x.Rvol < 0.65 && x.C < x.E9 && x.Move8a < -1.25),
        new(21, "V21", "Sacudida bajista intravela", "WYCKOFF", 1, x => x.LoW > 0.52 && x.Clv > 0.35 && x.Rvol > 1.5),
        new(22, "V22", "Rechazo comprador intravela", "WYCKOFF", -1, x => x.UpW > 0.52 && x.Clv < -0.35 && x.Rvol > 1.5),
        new(29, "V29", "Rebote en banda VWAP -1σ", "PROFILE", 1, x => x.VwapN >= 6 && x.L <= x.VwapD1 && x.C > x.VwapD1 && x.Clv > 0.20),
        new(30, "V30", "Rechazo en banda VWAP +1σ", "PROFILE", -1, x => x.VwapN >= 6 && x.H >= x.VwapU1 && x.C < x.VwapU1 && x.Clv < -0.20),
        new(31, "V31", "Divergencia alcista de CVD", "FLOW", 1, x => x.PMove10 < -1.0 && x.Dr10Z > 1.0),
        new(32, "V32", "Divergencia bajista de CVD", "FLOW", -1, x => x.PMove10 > 1.0 && x.Dr10Z < -1.0),
        new(33, "V33", "Divergencia alcista de OBV", "FLOW", 1, x => x.PMove10 < -1.0 && x.Ob10Z > 1.0),
        new(34, "V34", "Divergencia bajista de OBV", "FLOW", -1, x => x.PMove10 > 1.0 && x.Ob10Z < -1.0),
        new(35, "V35", "Barrida de cortos por compras agresivas", "FLOW", 1, x => x.Rvol > 2.0 && x.ShareZ > 2.0 && x.DPa > 1.0),
        new(36, "V36", "Barrida de largos por ventas agresivas", "FLOW", -1, x => x.Rvol > 2.0 && x.ShareZ < -2.0 && x.DPa < -1.0),
        new(37, "V37", "Envolvente alcista con volumen", "FLOW", 1, x => x.Green && x.Red1 && x.C >= x.O1 && x.O <= x.C1 && x.Rvol > 1.35),
        new(38, "V38", "Envolvente bajista con volumen", "FLOW", -1, x => x.Red && x.Green1 && x.C <= x.O1 && x.O >= x.C1 && x.Rvol > 1.35),
        new(41, "V41", "Pullback en tendencia alcista con flujo", "REGIME", 1, x => double.IsFinite(x.E50) && x.E21 > x.E50 && x.C > x.E50 && x.L <= x.E21 + 0.25 * x.Atr && x.Green && x.Clv > 0.25 && x.ShareZ > 0.7),
        new(42, "V42", "Rebote en tendencia bajista con flujo", "REGIME", -1, x => double.IsFinite(x.E50) && x.E21 < x.E50 && x.C < x.E50 && x.H >= x.E21 - 0.25 * x.Atr && x.Red && x.Clv < -0.25 && x.ShareZ < -0.7),
        new(43, "V43", "Ruptura alcista tras compresión (Squeeze)", "REGIME", 1, x => double.IsFinite(x.AtrRatioPrev) && x.AtrRatioPrev < 0.88 && x.C > x.Hi10 && x.Rvol > 1.35 && x.Share > 0.55),
        new(44, "V44", "Ruptura bajista tras compresión (Squeeze)", "REGIME", -1, x => double.IsFinite(x.AtrRatioPrev) && x.AtrRatioPrev < 0.88 && x.C < x.Lo10 && x.Rvol > 1.35 && x.Share < 0.45),
        new(45, "V45", "Reversión extrema en VWAP -2σ con delta", "PROFILE", 1, x => x.VwapN >= 8 && x.L <= x.VwapD2 && x.C > x.VwapD2 && x.Clv > 0.20 && x.Delta > 0),
        new(46, "V46", "Reversión extrema en VWAP +2σ con delta", "PROFILE", -1, x => x.VwapN >= 8 && x.H >= x.VwapU2 && x.C < x.VwapU2 && x.Clv < -0.20 && x.Delta < 0),
        new(49, "V49", "Presión compradora persistente (3v)", "FLOW", 1, x => x.Share > 0.55 && x.Share1 > 0.55 && x.Share2 > 0.55 && x.Rvol > 1.0 && x.C > x.C3),
        new(50, "V50", "Presión vendedora persistente (3v)", "FLOW", -1, x => x.Share < 0.45 && x.Share1 < 0.45 && x.Share2 < 0.45 && x.Rvol > 1.0 && x.C < x.C3),
        // Triggers Adaptativos QuantPulse v4.0 (V53-V56)
        new(53, "V53", "Reversión RSI adaptativo sobrevendido + flujo", "ADAPTIVE", 1, x => double.IsFinite(x.Rsi) && x.Rsi <= x.RsiLow && x.Clv > 0.20 && x.ShareZ > x.ZTrig && x.Rvol > 1.15),
        new(54, "V54", "Reversión RSI adaptativo sobrecomprado + flujo", "ADAPTIVE", -1, x => double.IsFinite(x.Rsi) && x.Rsi >= x.RsiHigh && x.Clv < -0.20 && x.ShareZ < -x.ZTrig && x.Rvol > 1.15),
        new(55, "V55", "Expansión adaptativa alcista en compresión", "ADAPTIVE", 1, x => x.VolComp < 0.95 && double.IsFinite(x.Rsi) && x.Rsi > 54.0 && x.C > x.E21 && x.C > x.Vwap && x.ShareZ > x.ZTrig && x.Rvol > 1.30),
        new(56, "V56", "Expansión adaptativa bajista en compresión", "ADAPTIVE", -1, x => x.VolComp < 0.95 && double.IsFinite(x.Rsi) && x.Rsi < 46.0 && x.C < x.E21 && x.C < x.Vwap && x.ShareZ < -x.ZTrig && x.Rvol > 1.30),
    };

    private static double ZPrev(double[] arr, int i, int window, int minN)
    {
        if (!double.IsFinite(arr[i])) return double.NaN;
        double s = 0, s2 = 0;
        int k = 0;
        for (int j = Math.Max(0, i - window); j < i; j++)
        {
            double x = arr[j];
            if (double.IsFinite(x)) { s += x; s2 += x * x; k++; }
        }
        if (k < minN) return double.NaN;
        double m = s / k;
        double v = (s2 - k * m * m) / (k - 1);
        return v > 0 ? (arr[i] - m) / Math.Sqrt(v) : double.NaN;
    }

    private static long AnchorKey(long openTimeMs, string tf)
    {
        if (tf is "1m" or "3m" or "5m" or "15m" or "30m") return openTimeMs / 86_400_000L;
        if (tf is "1h" or "4h") return ((openTimeMs / 86_400_000L) + 3) / 7;
        var dt = DateTimeOffset.FromUnixTimeMilliseconds(openTimeMs);
        return dt.Year * 12 + dt.Month;
    }

    private static uint HashSeed(string s)
    {
        uint h = 2166136261u;
        foreach (char ch in s) { h ^= ch; h *= 16777619u; }
        return h;
    }

    private static double PermutePValue(double[] src, List<int> ev, int len, int bias, double obsMean, int kRuns, string seedStr)
    {
        var rng = new Random((int)HashSeed(seedStr));
        int ge = 0;
        int en = ev.Count;
        for (int k = 0; k < kRuns; k++)
        {
            int off = 1 + rng.Next(len - 1);
            double acc = 0;
            for (int z = 0; z < en; z++) acc += src[(ev[z] + off) % len];
            double pm = bias * acc / en;
            if (pm >= obsMean - 1e-15) ge++;
        }
        return (1.0 + ge) / (kRuns + 1.0);
    }

    /// <summary>
    /// Evalúa la última vela cerrada con validación estadística completa (permutación 2 etapas + BH global)
    /// y aplica el Filtro Duro de Régimen HTF (EMA50/EMA200) + Triggers Adaptativos.
    /// </summary>
    public static SignalHit? EvaluateLatestConfirmed(
        IReadOnlyList<Candle> closed,
        RegimeResult htfRegime,
        string symbol,
        string tf,
        double costRt,
        int kFine,
        bool hardRegimeFilter,
        bool strictLateral,
        string sigAlertDirection)
    {
        int n = closed.Count;
        int start = n >= 160 ? 60 : 40;
        if (n < start + 60) return null;

        var cl = closed.Select(k => k.Close).ToArray();
        var e9 = EmaSeries(cl, 9);
        var e21 = EmaSeries(cl, 21);
        var e50 = EmaSeries(cl, 50);
        var atr = AtrSeries(closed, 14);
        var atrSma50 = SmaSeries(atr, 50);
        var rsi = RsiSeries(cl, 14);
        double hurst = HurstRS(cl, n - 1, 100);

        var share = new double[n];
        var delta = new double[n];
        var obv = new double[n];
        var rvol = new double[n];
        var vwap = new double[n];
        var vwapU1 = new double[n];
        var vwapD1 = new double[n];
        var vwapU2 = new double[n];
        var vwapD2 = new double[n];
        var vwapN = new int[n];
        var dr10 = new double[n];
        var ob10 = new double[n];
        Array.Fill(rvol, double.NaN);
        Array.Fill(dr10, double.NaN);
        Array.Fill(ob10, double.NaN);

        long lastAnchor = long.MinValue;
        double sPv = 0, sP2v = 0, sV = 0, vol20 = 0, sv10 = 0, sd10 = 0, obAcc = 0;
        int vwCnt = 0;

        for (int i = 0; i < n; i++)
        {
            var k = closed[i];
            bool okFlow = k.Volume > 0 && k.TakerBuyBase >= 0;
            delta[i] = okFlow ? 2.0 * k.TakerBuyBase - k.Volume : 0.0;
            share[i] = okFlow ? k.TakerBuyBase / k.Volume : double.NaN;
            if (i > 0)
            {
                if (k.Close > closed[i - 1].Close) obAcc += k.Volume;
                else if (k.Close < closed[i - 1].Close) obAcc -= k.Volume;
            }
            obv[i] = obAcc;

            if (i >= 20)
            {
                double baseVol = vol20 / 20.0;
                rvol[i] = baseVol > 0 ? k.Volume / baseVol : double.NaN;
            }
            vol20 += k.Volume;
            if (i >= 20) vol20 -= closed[i - 20].Volume;

            sv10 += k.Volume; sd10 += delta[i];
            if (i >= 10) { sv10 -= closed[i - 10].Volume; sd10 -= delta[i - 10]; }
            if (i >= 9 && sv10 > 0) dr10[i] = sd10 / sv10;
            if (i >= 10 && sv10 > 0) ob10[i] = (obv[i] - obv[i - 10]) / sv10;

            long ak = AnchorKey(k.OpenTime, tf);
            if (ak != lastAnchor) { lastAnchor = ak; sPv = sP2v = sV = 0; vwCnt = 0; }
            double tp = (k.High + k.Low + k.Close) / 3.0;
            sPv += tp * k.Volume; sP2v += tp * tp * k.Volume; sV += k.Volume; vwCnt++;
            double vm = sV > 0 ? sPv / sV : tp;
            double vs = sV > 0 ? Math.Sqrt(Math.Max(0, sP2v / sV - vm * vm)) : 0;
            vwap[i] = vm; vwapU1[i] = vm + vs; vwapD1[i] = vm - vs; vwapU2[i] = vm + 2 * vs; vwapD2[i] = vm - 2 * vs; vwapN[i] = vwCnt;
        }

        // Precomputar reglas activas en cada vela cerrada
        var firedByBar = new HashSet<int>[n];
        BarFeature? lastFeat = null;
        for (int i = start; i < n; i++)
        {
            var k = closed[i];
            var k1 = closed[i - 1];
            var k2 = closed[i - 2];
            var k3 = closed[i - 3];
            double a = atr[i];
            double rng = k.High - k.Low;
            double lo15 = double.PositiveInfinity, hi15 = double.NegativeInfinity;
            double lo10 = double.PositiveInfinity, hi10 = double.NegativeInfinity;
            for (int j = i - 15; j < i; j++)
            {
                if (closed[j].Low < lo15) lo15 = closed[j].Low;
                if (closed[j].High > hi15) hi15 = closed[j].High;
                if (j >= i - 10)
                {
                    if (closed[j].Low < lo10) lo10 = closed[j].Low;
                    if (closed[j].High > hi10) hi10 = closed[j].High;
                }
            }
            double vc = (double.IsFinite(atr[i]) && double.IsFinite(atrSma50[i]) && atrSma50[i] > 0) ? atr[i] / atrSma50[i] : 1.0;
            var ab = ComputeAdaptiveBounds(vc);
            var f = new BarFeature
            {
                C = k.Close, O = k.Open, H = k.High, L = k.Low, V = k.Volume, Atr = a,
                DP = (k.Close - k.Open) / k.Open,
                DPa = a > 0 ? (k.Close - k.Open) / a : double.NaN,
                Rvol = rvol[i],
                SpreadAtr = a > 0 ? rng / a : double.NaN,
                Clv = rng > 0 ? ((k.Close - k.Low) - (k.High - k.Close)) / rng : 0.0,
                UpW = rng > 0 ? (k.High - Math.Max(k.Open, k.Close)) / rng : 0.0,
                LoW = rng > 0 ? (Math.Min(k.Open, k.Close) - k.Low) / rng : 0.0,
                Green = k.Close > k.Open, Red = k.Close < k.Open,
                Green1 = k1.Close > k1.Open, Red1 = k1.Close < k1.Open,
                Green2 = k2.Close > k2.Open, Red2 = k2.Close < k2.Open,
                O1 = k1.Open, C1 = k1.Close, C3 = k3.Close, V1 = k1.Volume, V2 = k2.Volume,
                E9 = e9[i], E21 = e21[i], E50 = e50[i], Rsi = rsi[i],
                AtrRatioPrev = (double.IsFinite(atr[i - 1]) && double.IsFinite(atrSma50[i - 1]) && atrSma50[i - 1] > 0) ? atr[i - 1] / atrSma50[i - 1] : double.NaN,
                VolComp = ab.VolCompression, RsiLow = ab.RsiLow, RsiHigh = ab.RsiHigh, ZTrig = ab.ZTrigger,
                Move8a = atr[i - 1] > 0 ? (k1.Close - closed[i - 8].Open) / atr[i - 1] : double.NaN,
                Lo15 = lo15, Hi15 = hi15, Lo10 = lo10, Hi10 = hi10,
                Vwap = vwap[i], VwapU1 = vwapU1[i], VwapD1 = vwapD1[i], VwapU2 = vwapU2[i], VwapD2 = vwapD2[i], VwapN = vwapN[i],
                Share = share[i], Share1 = share[i - 1], Share2 = share[i - 2],
                ShareZ = ZPrev(share, i, 100, 30), Delta = delta[i],
                Dr10Z = ZPrev(dr10, i, 100, 30), Ob10Z = ZPrev(ob10, i, 100, 30),
                PMove10 = a > 0 ? (k.Close - closed[i - 10].Close) / (a * Math.Sqrt(10.0)) : double.NaN
            };

            var set = new HashSet<int>();
            foreach (var r in Rules)
            {
                if (r.Match(f)) set.Add(r.Id);
            }
            firedByBar[i] = set;
            if (i == n - 1) lastFeat = f;
        }

        var lastFired = firedByBar[n - 1];
        if (lastFired == null || lastFired.Count == 0 || lastFeat == null) return null;

        // Filtrar candidatas activas en la última vela según dirección y Filtro Duro de Régimen HTF
        var candidates = Rules.Where(r =>
            lastFired.Contains(r.Id) &&
            (sigAlertDirection != "BUY" || r.Bias > 0) &&
            (sigAlertDirection != "SELL" || r.Bias < 0) &&
            (!hardRegimeFilter || RegimeAllows(r.Bias, htfRegime, r.Category, strictLateral))
        ).ToList();

        if (candidates.Count == 0) return null;

        // Evidencia estadística multi-horizonte con permutación adaptativa + BH global
        var allTests = new List<(int RuleId, int H, double P, bool Stable, double Excess, double Net, double TNet, double Hit, int Count)>();
        foreach (int h in Horizons)
        {
            int endIdx = n - 1 - h;
            int len = endIdx - start + 1;
            if (len < 40) continue;

            var rArr = new double[len];
            int mid = start + (len >> 1);
            double sum = 0;
            double[] sumH = new double[2];
            int[] cntH = new int[2];
            for (int q = 0; q < len; q++)
            {
                double v = (closed[start + q + h].Close - closed[start + q + 1].Open) / closed[start + q + 1].Open;
                rArr[q] = v;
                sum += v;
                int half = (start + q) < mid ? 0 : 1;
                sumH[half] += v;
                cntH[half]++;
            }
            double bm = sum / len;
            var srt = (double[])rArr.Clone();
            Array.Sort(srt);
            double qLo = srt[(int)(0.02 * (len - 1))];
            double qHi = srt[(int)Math.Ceiling(0.98 * (len - 1))];
            var rw = new double[len];
            double[] sumWH = new double[2];
            for (int q = 0; q < len; q++)
            {
                rw[q] = Math.Clamp(rArr[q], qLo, qHi);
                sumWH[(start + q) < mid ? 0 : 1] += rw[q];
            }
            double[] bmWH = { sumWH[0] / cntH[0], sumWH[1] / cntH[1] };

            foreach (var rule in Rules)
            {
                var ev = new List<int>();
                int lastTaken = -1_000_000;
                for (int i = start; i <= endIdx; i++)
                {
                    if (firedByBar[i] != null && firedByBar[i].Contains(rule.Id) && i >= lastTaken + h)
                    {
                        ev.Add(i - start);
                        lastTaken = i;
                    }
                }
                if (ev.Count < 12) continue;

                double sumX = 0, sumW = 0, sumW2 = 0;
                int hits = 0;
                double h0Sum = 0, h1Sum = 0;
                int h0N = 0, h1N = 0;
                foreach (int q in ev)
                {
                    double xVal = rule.Bias * rArr[q];
                    double wVal = rule.Bias * rw[q];
                    sumX += xVal;
                    sumW += wVal;
                    sumW2 += wVal * wVal;
                    if (xVal - costRt > 0) hits++;
                    int half = (start + q) < mid ? 0 : 1;
                    double exHalf = wVal - rule.Bias * bmWH[half];
                    if (half == 0) { h0Sum += exHalf; h0N++; } else { h1Sum += exHalf; h1N++; }
                }
                double m = sumX / ev.Count;
                double mw = sumW / ev.Count;
                double varW = (sumW2 - ev.Count * mw * mw) / Math.Max(1, ev.Count - 1);
                double sdW = varW > 0 ? Math.Sqrt(varW) : double.NaN;
                double net = m - costRt;
                double excess = m - rule.Bias * bm;
                double tNet = double.IsFinite(sdW) && sdW > 0 ? (mw - costRt) / (sdW / Math.Sqrt(ev.Count)) : double.NaN;
                int minHalf = ev.Count >= 20 ? 5 : 4;
                bool stable = h0N >= minHalf && h1N >= minHalf && (h0Sum / h0N) > 0 && (h1Sum / h1N) > 0;

                string seedBase = $"{tf}|{rule.Id}|{h}|{n}|{symbol}";
                double pVal = PermutePValue(rw, ev, len, rule.Bias, mw, 200, seedBase + "|s1");
                if (kFine > 200 && pVal <= 0.08)
                {
                    pVal = PermutePValue(rw, ev, len, rule.Bias, mw, kFine, seedBase + "|s2");
                }
                allTests.Add((rule.Id, h, pVal, stable, excess, net, tNet, (double)hits / ev.Count, ev.Count));
            }
        }

        if (allTests.Count == 0) return null;

        // Benjamini-Hochberg global (q = 0.10)
        var sorted = allTests.OrderBy(t => t.P).ToList();
        int cut = -1;
        for (int i = 0; i < sorted.Count; i++)
        {
            if (sorted[i].P <= (i + 1) * 0.10 / sorted.Count) cut = i;
        }
        var bhPassed = new HashSet<(int, int)>();
        for (int i = 0; i <= cut; i++) bhPassed.Add((sorted[i].RuleId, sorted[i].H));

        SignalHit? bestHit = null;
        foreach (var cand in candidates)
        {
            foreach (var t in allTests.Where(x => x.RuleId == cand.Id))
            {
                double tReq = t.Count >= 20 ? 1.28 : 1.65;
                if (bhPassed.Contains((t.RuleId, t.H)) && t.Stable && t.Excess > 0 && t.Net > 0 && t.TNet >= tReq)
                {
                    if (bestHit == null || t.TNet > bestHit.TNet)
                    {
                        bestHit = new SignalHit(
                            cand.Code, cand.Name, cand.Category, cand.Bias,
                            t.H, t.Count, t.Hit, t.Net, t.P, t.TNet,
                            lastFeat.Atr, hurst,
                            new AdaptiveBounds(lastFeat.VolComp, lastFeat.RsiLow, lastFeat.RsiHigh, lastFeat.ZTrig),
                            htfRegime
                        );
                    }
                }
            }
        }
        return bestHit;
    }

    public static string FormatPrice(double p)
    {
        double a = Math.Abs(p);
        string fmt = a >= 100 ? "N2" : a >= 1 ? "N3" : a >= 0.01 ? "N4" : a >= 0.0001 ? "N6" : "N8";
        return p.ToString(fmt, CultureInfo.InvariantCulture);
    }
}
