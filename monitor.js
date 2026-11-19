'use strict';
/**
 * VSA & Orderflow v3.0 · Monitor para GitHub Actions (Node.js 20+, cero dependencias)
 * - Lee TELEGRAM_TOKEN y TELEGRAM_CHAT_ID desde GitHub Secrets (variables de entorno)
 * - Revisa precios cada 15 segundos durante LOOP_SECONDS (por defecto 260 s = 4 min 20 s)
 * - Revisa señales confirmadas ("con respaldo") en las velas recién cerradas
 * - Guarda estado en state.json (persistido con actions/cache entre corridas)
 */
const fs = require('fs');
const path = require('path');

const DIR = __dirname;
const HTML_PATH = path.join(DIR, 'index.html');
const CFG_PATH = path.join(DIR, 'config.json');
const STATE_PATH = path.join(DIR, 'state.json');

const LOOP_SECONDS = Math.max(0, Number(process.env.LOOP_SECONDS || 260));
const POLL_MS = Math.max(5000, Number(process.env.POLL_MS || 15000));
const TEST_MODE = process.env.TEST_NOTIFY === '1';

// 1. Extraer motor VSA y VSAData desde index.html
const htmlRaw = fs.readFileSync(HTML_PATH, 'utf8');
const scriptMatch = htmlRaw.match(/<script>([\s\S]*?)<\/script>/);
if (!scriptMatch) throw new Error('No se encontró <script> en index.html');
const uiSplit = scriptMatch[1].indexOf('/* ============================================================================\n   INTERFAZ v3.0');
if (uiSplit < 0) throw new Error('No se encontró el corte de INTERFAZ v3.0');
eval(scriptMatch[1].slice(0, uiSplit));

const V = globalThis.VSA;
const D = globalThis.VSAData;

const TOKEN = (process.env.TELEGRAM_TOKEN || '').trim();
const CHAT_ID = String(process.env.TELEGRAM_CHAT_ID || '').trim();

function loadJson(p, def) {
  try { return JSON.parse(fs.readFileSync(p, 'utf8')); } catch { return def; }
}
function saveJson(p, obj) {
  fs.writeFileSync(p, JSON.stringify(obj, null, 2) + '\n', 'utf8');
}

const CFG = loadJson(CFG_PATH, {
  market: 'spot', symbols: ['BTCUSDT', 'ETHUSDT', 'SOLUSDT'], timeframes: ['1h', '4h'],
  hist: 3000, Kfine: 4000, sigAlertOn: true, sigAlert: 'BOTH', priceAlerts: []
});
const STATE = loadJson(STATE_PATH, { candles: {}, firedAlerts: {} });

async function sendTelegram(text) {
  if (!TOKEN || !CHAT_ID) {
    console.log('[Sin Secrets TELEGRAM_TOKEN/TELEGRAM_CHAT_ID]\n' + text);
    return false;
  }
  const url = `https://api.telegram.org/bot${encodeURIComponent(TOKEN)}/sendMessage`;
  try {
    const res = await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ chat_id: CHAT_ID, text, disable_web_page_preview: true })
    });
    const j = await res.json();
    if (!j.ok) console.error('[Telegram error]', j.description);
    return !!j.ok;
  } catch (e) {
    console.error('[Telegram red]', e.message);
    return false;
  }
}

function alertKey(a) {
  return `${a.market || CFG.market}|${a.sym}|${a.dir}|${a.target}`;
}

async function checkPriceAlerts() {
  const list = Array.isArray(CFG.priceAlerts) ? CFG.priceAlerts : [];
  const active = list.filter(a => {
    if (!a || !a.sym || !(a.target > 0)) return false;
    const prev = STATE.firedAlerts[alertKey(a)] || 0;
    if (!a.repeat && prev > 0) return false;
    if (a.repeat && prev > 0 && Date.now() - prev < 60000) return false;
    return true;
  });
  if (!active.length) return;

  const pairs = new Map();
  for (const a of active) {
    const m = a.market || CFG.market;
    pairs.set(`${m}|${a.sym}`, { market: m, sym: a.sym });
  }
  for (const { market, sym } of pairs.values()) {
    try {
      const tk = await D.ticker(market, sym);
      const px = tk.last;
      if (!(px > 0)) continue;
      for (const a of active) {
        if (a.sym !== sym || (a.market || CFG.market) !== market) continue;
        const up = a.dir === 'up';
        if (up ? px >= a.target : px <= a.target) {
          STATE.firedAlerts[alertKey(a)] = Date.now();
          const msg = `${up ? '🚨📈 SUBIÓ' : '🚨📉 BAJÓ'} ${sym} (${market.toUpperCase()})\nPrecio actual: ${V.fpx(px)}\nObjetivo ${up ? '≥' : '≤'} ${V.fpx(a.target)}`;
          console.log('[ALERTA PRECIO]', sym, px);
          await sendTelegram(msg);
        }
      }
    } catch (e) {
      console.warn(`[precio ${sym}]`, e.message);
    }
  }
}

async function checkConfirmedSignals() {
  if (!CFG.sigAlertOn) return;
  const market = CFG.market === 'futures' ? 'futures' : 'spot';
  const cost = market === 'futures' ? 0.0012 : 0.0024;
  await D.syncTime(market).catch(() => {});

  for (const sym of (CFG.symbols || [])) {
    for (const tf of (CFG.timeframes || ['1h', '4h'])) {
      try {
        const rs = await D.resolveSymbol(market, sym);
        const rows = await D.history(market, rs.sym, tf, Number(CFG.hist) || 3000);
        const sp = D.split(rows, D.now());
        if (sp.closed.length < V.START_SHORT + 2) continue;
        const an = V.analyze(sp.closed, { tf, thr: 'esc', rvol: 'seas' });
        const L = an.n - 1;
        const lastCandle = an.c[L];
        const key = `${market}|${rs.sym}|${tf}`;

        // Evitar repetir la misma vela ya alertada
        if (STATE.candles[key] === lastCandle.t) continue;

        // Si es la primera corrida sin cache, solo alertar si la vela cerró hace menos de 20 min
        const closedAgoMs = D.now() - (lastCandle.T + 1);
        const firstSeen = STATE.candles[key] === undefined;
        STATE.candles[key] = lastCandle.t;
        if (firstSeen && closedAgoMs > 20 * 60 * 1000) continue;

        const ev = V.evidence(an, { hs: V.HORIZONS, cost, K0: 200, Kfine: Number(CFG.Kfine) || 4000, seed: rs.sym });
        const items = V.ruleItems(an, L, ev, 0).filter(it => {
          if (it.label !== 'respaldo' || !it.rule.bias) return false;
          if (CFG.sigAlert === 'BUY' && it.rule.bias < 0) return false;
          if (CFG.sigAlert === 'SELL' && it.rule.bias > 0) return false;
          return true;
        });
        if (!items.length) continue;

        const it = items[0];
        const up = it.rule.bias > 0;
        const px = sp.forming ? sp.forming.c : lastCandle.c;
        const atr = an.atr[L];
        const risk = Math.max(1.3 * atr, px * 0.0035);
        const sl = px - it.rule.bias * risk;
        const tp = px + it.rule.bias * 2 * risk;
        const msg = [
          `${up ? '🚨📈 COMPRA CONFIRMADA' : '🚨📉 VENTA CONFIRMADA'} · ${rs.sym} (${market.toUpperCase()}) · ${tf.toUpperCase()}`,
          `Regla: ${it.rule.code} ${it.rule.name}`,
          `Evidencia (h=${it.h}v): n=${it.ev.n} · acierto ${V.fshare(it.ev.hit)} · neto ${V.fpct(it.ev.net)} · p=${V.fnum(it.ev.p, 4)} (BH✓)`,
          `Ref: ${V.fpx(px)} · SL: ${V.fpx(sl)} · Obj 2R: ${V.fpx(tp)}`
        ].join('\n');
        console.log('[SEÑAL CONFIRMADA]', rs.sym, tf, it.rule.code);
        await sendTelegram(msg);
      } catch (e) {
        console.warn(`[señal ${sym} ${tf}]`, e.message);
      }
    }
  }
}

const sleep = ms => new Promise(r => setTimeout(r, ms));

async function main() {
  const start = Date.now();
  console.log(`VSA Monitor iniciado · loop=${LOOP_SECONDS}s · poll=${POLL_MS}ms · pares=${(CFG.symbols || []).length}`);
  if (TEST_MODE) {
    await sendTelegram(`✅ Prueba manual desde GitHub Actions OK (${(CFG.market || 'spot').toUpperCase()}, ${(CFG.symbols || []).length} pares).`);
  }

  // 1) Revisión inicial de precios + señales confirmadas
  await checkPriceAlerts();
  await checkConfirmedSignals();

  // 2) Bucle de precios cada 15 s hasta completar los ~4 min 20 s de esta corrida
  while (Date.now() - start + POLL_MS < LOOP_SECONDS * 1000) {
    await sleep(POLL_MS);
    await checkPriceAlerts();
  }

  saveJson(STATE_PATH, STATE);
  console.log(`Corrida finalizada en ${Math.round((Date.now() - start) / 1000)} s.`);
}

main().catch(e => { console.error(e); process.exit(1); });
