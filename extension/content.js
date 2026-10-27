(() => {
  'use strict';
  const APP = 730, CTX = 2;
  const CURRENCY = {1:'USD',2:'GBP',3:'EUR',4:'CHF',5:'RUB',6:'PLN',7:'BRL',8:'JPY',9:'NOK',10:'IDR',11:'MYR',12:'PHP',13:'SGD',14:'THB',15:'VND',16:'KRW',17:'TRY',18:'UAH',19:'MXN',20:'CAD',21:'AUD',22:'NZD',23:'CNY',24:'INR',25:'CLP',26:'PEN',27:'COP',28:'ZAR',29:'HKD',30:'TWD',31:'SAR',32:'AED'};

  const sleep = ms => new Promise(r => setTimeout(r, ms));
  const S = { steamid: null, wallet: null, groups: [], busy: false, stop: false, delay: 400, next: 0 };

  // ---------- UI ----------
  const fab = document.createElement('button');
  fab.id = 'cs2is-fab';
  fab.textContent = '⚡ Instant Sell';
  const panel = document.createElement('div');
  panel.id = 'cs2is-panel';
  panel.innerHTML = `
    <h3>CS2 Instant Sell</h3>
    <div class="row">
      <label>Min. payout per item <input id="cs2is-min" type="number" step="0.01" min="0" value="0.01"></label>
      <label>Max. price drop % <input id="cs2is-drop" type="number" step="1" min="0" max="100" value="20"></label>
      <label>No drop limit below <input id="cs2is-free" type="number" step="0.01" min="0" value="0.50"></label>
    </div>
    <div class="row">
      <button id="cs2is-scan">1. Scan inventory</button>
      <button id="cs2is-sell" disabled>2. Sell selected</button>
      <button id="cs2is-stop" class="danger" disabled>Stop</button>
      <button id="cs2is-copy">Copy log</button>
      <button id="cs2is-dump">Dump item</button>
    </div>
    <div id="cs2is-summary"></div>
    <div id="cs2is-list"></div>
    <div id="cs2is-log"></div>`;
  document.body.append(fab, panel);
  const $ = id => panel.querySelector('#cs2is-' + id);
  fab.onclick = () => panel.classList.toggle('open');

  const log = msg => {
    const el = $('log');
    el.textContent += msg + '\n';
    el.scrollTop = el.scrollHeight;
  };
  const money = cents => {
    const code = CURRENCY[S.wallet?.wallet_currency] || '';
    return (cents / 100).toFixed(2) + ' ' + code;
  };

  // ---------- Steam helpers ----------
  // Adaptive pacing: every request reserves a time slot (~0.4s apart). A 429 slows everything down and pauses all workers.
  async function req(url, opts = {}, tries = 5) {
    for (let i = 0; i < tries; i++) {
      if (S.stop) throw new Error('Stopped');
      const now = Date.now();
      const at = Math.max(now, S.next);
      S.next = at + S.delay;
      if (at > now) await sleep(at - now);
      if (S.stop) throw new Error('Stopped');
      const r = await fetch(url, { credentials: 'include', ...opts });
      if (r.status === 429) {
        S.delay = Math.min(S.delay * 1.6, 10000);
        const w = 10 * (i + 1);
        S.next = Math.max(S.next, Date.now() + w * 1000);
        log(`Rate limited, pausing ${w}s (pace now ${(S.delay / 1000).toFixed(1)}s per request)…`);
        continue;
      }
      S.delay = Math.max(400, S.delay * 0.97);
      return r;
    }
    throw new Error('Steam keeps rate limiting (429). Wait a few minutes and try again.');
  }

  async function init() {
    const t = await (await req('/market/')).text();
    const id = t.match(/g_steamID\s*=\s*"(\d+)"/);
    const w = t.match(/g_rgWalletInfo\s*=\s*(\{.*?\});/);
    if (!id || !w) throw new Error('Could not read account info. Are you logged in?');
    S.steamid = id[1];
    S.wallet = JSON.parse(w[1]);
  }

  async function loadInventory() {
    const assets = [], desc = new Map();
    let start = '';
    for (;;) {
      const url = `/inventory/${S.steamid}/${APP}/${CTX}?l=english&count=2000` + (start ? `&start_assetid=${start}` : '');
      const r = await req(url);
      if (!r.ok) throw new Error('Inventory request failed: HTTP ' + r.status);
      const j = await r.json();
      if (!j || !j.assets) break;
      j.descriptions.forEach(d => desc.set(d.classid + '_' + d.instanceid, d));
      assets.push(...j.assets);
      if (j.more_items) { start = j.last_assetid; await sleep(1500); } else break;
    }
    S.inv = [...desc.values()];
    const groups = new Map();
    let unmarketable = 0, locked = 0;
    // Diagnostics: which fields exist on how many items (reveals where Steam stores the trade lock).
    const fields = {}, assetFields = {};
    let lockSample = null, lockCount = 0;
    const LOCK_RE = /(tradable|marketable|trade).{0,20}(after|protect|until)|trade[- ]?protect|trade[- ]?lock/i;
    const lockLine = d => [...(d.owner_descriptions || []), ...(d.descriptions || [])].map(o => o.value || '').find(v => LOCK_RE.test(v));
    for (const a of assets) {
      const d = desc.get(a.classid + '_' + a.instanceid);
      for (const k of Object.keys(a)) assetFields[k] = (assetFields[k] || 0) + 1;
      if (d) {
        for (const k of Object.keys(d)) fields[k] = (fields[k] || 0) + 1;
        const line = lockLine(d);
        if (line) { lockCount++; if (!lockSample) lockSample = { name: d.market_hash_name, line }; }
      }
      if (!d || d.marketable !== 1) { unmarketable++; continue; }
      // Trade-protected items are still flagged marketable by Steam, so check the lock explicitly.
      const lockedText = !!lockLine(d);
      const exp = d.cache_expiration ? Date.parse(String(d.cache_expiration).replace(/[()]/g, '')) : NaN;
      const lockedTime = d.cache_expiration && (isNaN(exp) || exp > Date.now());
      if (d.tradable !== 1 || lockedText || lockedTime) { locked++; continue; }
      let g = groups.get(d.market_hash_name);
      if (!g) {
        g = { name: d.market_hash_name, label: d.name, icon: d.icon_url, assetids: [], plan: [], checked: true, status: '' };
        groups.set(d.market_hash_name, g);
      }
      g.assetids.push(a.assetid);
    }
    return { groups: [...groups.values()], unmarketable, locked, fields, assetFields, lockSample, total: assets.length };
  }

  // Steam's new market pages no longer contain item_nameid, so the name-based orderbook endpoint is used.
  // Returns [[priceCents, cumulativeQty], ...] sorted from highest buy order down.
  async function getBuyGraph(name) {
    const w = S.wallet;
    const qp = JSON.stringify([APP, name]);
    const url = `/market/orderbook?q=Load&qp=${encodeURIComponent(qp)}&cc=${w.wallet_country}&l=english&currency=${w.wallet_currency}`;
    const r = await req(url, {
      headers: { 'x-valve-request-type': 'queryAction', 'Accept': 'application/json' },
      referrer: `https://steamcommunity.com/market/listings/${APP}/${encodeURIComponent(name)}`
    });
    const text = await r.text();
    let j;
    try { j = JSON.parse(text); } catch { throw new Error(`orderbook HTTP ${r.status}, not JSON: ${text.replace(/\s+/g, ' ').slice(0, 150)}`); }
    const d = j && j.data && j.data.data ? j.data.data : (j && j.data) || j;
    if (!d || !Array.isArray(d.rgCompactBuyOrders)) throw new Error(`orderbook HTTP ${r.status}, unexpected reply: ${text.slice(0, 150)}`);
    const flat = d.rgCompactBuyOrders, levels = [];
    for (let i = 0; i + 1 < flat.length; i += 2) levels.push([flat[i], flat[i + 1]]);
    levels.sort((x, y) => y[0] - x[0]);
    let cum = 0;
    const out = levels.map(l => [l[0], (cum += l[1])]);
    out.raw = text.slice(0, 300);
    return out;
  }

  // Steam's own fee logic (same as the market sell dialog)
  function feesFor(amount, pubPct) {
    const w = S.wallet;
    const steam = Math.floor(Math.max(amount * parseFloat(w.wallet_fee_percent), parseInt(w.wallet_fee_minimum)) + parseInt(w.wallet_fee_base || 0));
    const pub = pubPct > 0 ? Math.floor(Math.max(amount * pubPct, 1)) : 0;
    return steam + pub;
  }
  // buyer pays `p` -> what the seller receives
  function sellerReceives(p) {
    const w = S.wallet;
    const pub = parseFloat(w.wallet_publisher_fee_percent_default ?? 0.1);
    const base = parseInt(w.wallet_fee_base || 0);
    let e = Math.floor((p - base) / (parseFloat(w.wallet_fee_percent) + pub + 1));
    while (e + feesFor(e + 1, pub) <= p) e++;
    while (e > 0 && e + feesFor(e, pub) > p) e--;
    return e;
  }

  // For N units, walk down the buy-order book (cumulative graph), stop at max drop / min payout.
  function planUnits(graph, n, maxDropPct, minCents, freeBelowCents = 0) {
    if (!graph.length) return [];
    const top = graph[0][0];
    const floor = top < freeBelowCents ? 0 : top * (1 - maxDropPct / 100); // cheap items ignore the drop limit
    const plan = [];
    for (let k = 0; k < n; k++) {
      const lvl = graph.find(g => g[1] > k);
      if (!lvl) break;
      const buyer = lvl[0];
      if (buyer < floor) break;
      const recv = sellerReceives(buyer);
      if (recv < minCents || recv < 1) break;
      plan.push({ buyer, recv });
    }
    return plan;
  }

  // Recompute what can be sold from the stored buy-order ladder (no new requests).
  const freeBelow = () => Math.round(parseFloat($('free').value || 0) * 100);
  function plan(g) {
    const minCents = Math.round(parseFloat($('min').value || 0) * 100);
    const drop = parseFloat($('drop').value || 0);
    g.plan = planUnits(g.graph || [], g.assetids.length, drop, minCents, freeBelow());
    g.status = g.plan.length ? '' : (g.graph && g.graph.length ? 'too low' : 'no buyers');
  }
  function replan() {
    if (S.busy || !S.groups.length) return;
    S.groups.forEach(plan);
    render();
  }
  $('min').onchange = replan;
  $('drop').onchange = replan;
  $('free').onchange = replan;

  // ---------- render ----------
  function render() {
    const rows = S.groups.map((g, i) => {
      const net = g.plan.reduce((s, u) => s + u.recv, 0);
      const first = g.plan[0];
      return `<tr class="${g.plan.length ? '' : 'skip'}">
        <td><input type="checkbox" data-i="${i}" ${g.checked && g.plan.length ? 'checked' : ''} ${g.plan.length ? '' : 'disabled'}></td>
        <td><img src="https://community.cloudflare.steamstatic.com/economy/image/${g.icon}/64fx48f"></td>
        <td class="n"></td>
        <td class="num">${g.plan.length}/${g.assetids.length}</td>
        <td class="num">${first ? money(first.recv) : (g.status || '–')}</td>
        <td class="num">${net ? money(net) : ''}</td></tr>`;
    }).join('');
    $('list').innerHTML = `<table><tr><th></th><th></th><th>Item</th><th class="num">Sellable</th><th class="num">You get</th><th class="num">Total</th></tr>${rows}</table>`;
    $('list').querySelectorAll('td.n').forEach((td, i) => td.textContent = S.groups[i].name);
    $('list').querySelectorAll('input[type=checkbox]').forEach(cb => {
      cb.onchange = () => { S.groups[cb.dataset.i].checked = cb.checked; summary(); };
    });
    summary();
  }

  function summary() {
    const sel = S.groups.filter(g => g.checked && g.plan.length);
    const n = sel.reduce((s, g) => s + g.plan.length, 0);
    const tot = sel.reduce((s, g) => s + g.plan.reduce((a, u) => a + u.recv, 0), 0);
    $('summary').textContent = S.groups.length ? `Selected: ${n} items → about ${money(tot)} (after fees)` : '';
    $('sell').disabled = S.busy || !n;
  }

  function setBusy(b) {
    S.busy = b;
    $('scan').disabled = b;
    $('stop').disabled = !b;
    summary();
  }

  // ---------- actions ----------
  $('scan').onclick = async () => {
    S.stop = false; setBusy(true); $('log').textContent = ''; $('list').innerHTML = '';
    try {
      await init();
      log('Loading CS2 inventory…');
      const inv = await loadInventory();
      S.groups = inv.groups;
      log(`${inv.total} items: ${inv.unmarketable} flagged not marketable, ${inv.locked} lock markers found (Steam's data may not expose trade locks, so check the list yourself), ${inv.groups.length} unique sellable items.`);
      // Diagnostics: show raw data of items that look locked, so the lock detection can be verified.
      log('DEBUG lock sample: ' + JSON.stringify(inv.lockSample));
      const minCents = Math.round(parseFloat($('min').value || 0) * 100);
      const drop = parseFloat($('drop').value || 0);
      let okCount = 0, failStreak = 0, ladderLogged = false, done = 0, next = 0, abort = false;
      const worker = async () => {
        while (!abort && !S.stop) {
          const i = next++;
          if (i >= S.groups.length) return;
          const g = S.groups[i];
          try {
            const graph = await getBuyGraph(g.name);
            g.graph = graph;
            plan(g);
            if (!ladderLogged && g.plan.length < g.assetids.length) {
              ladderLogged = true;
              log(`DEBUG ladder for ${g.name} (price in cents x cumulative orders): ` + (graph.length ? graph.slice(0, 10).map(l => l[0] + 'x' + l[1]).join('  ') : 'EMPTY, raw reply: ' + graph.raw));
            }
            okCount++; failStreak = 0;
          } catch (e) {
            if (e.message === 'Stopped') return;
            g.status = 'error'; log(`${g.name}: ${e.message}`);
            if (++failStreak >= 3 && okCount === 0) {
              log('Stopping: the first 3 lookups all failed, so continuing would just waste time. Copy this log and send it to me.');
              abort = true;
            }
          }
          done++;
          const mins = Math.ceil((S.groups.length - done) * S.delay / 60000);
          $('summary').textContent = `Pricing ${done}/${S.groups.length} (~${mins} min left)`;
        }
      };
      await Promise.all([worker(), worker(), worker()]);
      if (S.stop) throw new Error('Stopped');
      render();
      log('Scan done. Review the list, untick what you want to keep, then press "Sell selected".');
    } catch (e) { log('Error: ' + e.message); }
    setBusy(false);
  };

  $('sell').onclick = async () => {
    const sel = S.groups.filter(g => g.checked && g.plan.length);
    const n = sel.reduce((s, g) => s + g.plan.length, 0);
    const tot = sel.reduce((s, g) => s + g.plan.reduce((a, u) => a + u.recv, 0), 0);
    if (!confirm(`Sell up to ${n} items for about ${money(tot)}?\nBuy orders are re-checked per item right before selling, so fewer may be listed. Filled sales cannot be undone.`)) return;
    S.stop = false; setBusy(true);
    const sessionid = (document.cookie.match(/(?:^|;\s*)sessionid=([^;]+)/) || [])[1];
    if (!sessionid) { log('No sessionid cookie found. Reload the page.'); setBusy(false); return; }
    let ok = 0, fail = 0, confirmNeeded = 0;
    try {
      outer: for (const g of sel) {
        // Re-read the buy orders right before selling: they may have been used up since the scan.
        let units;
        try {
          const minCents = Math.round(parseFloat($('min').value || 0) * 100);
          const drop = parseFloat($('drop').value || 0);
          units = planUnits(await getBuyGraph(g.name), g.plan.length, drop, minCents, freeBelow());
          if (units.length < g.plan.length) log(`${g.name}: buy orders changed, only ${units.length} of ${g.plan.length} can still sell instantly.`);
        } catch (e) {
          if (e.message === 'Stopped') break outer;
          log(`${g.name}: skipped, could not re-check buy orders (${e.message})`);
          continue;
        }
        for (let i = 0; i < units.length; i++) {
          if (S.stop) break outer;
          const { recv } = units[i];
          const body = new URLSearchParams({ sessionid, appid: APP, contextid: CTX, assetid: g.assetids[i], amount: 1, price: recv });
          let j;
          try {
            const r = await req('/market/sellitem/', {
              method: 'POST',
              headers: { 'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8' },
              body
            });
            j = await r.json();
          } catch (e) {
            if (e.message === 'Stopped') break outer;
            j = { success: false, message: e.message };
          }
          if (j && j.success) {
            ok++;
            if (j.requires_confirmation || j.needs_mobile_confirmation || j.needs_email_confirmation) confirmNeeded++;
            log(`✓ ${g.name} → ${money(recv)}`);
          } else {
            fail++;
            log(`✗ ${g.name}: ${(j && j.message) || 'failed'}`);
          }
          $('summary').textContent = `Sold ${ok}, failed ${fail} of ${n}…`;
          await sleep(1500);
        }
      }
    } catch (e) { log('Error: ' + e.message); }
    log(`Finished: ${ok} listed, ${fail} failed.` + (confirmNeeded ? ` ${confirmNeeded} need confirmation in the Steam mobile app (Confirmations tab).` : ''));
    setBusy(false);
  };

  $('stop').onclick = () => { S.stop = true; log('Stopping…'); };
  $('dump').onclick = () => {
    const q = prompt('Part of an item name (e.g. a skin you know is trade-locked). Scan first.');
    if (!q) return;
    const m = (S.inv || []).filter(d => d.market_hash_name.toLowerCase().includes(q.toLowerCase())).slice(0, 2);
    if (!m.length) { log('Dump: no item found. Press Scan first.'); return; }
    m.forEach(d => { const { icon_url, icon_url_large, ...rest } = d; log('DUMP ' + JSON.stringify(rest)); });
  };
  $('copy').onclick = async () => {
    try { await navigator.clipboard.writeText($('log').textContent); $('copy').textContent = 'Copied!'; }
    catch { $('copy').textContent = 'Select log text manually'; }
    setTimeout(() => $('copy').textContent = 'Copy log', 2000);
  };
})();
