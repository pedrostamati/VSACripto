# VSA & Orderflow v6.0 · Edge Lab

Web + PWA + App Android (APK) + Backend C# .NET 8 · Binance Spot/Futuros · 64 reglas

> **Qué cambia en una línea:** la v5 buscaba señales par por par y el azar le fabricaba «respaldos». La v6 prueba cada regla **una sola vez sobre todo el universo líquido**, con la misma salida y los mismos costos que se operan, la valida **fuera de muestra**, y solo deja pasar a alertas y Paper lo que sobrevive.

---

## 1. Diagnóstico con datos reales (Binance Spot, 22/11/2026)

Medido sobre 26 pares líquidos (sin estables, oro tokenizado ni listados de menos de 1 año), 4 temporalidades, 3.000 velas cada una:

| Hallazgo | Número |
|---|---|
| Etiquetas «prometedora» del motor por par v5 (p < 0,05 sin corregir) | **399** en 104 par×TF (≈3,8 por par×TF, casi todo azar) |
| Operaciones fuera de muestra elegidas por el motor por par v5 (walk-forward 60/40) | 21 operaciones · **−0,80 % por operación (−0,30 R)** |
| Reglas×TF que pasan el laboratorio con la salida del plan (1,3 ATR · 2R · 12 velas) | **0 de 197** |
| Reglas×TF que pierden plata de forma significativa | **88** (casi todas en 15m y 1H) |
| Costo por operación en R (comisión + ½ spread, stop 1,3 ATR) | **15m 0,38 R · 1H 0,21 R · 4H 0,09 R · 1D 0,03 R** |

Conclusión operativa: en Spot, el intradía está estructuralmente perdido por costos, y ninguna de las reglas VSA actuales tiene hoy una ventaja demostrable después de costos. La v6 **no fabrica oportunidades**: las mide, y te protege de operar las falsas.

### Búsqueda pre-declarada de configuraciones (reportada completa)

Todas sobre 4H+1D salvo la última fila. «BH» = pasa Benjamini-Hochberg en entrenamiento.

| Salida | Filtro HTF | Pruebas | Pasan BH | Validadas |
|---|---|---|---|---|
| 1,3 ATR · 2R · 12v (plan) | sí | 93 | 0 | 0 |
| 1,3 ATR · 2R · 12v | no | 105 | 0 | 0 |
| 2 ATR · 1,5R · 24v (swing) | sí | 92 | 0 | 0 |
| 1 ATR · 3R · 24v (tendencia) | sí | 93 | 0 | 0 |
| 2 ATR · 1,5R · 24v | no | 104 | 0 | 0 |
| 2 ATR · 1,5R · 24v · **50 pares · solo 1D** | sí / no | 46 / 51 | 0 / 0 | 0 / 0 |

Lo único digno de seguir (sin validar):
- **Familia de rupturas diarias** (V07, V23, V27, V47): +0,22 a +0,33 R por operación en entrenamiento, positiva en ambas mitades, pero con exceso sobre la entrada al azar de solo +0,10 a +0,20 R y fuera de muestra ≈ 0. Parece en buena parte beta del ciclo alcista 2018–2023.
- **V53 en 1D con Filtro HTF y salida swing**: +0,64 R en entrenamiento (n=31) y +0,72 R fuera de muestra (n=27, t≈4). No pasa BH estricta (p=0,0039) y apareció después de mirar 7 configuraciones: el laboratorio la marca **candidata** → va a Paper para validarla hacia adelante, **nunca** a capital real todavía.

---

## 2. Qué hace la v6

### Pestaña nueva «Oportunidades»
1. **Laboratorio de Ventaja (universo agrupado).** Toma los 15/30/50 pares USDT más líquidos (spread real del `bookTicker`, filtro de estables por paridad, mínimo 1 año listado) y para cada regla×TF:
   - simula **la misma salida que se opera** (stop ATR, objetivo R, tope de velas) con comisión + máx(deslizamiento, ½ spread real) por par;
   - aplica el **Filtro HTF reconstruido sin mirar el futuro** (la evidencia es condicional a lo que realmente se habría operado);
   - entrena en el 70 % inicial (purga en el corte) y valida en el 30 % más reciente;
   - error estándar **robusto por clúster temporal** (las criptos disparan juntas en los mismos movimientos);
   - **prueba de intersección-unión**: la regla tiene que ganar plata **y** ganarle a la entrada al azar del mismo par (no vale ser solo beta de un mercado alcista);
   - BH global sobre todas las reglas×TF; **umbral fuera de muestra corregido por la cantidad de configuraciones probadas** en 30 días (k).
2. **Libro de ventajas**: Validada (BH 10 % + fuera de muestra) → alertas y Paper. Candidata (BH 25 % o fuera de muestra débil) → solo Paper. Refutada, En contra, Sin ventaja. Exportable a CSV y al backend.
3. **Oportunidades validadas ahora**: reglas del libro que dispararon en la última vela cerrada de cualquier par del universo, con plan (entrada, stop, objetivo, vencimiento), frescura de la señal, deriva desde el cierre y costo en R. En Spot, las validadas bajistas aparecen como **salidas** («si tenés la moneda»).
4. **Vigilancia automática**: en cada cierre de vela de las TF del libro re-evalúa el universo y dispara alarma + Telegram + Paper solo para ventajas validadas.

### Radar, alertas y Paper
- Con libro vigente (mismo mercado, umbrales y RVOL, < 14 días), **las alertas y el Paper automático salen solo del libro**. El veredicto lo muestra («COMPRA VALIDADA (UNIVERSO)», «SEÑALES DEL PAR SIN VALIDAR EN EL UNIVERSO») y el plan usa la salida con la que se validó.
- El plan avisa cuando los costos superan 0,25 R por operación.
- **Paper realista**: tope de exposición **agregado** (antes 5 posiciones Spot podían sumar 5× el capital), stops/objetivos **conciliados con velas de 1m** (antes se miraba el ticker cada 15 s y las mechas no contaban), curva **marcada a mercado** (el drawdown flotante ya no queda oculto), auditoría con **t y cota al 95 %** (N ≥ 30) y desglose **por origen** (validada / candidata / por par / manual).

### Núcleo estadístico
- **p-valores sin piso**: con < 10 excedencias en la permutación se usa la cola t de Student calibrada con la propia distribución permutada. Antes, con K=1.500 y ~300 pruebas, era imposible que una regla sola pasara BH.
- **«Prometedora» con FDR controlada** (BH q=25 %) en vez de p < 0,05 crudo: de 399 a 36 en los mismos datos.
- **Reglas nuevas V61–V64** (hipótesis propias para el flujo taker de cripto): absorción pasiva en mínimo/máximo de 20 velas con compras/ventas agresivas sin avance, y sequía de volumen tras una vela de expansión (banderas).
- Etiquetas honestas: el puntaje de squeeze y los «imanes de liquidación» se marcan como heurísticos; el «Sharpe» del backtest era en realidad una t y ahora se llama así.

### Backend C# (24/7)
- Réplica **exacta** del núcleo de la web: perfil de volumen, RVOL por franja horaria y las 64 reglas (antes le faltaban 12 de las 58 reglas direccionales —V11, V12, V23–V28, V47, V48, V51, V52— y usaba otro RVOL, así que «disparaba» cosas distintas que la web).
- Si `config.json` trae `edgeBook`, el backend alerta **solo** con ventajas validadas (candidatas: solo Paper), con la salida exacta del libro y descartando señales tardías (pasada la mitad de la vela de entrada). Sin libro vigente, modo clásico por par (con los p-valores corregidos).
- Paper: conciliación con velas 1m/5m/15m al inicio de cada corrida (stops tocados mientras no corría), tope de exposición agregado, curva de capital y auditoría con t.

---

## 3. Instalación

El repo no tiene (ni necesita) carpeta `AndroidApp/`: el workflow `build_android_apk.yml` arma el proyecto Android en cada corrida y le mete adentro el `index.html` de la raíz.

| Archivo | Dónde va |
|---|---|
| `index.html` | **GitHub**: raíz del repo · **Servidor**: `fanatics.com.ar/cripto/` |
| `manifest.json`, `sw.js` | **Servidor**: `fanatics.com.ar/cripto/` (los íconos no cambian) |
| `build_android_apk.yml` | **GitHub**: `.github/workflows/` (reemplaza al anterior) |
| `Program.cs`, `Models.cs`, `QuantEngine.cs`, `VsaMonitor.csproj` | **GitHub**: `VsaMonitor/` |
| `config.json`, `README.md` | **GitHub**: raíz (`config.json` lo mantiene la web con la sincronización) |

### Web / PWA (`fanatics.com.ar/cripto/`)
Subí `index.html`, `manifest.json` y `sw.js`. El service worker cambió de nombre de caché (`vsa-orderflow-v6-0`), así que el celular toma la versión nueva al abrir (si no aparece, cerrá la app/pestaña y volvé a abrir).

### APK Android (GitHub Actions)
1. **Una sola vez**, creá el secreto `APK_KEY_PASS`: Settings → Secrets and variables → Actions → New repository secret. Contraseña de 12 caracteres o más; guardala.
2. Subí `build_android_apk.yml` a `.github/workflows/` y `index.html` a la raíz. Cada vez que cambia `index.html`, el APK se compila solo.
3. Actions → **Compilar App Android (APK)** → última corrida: el resumen muestra versión, tipo de firma y huella. Abajo, en Artifacts, está `VSA-Orderflow-v6.0-Android-APK` (un zip con el `.apk`).
4. **Por única vez**, desinstalá la versión anterior: estaba firmada con una llave descartable y Android no deja instalar encima. Desde ahora cada APK se instala sobre el anterior y conserva los datos de la app.

La primera corrida con el secreto crea `android-signing/vsa-key.p12.enc`: la llave de firma cifrada con AES-256 + PBKDF2 (600 000 iteraciones), inútil sin la contraseña. **No la borres**: si desaparece se genera otra llave y hay que desinstalar una vez más. Si el secreto cambia a otra contraseña, el workflow se detiene con un aviso en lugar de firmar con una llave distinta. Sin el secreto todo funciona como antes (llave descartable; el archivo sale como `…-FIRMA-DEBUG.apk`).

`versionName` = versión del `<title>` de `index.html` + número de corrida (p. ej. 6.0.12). `versionCode` sale de la fecha del commit, así que siempre crece.

Dentro del APK los botones que descargan archivos (CSV, `config.json`) no hacen nada: el WebView no tiene gestor de descargas. Para mandar la configuración al bot usá la sincronización con GitHub; para exportar CSV, la versión web.

### Backend `VsaMonitor/`
Reemplazá `Program.cs`, `Models.cs` y `QuantEngine.cs` (`VsaMonitor.csproj` no cambia). El workflow `vsa_monitor.yml` **no necesita cambios**: mismas variables (`TELEGRAM_TOKEN`, `TELEGRAM_CHAT_ID`, `LOOP_SECONDS`, `TEST_NOTIFY`, `VSA_WORKDIR`) y mismos archivos (`config.json`, `state.json`, `paper_state.json`). Los JSON viejos se leen sin problema; los campos nuevos tienen valores por defecto.

`config.json` incluido: tu configuración actual + `thr`, `rvol` y `edgeBook: null`. El libro lo escribe la web cuando tocás **Enviar al backend** en el laboratorio (o automáticamente si tenés la sincronización con GitHub activa).

---

## 4. Flujo recomendado

1. **Oportunidades → Laboratorio**: 30 pares · 4H + 1D · «Plan actual» o «Swing» · **Correr laboratorio** (1–2 min en el celular; podés seguir usando la app).
2. Mirá el embudo: pruebas → pasan BH → estables + exceso → **validadas fuera de muestra**. Si dice 0, el mensaje correcto es *no operar*.
3. Activá **Vigilancia** (app abierta) y **Enviar al backend** (24/7 por Telegram).
4. Dejá que el Paper acumule. Mirá la auditoría **por origen**: recién con N ≥ 30, t ≥ 1,645 y drawdown flotante < 15 % una regla merece capital real.
5. Corré el laboratorio de nuevo cada semana (el libro vence a los 14 días). Cada configuración distinta que pruebes sube el umbral fuera de muestra (corrección por corridas): **elegí la configuración antes de mirar resultados**.

---

## 5. Verificación realizada

| Prueba | Resultado |
|---|---|
| Autoprueba de fórmulas (22 escenarios, incluye V61–V64) | 22/22 |
| Universos nulos sintéticos (mercado común con GARCH, betas, volumen acoplado a \|retorno\|) | 48 universos: **0 ventajas validadas falsas, 0 pasan BH** |
| Mismo nulo con error estándar i.i.d. ingenuo (sin clústeres) | falsas validadas en 7 de 12 universos (antes de la prueba intersección-unión) |
| Potencia: ventaja de timing plantada ≈ +0,22 R/op sobre azar | detectada en 6 de 8 universos |
| Potencia: ventaja ≈ +0,10 R/op | no detectada (límite de resolución con ~25 pares) |
| Paridad web (JS) ↔ backend (C#), reglas disparadas vela por vela | **286.457 velas, 0 diferencias** (26 pares × 4 TF + escenarios) |
| Interfaz completa en jsdom con datos reales de Binance | laboratorio, libro, oportunidades, Paper, conciliación 1m, radar, vigilancia y exportación sin errores de consola |
| Backend compilado contra Binance en vivo | modo libro, alertas validadas, salida Spot, conciliación Paper (cerró por STOP una mecha entre corridas) |
| Workflow del APK en un emulador de GitHub Actions (pasos `run:` reales; Gradle y apksigner simulados) | 38/38: misma huella de firma entre corridas, contraseña equivocada, corrida en cola, carrera entre corridas, push denegado, control de que el APK lleva el `index.html` del repo |

---

## 6. Límites que conviene tener presentes
- **Supervivencia**: el universo son las monedas líquidas *de hoy*. La exigencia de ganarle a la entrada al azar del mismo par lo atenúa, no lo elimina.
- **Resolución**: con ~25–50 pares el laboratorio detecta ventajas de ≈ +0,2 R por operación sobre el azar. Ventajas más chicas existen o no, pero no se pueden distinguir del ruido con estos datos.
- **Decaimiento**: lo que valida hoy puede dejar de funcionar; por eso el libro vence y la auditoría del Paper es la prueba final.
- Libro de órdenes (OBI), funding y puntaje de squeeze siguen siendo descriptivos: no están validados.
- Nada de esto es asesoramiento financiero: es una herramienta para medir, no una promesa de rendimiento.

---

## Archivos de esta entrega
```
GitHub (pedrostamati/VSACripto)
  index.html                                → web y contenido del APK (v6.0)
  config.json                               → configuración del bot (la escribe la web)
  README.md
  VsaMonitor/Program.cs, Models.cs, QuantEngine.cs, VsaMonitor.csproj
  .github/workflows/build_android_apk.yml   → v6: firma estable + versión automática
  .github/workflows/vsa_monitor.yml         → sin cambios
  android-signing/vsa-key.p12.enc           → lo crea el workflow (no tocar)

Servidor (fanatics.com.ar/cripto/)
  index.html, manifest.json, sw.js          (+ icon-192.png, icon-512.png que ya están)
```
