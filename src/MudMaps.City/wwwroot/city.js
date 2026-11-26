// City map scripts: traders on the roads, hover tooltips, label and banner fitting, and the journey between levels
// (a caravan walks to the destination while the camera follows it there, and a banner unfurls on arrival).
// Blazor draws the world; this module only animates and positions.

const NS = 'http://www.w3.org/2000/svg';

// The shared label fitter comes from the versioned URL the page passes to init(), never a cached older copy. The
// stylesheets are asked for again under this build's version too, so an old cached app.css never dresses a new page.
let fitLabels = () => {};
export async function init(zoomModule, version) {
  ({ fitLabels } = await import(new URL(zoomModule, document.baseURI).href));
  if (version) {
    for (const link of document.querySelectorAll('link[rel="stylesheet"]')) {
      const href = link.getAttribute('href') || '';
      if (!/^(css\/app\.css|_content\/MudMaps\.Shared\/mudmaps\.css)(\?|$)/.test(href) || href.includes(`v=${version}`)) continue;
      const fresh = link.cloneNode();
      fresh.href = `${href.split('?')[0]}?v=${version}`;
      fresh.addEventListener('load', () => link.remove(), { once: true });
      link.after(fresh);
    }
  }
}

const reduced = () => matchMedia('(prefers-reduced-motion: reduce)').matches;
const box = svg => svg.getAttribute('viewBox').split(/[\s,]+/).map(Number);
const setBox = (svg, v) => svg.setAttribute('viewBox', v.map(n => n.toFixed(2)).join(' '));
const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));
const smooth = k => k * k * (3 - 2 * k);
const easeInOut = k => (k < .5 ? 4 * k * k * k : 1 - Math.pow(-2 * k + 2, 3) / 2);

// ---- Labels -------------------------------------------------------------------------------------------------
// Every label is measured and cut to its plate; banners then fit their plate round the badge and the measured name.
const fitted = new WeakSet();
export function fit(svg, scale) {
  fitLabels(svg, scale);
  fitBanners(svg);
  fitTiles(svg, scale);
  if (fitted.has(svg)) return;
  fitted.add(svg);
  let queued = 0;
  const again = () => { cancelAnimationFrame(queued); queued = requestAnimationFrame(() => { fitLabels(svg); fitBanners(svg); fitTiles(svg); }); };
  new ResizeObserver(again).observe(svg);
  document.fonts?.ready.then(again);
  document.fonts?.addEventListener?.('loadingdone', again);
}

function fitBanners(svg) {
  for (const g of svg.querySelectorAll('g[data-banner]')) {
    const name = g.querySelector('.banner-name');
    const plate = g.querySelector('.banner-plate');
    const badge = g.querySelector('.banner-badge');
    const pop = g.querySelector('.banner-pop');
    if (!name || !plate || !badge || !pop) continue;
    const f = parseFloat(name.getAttribute('font-size')) || 16;
    const cx = +g.dataset.cx, cy = +g.dataset.cy;
    const tw = name.getComputedTextLength();
    const bw = f * 1.9, h = f * 1.6, pad = f * .5;
    const w = bw + pad + tw + pad * 1.3;
    const x0 = cx - w / 2;
    for (const r of [plate, badge]) { r.setAttribute('y', (cy - h / 2).toFixed(2)); r.setAttribute('height', h.toFixed(2)); r.setAttribute('rx', (f * .3).toFixed(2)); }
    plate.setAttribute('x', x0.toFixed(2)); plate.setAttribute('width', w.toFixed(2));
    badge.setAttribute('x', x0.toFixed(2)); badge.setAttribute('width', bw.toFixed(2));
    pop.setAttribute('font-size', (f * .88).toFixed(2));
    pop.setAttribute('x', (x0 + bw / 2).toFixed(2)); pop.setAttribute('y', (cy + f * .32).toFixed(2));
    name.setAttribute('x', (x0 + bw + pad).toFixed(2)); name.setAttribute('y', (cy + f * .35).toFixed(2));
    name.setAttribute('text-anchor', 'start');
  }
}

// Tile and room names: about 13 px on screen (never under 12), one line when it fits, else broken over two lines at
//   the space or capital that balances them best, and cut with an ellipsis only if a line still does not fit.
//   <g data-wrap data-full data-cx data-cy data-maxw data-maxfont> [<rect plate>] <text line 1> <text line 2>
function fitTiles(svg, scaleOverride) {
  const ctm = scaleOverride ? null : svg.getScreenCTM();
  const scale = scaleOverride || (ctm ? Math.hypot(ctm.a, ctm.b) : 0);
  if (!scale) return;
  for (const g of svg.querySelectorAll('g[data-wrap]')) {
    const inner = g.querySelector('g');
    const [a, b] = inner ? inner.querySelectorAll('text') : [];
    if (!a || !b) continue;
    const d = g.dataset, full = d.full || '', cx = +d.cx, cy = +d.cy;
    const na = textNode(a), nb = textNode(b);
    // The text is set at 16 units in a group scaled to the font size wanted (f map units), so widths are in 16ths of f.
    const width = (n, t, v) => { n.nodeValue = v; return t.getComputedTextLength(); };
    let f = 0, k = 1, maxw = 0, lines = [full];
    for (const px of [13, 12]) {
      f = Math.max(12 / scale, Math.min(px / scale, +d.maxfont || Infinity));
      k = f / 16;
      maxw = +d.maxw / k;
      lines = [full];
      let worst = width(na, a, full);
      if (worst > maxw) {
        let best = null;
        for (let i = 1; i < full.length; i++) {
          const at = full[i] === ' ' || (/[a-z0-9]/.test(full[i - 1]) && /[A-Z]/.test(full[i])) || full[i - 1] === '&';
          if (!at) continue;
          const l1 = full.slice(0, i).trimEnd(), l2 = full.slice(i).trimStart();
          const w2 = Math.max(width(na, a, l1), width(nb, b, l2));
          if (!best || w2 < best.w2) best = { l1, l2, w2 };
        }
        if (best) { lines = [best.l1, best.l2]; worst = best.w2; }
      }
      if (worst <= maxw) break;
    }
    const cut = (n, t, v) => {
      if (width(n, t, v) <= maxw) return;
      let lo = 0, hi = v.length - 1;
      while (lo < hi) { const mid = (lo + hi + 1) >> 1; if (width(n, t, v.slice(0, mid).trimEnd() + '…') <= maxw) lo = mid; else hi = mid - 1; }
      n.nodeValue = v.slice(0, Math.max(1, lo)).trimEnd() + '…';
    };
    cut(na, a, lines[0]);
    if (lines.length > 1) cut(nb, b, lines[1]); else nb.nodeValue = '';
    const two = lines.length > 1;
    inner.setAttribute('transform', `translate(${cx.toFixed(2)},${cy.toFixed(2)}) scale(${k.toFixed(5)})`);
    a.setAttribute('y', (two ? -4 : 5.6).toFixed(1));
    b.setAttribute('y', '15.2');
    const plate = g.querySelector(':scope > rect');
    if (plate) {
      const w = (Math.max(a.getComputedTextLength(), two ? b.getComputedTextLength() : 0) + 17.6) * k;
      const h = f * (two ? 2.55 : 1.45);
      plate.setAttribute('x', (cx - w / 2).toFixed(2));
      plate.setAttribute('y', (cy - (two ? .95 : .72) * f).toFixed(2));
      plate.setAttribute('width', w.toFixed(2));
      plate.setAttribute('height', h.toFixed(2));
      plate.setAttribute('rx', (f * .55).toFixed(2));
    }
  }
}

// Blazor owns each label's text node and rewrites it only when the name changes (fitting runs again then).
function textNode(t) {
  let n = t.firstChild;
  if (!n || n.nodeType !== Node.TEXT_NODE) { t.textContent = ' '; n = t.firstChild; }
  return n;
}

/** Fit labels as the given view will show them (for a level drawn before the camera gets there). */
export function fitFor(svg, view) {
  const w = svg.clientWidth || svg.getBoundingClientRect().width;
  fit(svg, w && view ? w / view[2] : undefined);
}

/** Put the camera on a view at once (deep links, redraws, Skip animations). */
export function jump(svg, view) {
  if (view) setBox(svg, view);
}

// On a phone the map is wider than the screen and pans sideways: start each new view centred.
export function centre(svg) {
  const pan = svg.parentElement;
  if (!pan || pan.scrollWidth <= pan.clientWidth) return;
  pan.scrollLeft = (pan.scrollWidth - pan.clientWidth) / 2;
}

// ---- Traders and tooltips -------------------------------------------------------------------------------------
const running = new WeakMap();

export function start(svg) {
  bindTips(svg);
  const layer = svg.querySelector('#traffic');
  if (!layer) return;
  layer.replaceChildren();
  const roads = [...svg.querySelectorAll('path.route')].map(p => ({ p, len: p.getTotalLength(), w: Math.sqrt(+p.dataset.heat || 0) }));
  const total = roads.reduce((t, x) => t + x.w, 0);
  if (!roads.length || !total) return;
  let seed = 21;
  const rnd = () => ((seed = (seed * 1664525 + 1013904223) >>> 0) / 4294967296);
  const dots = [];
  for (let i = 0; i < 18; i++) {
    let x = rnd() * total, k = 0;
    while (x > roads[k].w && k < roads.length - 1) { x -= roads[k].w; k++; }
    dots.push({ g: makeTrader(layer), t: roads[k], pos: rnd() * roads[k].len, dir: rnd() < .5 ? 1 : -1, speed: .2 + rnd() * .3 });
  }
  const prior = running.get(svg);
  if (prior) { prior.dots = dots; return; }
  const s = { dots };
  running.set(svg, s);
  let last = performance.now();
  const frame = now => {
    const dt = Math.min(50, now - last); last = now;
    if (!svg.isConnected) return;
    const scale = Math.max(.25, 1.5 * box(svg)[2] / 1600);
    const still = reduced();
    for (const d of s.dots) {
      if (!still) {
        d.pos += d.dir * d.speed * dt * .05;
        if (d.pos > d.t.len || d.pos < 0) { d.dir *= -1; d.pos = clamp(d.pos, 0, d.t.len); }
      }
      const p = d.t.p.getPointAtLength(d.pos);
      d.g.setAttribute('transform', `translate(${p.x.toFixed(1)},${p.y.toFixed(1)}) scale(${scale.toFixed(3)})`);
    }
    requestAnimationFrame(frame);
  };
  requestAnimationFrame(frame);
}

function makeTrader(layer) {
  const g = document.createElementNS(NS, 'g');
  const c = document.createElementNS(NS, 'circle');
  c.setAttribute('r', '2.4');
  c.setAttribute('fill', '#ffd27a');
  c.setAttribute('stroke', '#2a1d14');
  c.setAttribute('stroke-width', '.8');
  g.appendChild(c);
  layer.appendChild(g);
  return g;
}

const tipped = new WeakSet();

function bindTips(svg) {
  if (tipped.has(svg)) return;
  tipped.add(svg);
  const stage = svg.closest('.mm-stage');
  const tip = document.createElement('div');
  tip.className = 'mm-tip';
  tip.setAttribute('role', 'tooltip');
  tip.hidden = true;
  const head = document.createElement('div');
  head.className = 'mm-tip-title';
  const sub = document.createElement('div');
  sub.className = 'mm-tip-sub';
  tip.append(head, sub);
  stage.appendChild(tip);
  let current = null;

  const show = (el, x, y) => {
    if (!el.isConnected) return hide();
    if (el !== current) {
      current = el;
      head.textContent = el.dataset.tip || '';
      sub.textContent = el.dataset.sub || '';
      sub.hidden = !el.dataset.sub;
      tip.hidden = false;
    }
    const b = stage.getBoundingClientRect();
    const w = tip.offsetWidth, h = tip.offsetHeight;
    let left = x - b.left + 14, top = y - b.top + 18;
    if (left + w > b.width - 6) left = x - b.left - w - 14;
    if (top + h > b.height - 6) top = y - b.top - h - 14;
    tip.style.transform = `translate(${Math.max(6, left)}px, ${Math.max(6, top)}px)`;
  };
  const hide = () => { current = null; tip.hidden = true; };
  const target = e => { const t = e.target.closest?.('[data-tip]'); return t && svg.contains(t) ? t : null; };

  svg.addEventListener('pointermove', e => {
    if (e.pointerType === 'touch' || rides.has(svg)) return;
    const t = target(e);
    if (t) show(t, e.clientX, e.clientY); else if (current) hide();
  });
  svg.addEventListener('pointerleave', hide);
  svg.addEventListener('pointerdown', hide);
  svg.addEventListener('focusin', e => {
    const t = target(e);
    if (!t || !t.matches(':focus-visible')) return;
    const r = t.getBoundingClientRect();
    show(t, r.left + r.width / 2, r.top);
  });
  svg.addEventListener('focusout', hide);
  svg.addEventListener('mm-hide-tip', hide);
}

// ---- The journey ------------------------------------------------------------------------------------------
// travel() walks a caravan token from opts.from to opts.to along a gently curved path while the camera glides from
// the current view to opts.view (zooming smoothly, its centre drawn along with the caravan so it stays in sight).
// Near the end a banner unfurls over the stage with the destination's name. The promise resolves when the camera has
// arrived (the page swaps in the new level then); arrive() resolves when the banner has had its moment and gone.
// About 3.9 s in all, either way. Reduced motion: the camera cuts to the view under a short cross-fade.
const T = { travel: 3000, banner: 2150, unfurl: 520, beat: 900, fadeOut: 260 };
const rides = new WeakMap();

export function travel(svg, o, freezeMs = -1) {
  if (rides.has(svg) || !o?.view) return Promise.resolve(0);
  svg.dispatchEvent(new Event('mm-hide-tip'));
  const stage = svg.closest('.mm-stage');
  const from = box(svg), to = o.view;
  const s = { svg, stage, o, from, to, t0: 0, frame: 0, arrived: false, done: null, finished: null, resolve: null, freezeMs, banner: null };
  rides.set(svg, s);
  s.finished = new Promise(r => { s.done = r; });
  const promise = new Promise(r => { s.resolve = r; });
  s.log = { label: o.label, dir: o.dir, start: performance.now() };

  s.banner = makeBanner(stage, o);
  if (reduced()) {
    // A short cross-fade: the camera cuts to the destination while the map dips and the banner shows plainly.
    setBox(svg, to);
    svg.animate?.([{ opacity: .2 }, { opacity: 1 }], { duration: 260, easing: 'ease-out' });
    s.banner.style.clipPath = 'none';
    s.banner.animate?.([{ opacity: 0 }, { opacity: 1 }, { opacity: 1 }, { opacity: 0 }], { duration: 1100 });
    s.reducedEnd = performance.now() + 1100;
    s.resolve(1);
    s.arrived = true;
    return promise;
  }

  stage.classList.add('travelling', o.dir >= 0 ? 'inbound' : 'outbound');
  if (o.target) svg.querySelector(`.civ-city[data-repo="${CSS.escape(o.target)}"]`)?.classList.add('ride-target');
  s.layer = svg.querySelector('#ride');
  buildRideArt(s);
  // On a phone the map pans sideways inside its box: bring the middle of the map into the window as we set out.
  const pan = svg.parentElement;
  s.pan = pan && pan.scrollWidth > pan.clientWidth ? { el: pan, from: pan.scrollLeft, to: (pan.scrollWidth - pan.clientWidth) / 2, frac: pan.clientWidth / pan.scrollWidth } : null;

  if (freezeMs >= 0) {
    // Screenshot hook: step a synthetic clock to the given moment and leave the frame there.
    for (let t = 0; t <= freezeMs; t += 16) step(s, t);
    return Promise.resolve(0);
  }
  requestAnimationFrame(now => { s.t0 = now; tick(s, now); });
  return promise;
}

export function arrive(svg) {
  const s = rides.get(svg);
  if (!s) return Promise.resolve();
  if (s.reducedEnd) {
    return new Promise(r => setTimeout(() => { cleanup(s); r(); }, Math.max(0, s.reducedEnd - performance.now())));
  }
  return s.finished;
}

function tick(s, now) {
  if (step(s, now - s.t0)) requestAnimationFrame(t => tick(s, t));
}

// Bezier from a to b with the control point pushed sideways, so the road reads as travelled, not teleported.
function curve(o) {
  const [x0, y0] = o.from, [x1, y1] = o.to;
  const dx = x1 - x0, dy = y1 - y0, len = Math.hypot(dx, dy) || 1;
  const bend = Math.min(len * .16, 120) * (o.dir >= 0 ? 1 : -1);
  const cx = (x0 + x1) / 2 - dy / len * bend, cy = (y0 + y1) / 2 + dx / len * bend;
  return t => [(1 - t) * (1 - t) * x0 + 2 * (1 - t) * t * cx + t * t * x1, (1 - t) * (1 - t) * y0 + 2 * (1 - t) * t * cy + t * t * y1];
}

function el(name, attrs, parent) {
  const e = document.createElementNS(NS, name);
  for (const [k, v] of Object.entries(attrs)) e.setAttribute(k, v);
  parent?.appendChild(e);
  return e;
}

function buildRideArt(s) {
  const { layer, o } = s;
  layer.replaceChildren();
  s.curve = curve(o);
  const colour = o.colour || '#e9b949';
  s.path = el('path', { class: 'ride-path', 'vector-effect': 'non-scaling-stroke' }, layer);
  // The destination: a ring that pulses on the target tile.
  s.mark = el('g', {}, layer);
  el('circle', { r: '18', fill: 'none', stroke: colour, 'stroke-width': '3', 'vector-effect': 'non-scaling-stroke' }, s.mark);
  el('circle', { r: '4', fill: colour }, s.mark);
  // The caravan token: a round shield in the city's colour with a covered wagon and a pennant.
  s.token = el('g', {}, layer);
  el('ellipse', { cx: '0', cy: '15', rx: '13', ry: '4', fill: 'rgba(0,0,0,.45)' }, s.token);
  el('circle', { r: '15', fill: '#140d0a', stroke: colour, 'stroke-width': '3' }, s.token);
  el('path', { d: 'M-9,3 L9,3 L9,-1 L-9,-1 Z', fill: '#8a5a35' }, s.token);
  el('path', { d: 'M-8,-1 C-8,-11 8,-11 8,-1 Z', fill: '#efe2c6' }, s.token);
  el('circle', { cx: '-5', cy: '6', r: '3', fill: '#2a1d14', stroke: '#c9a77a', 'stroke-width': '1' }, s.token);
  el('circle', { cx: '5', cy: '6', r: '3', fill: '#2a1d14', stroke: '#c9a77a', 'stroke-width': '1' }, s.token);
  el('path', { d: 'M10,-12 L10,-30', stroke: '#e8dcc4', 'stroke-width': '1.6' }, s.token);
  s.flag = el('path', { d: 'M10,-30 L24,-26 L10,-21 Z', fill: colour }, s.token);
}

function makeBanner(stage, o) {
  const b = document.createElement('div');
  b.className = 'civ-arrival';
  b.setAttribute('role', 'status');
  b.style.setProperty('--civ-colour', o.colour || '#e9b949');
  for (const [cls, text] of [['k', o.kicker], ['n', o.label], ['s', o.sub]]) {
    if (!text) continue;
    const d = document.createElement('div');
    d.className = cls;
    d.textContent = text;
    b.appendChild(d);
  }
  // Keep the banner in the part of the stage the reader can see.
  const r = stage.getBoundingClientRect();
  b.style.top = `${Math.round(clamp(-r.top, 0, Math.max(0, r.height - 140)) + 18)}px`;
  stage.appendChild(b);
  return b;
}

// One frame of the journey at e ms; false once it has finished (or the screenshot hook froze it).
function step(s, e) {
  const { svg, from, to, o } = s;
  const k = clamp(e / T.travel, 0, 1);
  const p = easeInOut(k);

  // Caravan along its road.
  const [ux, uy] = s.curve(p);

  // Camera: width eases in log space (a steady zoom); the centre moves with the caravan, kept well inside the frame.
  const w = from[2] * Math.pow(to[2] / from[2], p);
  const h = w * from[3] / from[2];
  let cx = from[0] + from[2] / 2 + (to[0] + to[2] / 2 - from[0] - from[2] / 2) * p;
  let cy = from[1] + from[3] / 2 + (to[1] + to[3] / 2 - from[1] - from[3] / 2) * p;
  const hold = 1 - smooth(clamp((p - .8) / .2, 0, 1));
  const fx = (s.pan ? s.pan.frac * .42 : .36) * w, fy = .36 * h;
  cx += (clamp(cx, ux - fx, ux + fx) - cx) * hold;
  cy += (clamp(cy, uy - fy, uy + fy) - cy) * hold;
  if (k >= 1) setBox(svg, to); else setBox(svg, [cx - w / 2, cy - h / 2, w, h]);
  if (s.pan) s.pan.el.scrollLeft = s.pan.from + (s.pan.to - s.pan.from) * smooth(clamp(e / 700, 0, 1));

  // Constant on-screen sizes for the token, the ring and the path, whatever the zoom.
  const unit = (k >= 1 ? to[2] : w) / (svg.clientWidth || 1000);
  const bob = Math.sin(e / 110) * 1.6 * (1 - Math.abs(2 * k - 1));
  s.token.setAttribute('transform', `translate(${ux.toFixed(2)},${(uy - 6 * unit + bob * unit).toFixed(2)}) scale(${(unit * 1.35).toFixed(4)})`);
  s.flag.setAttribute('d', `M10,-30 L${(23 + Math.sin(e / 90) * 2).toFixed(1)},${(-26 + Math.sin(e / 70)).toFixed(1)} L10,-21 Z`);
  const pulse = 1 + .18 * Math.sin(e / 160);
  s.mark.setAttribute('transform', `translate(${o.to[0].toFixed(2)},${o.to[1].toFixed(2)}) scale(${(unit * pulse).toFixed(4)})`);
  // The road ahead of the caravan, shrinking as it goes.
  let d = '';
  for (let i = 0; i <= 32; i++) {
    const [x, y] = s.curve(p + (1 - p) * i / 32);
    d += `${i ? 'L' : 'M'}${x.toFixed(1)},${y.toFixed(1)}`;
  }
  s.path.setAttribute('d', d);
  s.path.style.opacity = String(clamp(e / 350, 0, 1) * (1 - smooth(clamp((k - .9) / .1, 0, 1))));

  // Refit the labels every few frames so names stay readable while the camera zooms.
  if (++s.frame % 4 === 0) { const sc = (svg.clientWidth || 1000) / w; fitLabels(svg, sc); fitBanners(svg); fitTiles(svg, sc); }

  // The arrival banner unfurls from its middle, holds, and folds away.
  const bk = clamp((e - T.banner) / T.unfurl, 0, 1);
  const end = T.travel + T.beat;
  const out = clamp((e - end + T.fadeOut) / T.fadeOut, 0, 1);
  const open = 50 * (1 - smooth(bk));
  s.banner.style.clipPath = `inset(0 ${open.toFixed(2)}% 0 ${open.toFixed(2)}%)`;
  s.banner.style.opacity = String(bk > 0 ? 1 - out : 0);

  if (k >= 1 && !s.arrived) { s.arrived = true; s.resolve(1); }
  if (s.freezeMs >= 0 && e >= s.freezeMs) return false;
  if (e >= end) {
    s.log.ms = Math.round(performance.now() - s.log.start);
    (window.mmRideLog ??= []).push(s.log);
    cleanup(s);
    return false;
  }
  return true;
}

function cleanup(s) {
  s.layer?.replaceChildren();
  s.banner?.remove();
  s.stage.classList.remove('travelling', 'inbound', 'outbound');
  s.svg.querySelectorAll('.ride-target').forEach(n => n.classList.remove('ride-target'));
  rides.delete(s.svg);
  s.done?.();
}
