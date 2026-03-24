// ============================================================
// TRADE REAPER — Rocket Scooter Level Scraper v2.0
// ============================================================
// Scrapes ALL data from Rocket Scooter including:
//   - Chart levels (Bull/Bear Zones, HP, MHP, HG, DD, Open, Close)
//   - SP500 Resilience (3 numbers)
//   - NQ100 Resilience (3 numbers)
//   - DD Ratio
//   - Live prices + alert direction
//   - Header bar HP/MHP values
//
// HOW TO USE:
//   1. Open rocket.place/pro-plus/ — wait for charts to load
//   2. Press F12 → Console tab
//   3. Paste this entire script → Enter
//   4. Make sure trade_reaper.py is running
// ============================================================

(function () {
  'use strict';

  const BRIDGE_URL = 'http://localhost:8777/levels';
  const FAST_INTERVAL_MS = 500;     // 0.5s for resilience/DD/prices (res moves fast at open)
  const LEVEL_WARMUP_MS = 30000;    // 30s for levels during first 15min (levels loading at open)
  const LEVEL_STEADY_MS = 120000;   // 2min for levels after warmup
  const WARMUP_DURATION_MS = 900000; // 15 minutes warmup period
  let scrapeCount = 0;
  let lastError = null;
  let cachedLevels = {};
  let lastLevelScrape = 0;
  const startTime = Date.now();

  // ----------------------------------------------------------
  // FAST DATA: Resilience, DD Ratio, Live Prices (changes often)
  // ----------------------------------------------------------
  function scrapeFastData() {
    const data = {
      sp500Resilience: { res: null, sp: null, hp: null },
      nq100Resilience: { res: null, sp: null, hp: null },
      ru2kResilience: null,
      ddRatio: null,
      livePrice: {},
      alertDirection: {},
      headerHP: {},
      headerMHP: {}
    };

    try {
      // --- Right panel resilience containers ---
      const containers = document.querySelectorAll('.resilience-container');
      for (const c of containers) {
        const header = (c.querySelector('.header')?.textContent || '').trim();
        const spans = Array.from(c.querySelectorAll('span'));

        if (header.includes('SP500 RESILIENCE')) {
          const vals = spans.map(s => parseFloat(s.textContent));
          if (vals.length >= 3) {
            data.sp500Resilience = { res: vals[0], sp: vals[1], hp: vals[2] };
          } else if (vals.length >= 1) {
            data.sp500Resilience.res = vals[0];
            if (vals.length >= 2) data.sp500Resilience.sp = vals[1];
          }
        }
        else if (header.includes('NQ100 RESILIENCE')) {
          const vals = spans.map(s => parseFloat(s.textContent));
          if (vals.length >= 3) {
            data.nq100Resilience = { res: vals[0], sp: vals[1], hp: vals[2] };
          } else if (vals.length >= 1) {
            data.nq100Resilience.res = vals[0];
            if (vals.length >= 2) data.nq100Resilience.sp = vals[1];
          }
        }
        else if (header.includes('RU2K RESILIENCE')) {
          const vals = spans.map(s => parseFloat(s.textContent));
          if (vals.length >= 1) data.ru2kResilience = vals[0];
        }
        else if (header.includes('DD RATIO')) {
          const vals = spans.map(s => parseFloat(s.textContent));
          if (vals.length >= 1) data.ddRatio = vals[0];
        }
      }

      // --- Header bar data (HP, MHP, DD, Res) ---
      const headerItems = document.querySelectorAll('.resilience-item');
      for (const item of headerItems) {
        const text = item.textContent.trim();

        if (text.startsWith('SP500: DD:')) {
          const span = item.querySelector('span.sp');
          if (span) data.ddRatio = data.ddRatio || parseFloat(span.textContent);
        }
        else if (text.startsWith('HP:') && !text.includes('NQ') && !text.includes('MHP')) {
          const span = item.querySelector('span.hp');
          if (span) data.headerHP.SP = parseFloat(span.textContent);
        }
        else if (text.startsWith('MHP:') && !text.includes('NQ')) {
          const span = item.querySelector('span.mhp');
          if (span) data.headerMHP.SP = parseFloat(span.textContent);
        }
        else if (text.includes('NQ100:')) {
          // NQ res values are in this item
        }
      }

      // --- Separate pass for NQ header HP/MHP ---
      // These might be in a different order, so let's get them from the resilience items
      const allResItems = Array.from(headerItems);
      let foundNQ = false;
      for (const item of allResItems) {
        const text = item.textContent.trim();
        if (text.includes('NQ100:')) foundNQ = true;
        if (foundNQ) {
          if (text.startsWith('HP:')) {
            const span = item.querySelector('span.hp');
            if (span) data.headerHP.NQ = parseFloat(span.textContent);
          }
          if (text.startsWith('MHP:')) {
            const span = item.querySelector('span.mhp');
            if (span) data.headerMHP.NQ = parseFloat(span.textContent);
          }
        }
      }

      // --- Live prices from CQGdata ---
      if (window.CQGdata?.alerts) {
        for (const alert of window.CQGdata.alerts) {
          data.livePrice[alert.alert] = alert.price;
          data.alertDirection[alert.alert] = alert.dir;
        }
      }

      // --- Current SPHP/NQHP values ---
      if (window.SPHPNOW != null) data.sp500Resilience.hpNow = window.SPHPNOW;
      if (window.SPMHPNOW != null) data.sp500Resilience.mhpNow = window.SPMHPNOW;
      if (window.NQHPNOW != null) data.nq100Resilience.hpNow = window.NQHPNOW;
      if (window.NQMHPNOW != null) data.nq100Resilience.mhpNow = window.NQMHPNOW;

    } catch (e) {
      console.warn('[TradeReaper] Fast data scrape error:', e.message);
    }

    return data;
  }

  // ----------------------------------------------------------
  // SLOW DATA: Chart levels (Bull/Bear Zones WITH ranges, HP, MHP, etc.)
  // ----------------------------------------------------------
  function scrapeChartLevels(chartIndex, instrumentName) {
    const levels = {
      instrument: instrumentName,
      bullZones: [],   // [{bottom, top}] — bottom = label price, top = rectangle top
      bearZones: [],   // [{bottom, top}] — top = label price, bottom = rectangle bottom
      hp: null,
      mhp: null,
      hg: null,
      dd: { upper: null, lower: null },
      open: null,
      close: null
    };

    try {
      const chart = window.tvWidget.chart(chartIndex);
      if (!chart) return null;

      const shapes = chart.getAllShapes();

      // First pass: collect all rectangles (zone shading) and text labels
      const rects = [];
      const textLabels = [];

      for (const shape of shapes) {
        try {
          const entity = chart.getShapeById(shape.id);
          const points = entity.getPoints();
          const props = entity.getProperties();

          if (shape.name === 'rectangle' && (props.title || '').includes('Liquidity Map')) {
            const p1 = points[0]?.price, p2 = points[1]?.price;
            rects.push({ top: Math.max(p1, p2), bottom: Math.min(p1, p2) });
          }

          if (shape.name === 'text') {
            const text = (props.text || '').trim();
            const price = points[0]?.price;
            if (text && price != null) textLabels.push({ text, price });
          }
        } catch (e) {}
      }

      // Log all found labels for debugging (first level scrape only)
      if (lastLevelScrape === 0 || scrapeCount < 5) {
        console.log('[TradeReaper] Chart ' + chartIndex + ' (' + instrumentName + ') found ' + textLabels.length + ' text labels:');
        for (const l of textLabels) {
          console.log('  Label: "' + l.text + '" @ price ' + l.price);
        }
        console.log('[TradeReaper] Found ' + rects.length + ' rectangles');
      }

      // Second pass: match text labels to rectangles for zone ranges
      // Use case-insensitive matching and includes() for robustness
      for (const label of textLabels) {
        const t = label.text.trim();
        const tLower = t.toLowerCase();

        if (tLower.includes('bull zone') || tLower.includes('bull_zone') || tLower.startsWith('bull z')) {
          // Bull Zone label is at the BOTTOM of the zone
          let bestRect = null, bestDist = Infinity;
          for (const r of rects) {
            const dist = Math.abs(r.bottom - label.price);
            if (dist < bestDist) { bestDist = dist; bestRect = r; }
          }
          if (bestRect && bestDist < 50) {
            levels.bullZones.push({ bottom: bestRect.bottom, top: bestRect.top });
          } else {
            levels.bullZones.push({ bottom: label.price, top: label.price });
          }
        }
        else if (tLower.includes('bear zone') || tLower.includes('bear_zone') || tLower.startsWith('bear z')) {
          // Bear Zone label is at the TOP of the zone
          let bestRect = null, bestDist = Infinity;
          for (const r of rects) {
            const dist = Math.abs(r.top - label.price);
            if (dist < bestDist) { bestDist = dist; bestRect = r; }
          }
          if (bestRect && bestDist < 50) {
            levels.bearZones.push({ bottom: bestRect.bottom, top: bestRect.top });
          } else {
            levels.bearZones.push({ bottom: label.price, top: label.price });
          }
        }
        else if (/^mhp\b/i.test(t) || tLower === 'mhp') {
          // MHP must be checked BEFORE HP (since "MHP" contains "HP")
          levels.mhp = label.price;
        }
        else if (/^hp\b/i.test(t) || (tLower.startsWith('hp') && !tLower.startsWith('hg'))) {
          levels.hp = label.price;
        }
        else if (/^hg\b/i.test(t) || tLower.startsWith('hg')) {
          levels.hg = label.price;
        }
        else if (tLower.startsWith('dd') && (tLower.includes(':') || tLower.includes(' '))) {
          if (levels.dd.upper === null) {
            levels.dd.upper = label.price;
          } else if (label.price > levels.dd.upper) {
            levels.dd.lower = levels.dd.upper;
            levels.dd.upper = label.price;
          } else {
            levels.dd.lower = label.price;
          }
        }
        else if (tLower.includes('open') && !tLower.includes('close')) {
          levels.open = label.price;
        }
        else if (tLower.includes('close')) {
          levels.close = label.price;
        }
      }

      // Sort zones by price
      levels.bullZones.sort((a, b) => a.bottom - b.bottom);
      levels.bearZones.sort((a, b) => a.bottom - b.bottom);

      // Fix DD ordering
      if (levels.dd.upper !== null && levels.dd.lower !== null && levels.dd.upper < levels.dd.lower) {
        [levels.dd.upper, levels.dd.lower] = [levels.dd.lower, levels.dd.upper];
      }

      return levels;
    } catch (e) {
      console.warn(`[TradeReaper] Chart ${chartIndex} error:`, e.message);
      return null;
    }
  }

  function detectCharts() {
    const charts = [];
    try {
      for (let i = 0; i < 4; i++) {
        try {
          const chart = window.tvWidget.chart(i);
          if (!chart) break;
          const symbol = chart.symbol ? chart.symbol() : '';
          let name = 'Unknown';
          if (symbol.includes('ENQ') || symbol.includes('NQ') || symbol.includes('MNQ')) name = 'NQ';
          else if (symbol.includes('EP') || symbol.includes('ES') || symbol.includes('MES') || symbol.includes('SPY')) name = 'ES';
          else if (symbol.includes('RTY') || symbol.includes('M2K') || symbol.includes('IWM')) name = 'RTY';
          else name = symbol;
          charts.push({ index: i, symbol, name });
        } catch (e) { break; }
      }
    } catch (e) {}
    return charts;
  }

  // ----------------------------------------------------------
  // MAIN SCRAPE + SEND
  // ----------------------------------------------------------
  function scrapeAndSend() {
    try {
      if (!window.tvWidget?.activeChart) {
        console.warn('[TradeReaper] Waiting for TradingView widget...');
        return;
      }

      // Fast data (every cycle)
      const fastData = scrapeFastData();

      // Slow data (chart levels — fast at open, slower after warmup)
      const now = Date.now();
      const levelInterval = (now - startTime < WARMUP_DURATION_MS) ? LEVEL_WARMUP_MS : LEVEL_STEADY_MS;
      if (now - lastLevelScrape >= levelInterval) {
        const charts = detectCharts();
        cachedLevels = {};
        for (const c of charts) {
          const levels = scrapeChartLevels(c.index, c.name);
          if (levels) cachedLevels[c.name] = levels;
        }
        lastLevelScrape = now;
      }

      // Build payload
      const payload = {
        scrapeTime: new Date().toISOString(),
        scrapeCount: ++scrapeCount,
        // Fast-changing data
        sp500Resilience: fastData.sp500Resilience,
        nq100Resilience: fastData.nq100Resilience,
        ru2kResilience: fastData.ru2kResilience,
        ddRatio: fastData.ddRatio,
        livePrice: fastData.livePrice,
        alertDirection: fastData.alertDirection,
        headerHP: fastData.headerHP,
        headerMHP: fastData.headerMHP,
        // Slow-changing data
        chartLevels: cachedLevels
      };

      // Send
      fetch(BRIDGE_URL, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      })
        .then(res => {
          if (!res.ok) throw new Error(`HTTP ${res.status}`);
          if (lastError) { console.log('[TradeReaper] Reconnected!'); lastError = null; }
        })
        .catch(err => {
          if (!lastError || lastError !== err.message) {
            console.warn(`[TradeReaper] Bridge error: ${err.message}`);
            lastError = err.message;
          }
        });

      // Periodic log
      if (scrapeCount % 120 === 1) {
        console.log(
          `[TradeReaper] #${scrapeCount} | ` +
          `SP Res: ${fastData.sp500Resilience.res}/${fastData.sp500Resilience.sp}/${fastData.sp500Resilience.hp} | ` +
          `NQ Res: ${fastData.nq100Resilience.res}/${fastData.nq100Resilience.sp}/${fastData.nq100Resilience.hp} | ` +
          `DD: ${fastData.ddRatio} | ` +
          `SPY: ${fastData.livePrice.SPY} QQQ: ${fastData.livePrice.QQQ}`
        );
      }
    } catch (e) {
      console.error('[TradeReaper] Scrape error:', e);
    }
  }

  // ----------------------------------------------------------
  // START
  // ----------------------------------------------------------
  console.log('%c[TradeReaper] Level Scraper v3.0 Starting...', 'color: #0f0; font-size: 14px');
  console.log(`[TradeReaper] Fast data every ${FAST_INTERVAL_MS / 1000}s, levels every ${LEVEL_WARMUP_MS / 1000}s (warmup) → ${LEVEL_STEADY_MS / 1000}s (steady)`);
  console.log(`[TradeReaper] Sending to: ${BRIDGE_URL}`);

  setTimeout(scrapeAndSend, 2000);
  const intervalId = setInterval(scrapeAndSend, FAST_INTERVAL_MS);

  window.stopTradeReaper = () => { clearInterval(intervalId); console.log('[TradeReaper] Stopped.'); };
  console.log('[TradeReaper] To stop: run stopTradeReaper()');
})();
