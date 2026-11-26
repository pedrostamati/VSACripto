# VSA & Orderflow v10.1 · Unified Alpha Suite

Terminal cuantitativa autocontenida (Web, PWA, Android APK y Backend 24/7 en C# .NET 8) diseñada para análisis de flujo de órdenes, VSA, validación estadística fuera de muestra y monitoreo en vivo sobre Binance Spot y Futuros.

## Novedades de v10.1
1. **Unificación en un único `index.html` autocontenido**: integra el diseño visual, Command Deck en vivo (`aggTrade` + `miniTicker` + `Cmd+K`), Anticipo calibrado y las 70 reglas en un solo archivo listo para `fanatics.com.ar/cripto/`, GitHub Pages, PWA y el compilador automático de APK (`build_android_apk.yml`).
2. **Conexión directa a Binance**: elimina el proxy `/api/binance` que dependía de Vercel/Node y rompía el funcionamiento en hosting estático y WebView Android (`file://`).
3. **Etiquetas anti-colisión en el gráfico Canvas**: apilamiento dinámico en el eje derecho para `ENT`, `SL`, `1R/BE`, `OBJ`, `VAH`, `POC`, `VAL` y `P50`, eliminando superposiciones con velas o entre niveles.
4. **Validación OOS de «Anticipo» (Brier Score & Skill)** dentro del Laboratorio de Ventaja: mide si el pronóstico probabilístico (`h=6` velas) supera la climatología base fuera de muestra, mostrando Brier Score, Brier Skill Score (BSS), acierto en sesgo fuerte (`≥58%` / `≤42%`) y tabla de calibración en 5 bloques.
5. **Ranking Cross-Sectional en vivo** (pestaña *Oportunidades / Edge*): ranquea los 60 pares USDT más líquidos por Fuerza Relativa vs BTC, Funding Carry (Futuros) e Inversa de Volatilidad con apertura directa en Radar.
6. **Paridad total del Backend C# .NET 8 (`VsaMonitor/`)**: suma las 10 reglas faltantes (`V61`–`V70`), gestión dinámica de **Breakeven 1R + Trailing Stop ATR** en Paper Trading y soporte nativo de `edgeBook` desde `config.json`.

## Instalación rápida
- **Web (`fanatics.com.ar/cripto/`)**: subí `index.html`, `sw.js`, `manifest.json`, `icon-192.png` e `icon-512.png`.
- **GitHub + APK Android**: subí los archivos a tu repositorio `pedrostamati/VSACripto`. El workflow `.github/workflows/build_android_apk.yml` detecta automáticamente la versión `10.1` desde `<title>` y genera el APK firmado estable (`APK_KEY_PASS`).
- **Backend 24/7 Telegram**: actualizá la carpeta `VsaMonitor/` y `.github/workflows/vsa_monitor.yml`. Desde el Laboratorio de Ventaja web podés tocar **Enviar al backend** para sincronizar tu `edgeBook` directo a `config.json`.
