# VSA & Orderflow v11.1 · Institutional Pro Suite

Terminal cuantitativa autocontenida (Web, PWA, Android APK y Backend 24/7 en C# .NET 8) integrada con **APIs públicas y gratuitas** (sin API keys).

## Novedades de v11.1
1. **Radar Institucional Pro (4 vistas con APIs públicas gratis):**
   - **Macro & Sentimiento:** calendario de eventos de alto impacto (FOMC 2026–2027, CPI y NFP) con bloqueo preventivo opcional (±4h) y *Crypto Fear & Greed Index* (`alternative.me`).
   - **Multi-Exchange (Spot vs. Perps):** *Coinbase Premium (%)*, *Kraken Spread*, *Funding Bybit* y divergencia de **CVD Spot vs. Futuros (30h)** para distinguir demanda real de rallies frágiles por apalancamiento.
   - **Opciones Deribit en vivo:** *Put/Call OI Ratio*, *Max Pain* del vencimiento líder, *ATM IV (%)* y *Skew 25D*.
   - **Mapa de Liquidaciones Estimadas (10x–100x):** imanes superior/inferior y capa opcional **Liquidaciones est.** directo en el gráfico Canvas.
2. **Footprint Intravela en Vivo (`aggTrade`):** tabla de delta por nivel de precio en tiempo real desde WebSocket de Binance con *Live POC* e *Imbalances ≥ 3:1*.
3. **Control de Portafolio, Correlación BTC & Kill Switch:** correlación (`ρ`) y beta (`β`) vs. BTC, exposición neta beta-ponderada, riesgo sugerido por *Vol-Targeting* y **Kill Switch diario/semanal** que frena nuevas entradas Paper automáticamente.
4. **Diario de Ejecución & Orden Binance Lista:** botón para copiar orden *Limit Post-Only + OCO* lista para pegar, más registro y atribución de slippage (bps) y adherencia al plan (Real vs. Sistema).
5. **Backend C# .NET 8 (`VsaMonitor/` v11.1):** suma Kill Switch diario/semanal, Macro Guard FOMC y *Coinbase Premium* en cada alerta de Telegram.

## Despliegue
- **Servidor web (`fanatics.com.ar/cripto/`):** subí `index.html`, `sw.js`, `manifest.json`, `icon-192.png` e `icon-512.png`.
- **GitHub (`pedrostamati/VSACripto`):** subí todo el contenido respetando carpetas (`VsaMonitor/` y `.github/workflows/`), sin pisar tu `config.json` si ya tenés alertas guardadas. El APK `v11.1` se compila solo en **Actions**.
