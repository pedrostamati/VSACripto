# VSA & Orderflow v4.0 · QuantPulse Edition (.NET 8 WebSockets + Web Terminal)

Actualización mayor de la terminal web (`index.html`) y migración completa del backend de `monitor.js` (polling REST) a un **servicio compilado en C# .NET 8 con Binance WebSockets**.

## Novedades principales de v4.0

### 1. Gestor Completo de Favoritas (Web + Backend)
- Botón estrella (`★ / ☆`) directo junto al selector de par para marcar o quitar favoritas en 1 toque.
- **Barra rápida de favoritas** con variación 24h en vivo y modal **⚙ Gestionar Favoritas** (agregar, quitar, reordenar prioridad `↑ / ↓`, cargar Top 10 por volumen y botón **★ Favoritas** en el escáner).
- Sincronización automática de `"favorites"` en `config.json` (y GitHub) para que el servicio C# priorice tus favoritas al escanear.

### 2. Motor Cuantitativo Profundizado (Estilo QuantPulse)
- **Filtro Duro de Régimen de Mercado (HTF EMA 50 vs EMA 200)**: evalúa la tendencia en temporalidad superior (`Auto 4x`, `4H`, `1D` o `1W`) antes de habilitar largos o cortos intradía. Bloquea operaciones contra-tendencia tanto en el Radar como en el Backtest Walk-Forward y en el servicio C#.
- **Triggers Adaptativos por Volatilidad**: calcula la compresión `ATR(14) / SMA(ATR, 50)` y ajusta dinámicamente los umbrales de RSI (`rsiLow` / `rsiHigh`) y z-score de flujo (`zTrig`), incorporando 4 nuevas reglas adaptativas (**V53–V56**, 56 reglas en total) más el **Exponente de Hurst causal** (R/S de 100 velas).
- **Simulador Aislado de Paper Trading en Vivo**: cuenta virtual independiente del diario real (`10.000 USDT` configurables) que ejecuta alertas confirmadas en segundo plano, calcula P&L no realizado en vivo, cierra automáticamente por SL / TP / tiempo y grafica su propia curva de capital.
- **Mejoras adicionales**: Mapa de calor de las 11 temporalidades, Ratio de Sharpe en Backtest Walk-Forward y sugerencia de riesgo **¼ Kelly** en la calculadora.

### 3. Backend Compilado en C# .NET 8 con WebSockets (`VsaMonitor/`)
- Reemplaza el polling REST cada 15 s por suscripción nativa `ClientWebSocket` a `wss://stream.binance.com:9443/ws/!ticker@arr` (Spot) y `wss://fstream.binance.com/ws/!ticker@arr` (Futuros USDT-M).
- Elimina riesgos de bloqueo HTTP 429 en monitoreo de precios y evalúa alertas + SL/TP de Paper Trading con latencia sub-segundo.
- Cero dependencias NuGet externas; compila como ejecutable autónomo **Single-File** (`VsaMonitor` en Linux y `VsaMonitor.exe` en Windows).

## Puesta en marcha (GitHub Actions + fanatics.com.ar/cripto)
1. Subí `index.html` a tu hosting en `https://fanatics.com.ar/cripto/` (y opcionalmente a GitHub Pages).
2. Subí toda la carpeta del repositorio (`VsaMonitor/`, `.github/workflows/vsa_monitor.yml`, `config.json`, `index.html`, `README.md`) a tu repo de GitHub.
3. Verificá que en **Settings → Secrets and variables → Actions** estén configurados `TELEGRAM_TOKEN` y `TELEGRAM_CHAT_ID`.
4. Andá a **Actions → VSA & Orderflow v4.0 Monitor (.NET 8 WebSocket) → Run workflow**:
   - Compila automáticamente los binarios autónomos para **Linux** y **Windows (`.exe`)** y los deja listos para descargar en **Artifacts** (`VsaMonitor-v4-binaries`).
   - En ejecuciones programadas cada 5 minutos usa el binario cacheado en segundos, sin recompilar.
5. **Ejecución 24/7 en tu PC o VPS (modo continuo sin cortes)**:
   ```bash
   # En Linux / VPS (LOOP_SECONDS=0 corre de forma indefinida)
   TELEGRAM_TOKEN="TU_TOKEN" TELEGRAM_CHAT_ID="TU_CHAT" LOOP_SECONDS=0 ./VsaMonitor
   ```
   En Windows PowerShell:
   ```powershell
   $env:TELEGRAM_TOKEN="TU_TOKEN"; $env:TELEGRAM_CHAT_ID="TU_CHAT"; $env:LOOP_SECONDS="0"; .\VsaMonitor.exe
   ```
