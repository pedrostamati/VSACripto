# VSA & Orderflow v12.0 · Ultimate Guided & Live Suite

Terminal cuantitativa autocontenida (Web, PWA, Android APK y Backend 24/7 en C# .NET 8) pensada para ser clara, explicativa y en tiempo real con APIs públicas gratis.

## Novedades de v12.0
1. **Cotización fija siempre visible (`Sticky Quote Bar`):** al bajar en cualquier pestaña, la cotización en vivo, variación 24h, estado de APIs (`APIs X/8`) y el botón **`? Ayuda`** acompañan al menú superior.
2. **Explicación completa en pantalla de «Ranking Cross-Sectional»:** guía desplegable que detalla RS vs BTC, Funding Carry, Inversa de Volatilidad, Score Z y cómo usar los filtros (con exclusión ampliada de stablecoins como `RLUSD`, `USDE`, `USD1`, `PYUSD`, `BFUSD` y `U`).
3. **Estrategias y Alarmas mucho mejor explicadas:** tanto la alarma emergente en pantalla como los mensajes de Telegram (web y backend C#) detallan **Qué está pasando**, **Qué significa** y **Qué hacer paso a paso**.
4. **Glosario e Interfaz Interactiva (`? Ayuda` / Tap-to-Explain):** más de 40 definiciones claras; activando **`? Ayuda`** podés tocar cualquier menú, botón, métrica o palabra para ver al instante qué es, qué hace y cómo usarla, o buscar cualquier término en el glosario.
5. **Monitor de Carga y Salud de APIs de todos los Exchanges:** controla en vivo Binance Spot, Binance Futuros, WebSocket, Coinbase, Kraken, Bybit, Deribit y Fear & Greed con latencia en `ms`, barra de progreso y acceso en 1 toque desde la barra fija.
6. **Radar Institucional Pro automático + botón manual:** carga automáticamente al abrir la app, al cambiar de par, al volver a la pestaña y cada 90 segundos, conservando el botón manual **Actualizar Pro**.
7. **Lectura Rápida en 5 Segundos (`#cQuickRead`):** semáforo ejecutivo al tope del Radar combinando tendencia HTF, flujo Spot/Perps y calendario Macro.

## Despliegue rápido
- **Servidor web (`fanatics.com.ar/cripto/`):** reemplazá `index.html`, `sw.js` y `manifest.json` (si ya tenés los íconos).
- **GitHub (`pedrostamati/VSACripto`):** subí los archivos respetando carpetas (`VsaMonitor/` y `.github/workflows/`), sin pisar tu `config.json` si ya tenés alertas guardadas. **Actions** detecta `v12.0` solo y compila tu APK firmado.
