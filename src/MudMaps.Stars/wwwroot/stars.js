// Space map scripts. Blazor draws one level at a time (the whole sky, one star system, or one planet with its
// satellites); this module moves the orbiting bodies, keeps name plates a readable screen size (--u, chart units per
// screen pixel), cuts every name to fit inside its plate, hides a name while it would sit on a more important one,
// shows tooltips, and plays the rides between levels.
const state = new WeakMap();

const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));

export function reducedMotion() {
  return matchMedia('(prefers-reduced-motion: reduce)').matches;
}

// Phones get a tall chart. Returns whether the stage is narrow now and tells the page when that changes.
export function watchShape(svg, ref) {
  const narrow = () => (svg.parentElement?.getBoundingClientRect().width || innerWidth) < 640;
  let last = narrow(), timer = 0;
  addEventListener('resize', () => {
    clearTimeout(timer);
    timer = setTimeout(() => {
      const now = narrow();
      if (now !== last) { last = now; ref.invokeMethodAsync('ShapeChanged', now); }
    }, 150);
  });
  return last;
}

// (Re)start after Blazor has drawn a level: collect the orbiting bodies, size the plates, lay the names out.
export function start(svg) {
  let s = state.get(svg);
  const first = !s;
  if (first) {
    s = { orbs: [], paused: reducedMotion(), tipOff: false, u: 1, tick: 0 };
    state.set(svg, s);
    installTip(svg, s);
    const relayout = () => { scale(svg, s); fitPlates(svg, s, true); place(s); cull(svg); };
    svg.addEventListener('mapzoom', e => { scale(svg, s, e.detail?.width); fitPlates(svg, s, true); cull(svg); });
    addEventListener('resize', relayout);
    // The stage also changes width without the window resizing (the detail sheet opening beside it).
    if (window.ResizeObserver) new ResizeObserver(relayout).observe(svg);
    // Names are measured in the map's font; measure again once it has loaded.
    document.fonts?.ready.then(relayout);
    document.fonts?.addEventListener?.('loadingdone', relayout);
  }
  const angles = new Map(s.orbs.map(o => [o.key, o.a]));
  const labels = new Map([...svg.querySelectorAll('g.orb-lab')].map(g => [g.dataset.key, g]));
  s.orbs = [...svg.querySelectorAll('g.orb')].map(g => ({
    g, label: labels.get(g.dataset.key), key: g.dataset.key,
    cx: +g.dataset.cx, cy: +g.dataset.cy, rx: +g.dataset.rx, k: +g.dataset.k,
    a: angles.get(g.dataset.key) ?? +g.dataset.angle, speed: +g.dataset.speed,
  }));
  for (const o of s.orbs) { o.fit = o.label?.querySelector('.fit[data-below]'); o.below = +(o.fit?.dataset.below ?? 0); o.up = null; }
  // Conflict lines (review board open) run from a fixed pin to a moving satellite; place() keeps their far end on it.
  s.byKey = new Map(s.orbs.map(o => [o.key, o]));
  s.links = [...svg.querySelectorAll('line[data-orb]')];
  scale(svg, s);
  fitPlates(svg, s);
  place(s);
  cull(svg);
  if (first) {
    let last = performance.now();
    const frame = now => {
      const dt = Math.min(50, now - last); last = now;
      if (!s.paused) for (const o of s.orbs) o.a += o.speed * dt / 1000;
      place(s);
      if (s.tick-- <= 0) { cull(svg); s.tick = 10; }
      requestAnimationFrame(frame);
    };
    requestAnimationFrame(frame);
  }
}

function scale(svg, s, width) {
  const px = svg.getBoundingClientRect().width || 1;
  s.vw = +svg.getAttribute('viewBox').split(/\s+/)[2];
  s.u = (width ?? s.vw) / px;
  svg.style.setProperty('--u', s.u.toFixed(4));
  // The docking plate hangs above its beacon light; on a narrow stage (a sheet open beside it) lower it so the whole
  // plate stays inside the view, and start the beacon beam there.
  for (const g of svg.querySelectorAll('.dock-plate[data-y]')) {
    const y = Math.max(+g.dataset.y, 72 * s.u);
    g.style.transform = `translate(${g.dataset.x}px, ${y.toFixed(1)}px) scale(var(--u))`;
    g.closest('.dock')?.querySelector('.beacon-beam')?.setAttribute('y1', y.toFixed(1));
  }
}

// A name hangs under its body, except on the far (upper) half of the orbit, where it would hang towards the central
// body: there it sits above. A little hysteresis keeps it from flipping back and forth at the turn.
// Near the left or right edge a name slides sideways so its whole plate stays inside the view.
function place(s) {
  for (const o of s.orbs) {
    const sin = Math.sin(o.a);
    const x = o.cx + Math.cos(o.a) * o.rx, y = o.cy + sin * o.rx * o.k;
    const at = `translate(${x.toFixed(2)},${y.toFixed(2)})`;
    o.x = x; o.y = y;
    o.g.setAttribute('transform', at);
    o.label?.setAttribute('transform', at);
    if (!o.fit) continue;
    const up = o.up ? sin < .15 : sin < -.15;
    const half = (+o.fit.querySelector('.plate')?.getAttribute('width') || 0) / 2 * s.u + 4 * s.u;
    // (Not while a ride's camera is zoomed: the view's edges are elsewhere then.)
    const dx = s.riding ? (o.dx ?? 0) : Math.round(clamp(0, half - x, s.vw - half - x));
    if (up !== o.up || o.u !== s.u || dx !== o.dx) {
      o.up = up; o.u = s.u; o.dx = dx;
      const dy = up ? -(o.below + 24 * s.u) : o.below;
      o.fit.style.transform = `translate(${dx}px, ${dy.toFixed(1)}px) scale(var(--u))`;
    }
  }
  for (const l of s.links ?? []) {
    const o = s.byKey.get(l.dataset.orb);
    if (o?.x === undefined) continue;
    l.setAttribute('x2', o.x.toFixed(2));
    l.setAttribute('y2', o.y.toFixed(2));
  }
}

// Every name plate (.fit) is drawn in screen pixels. Each name is measured in its real font and cut with an ellipsis
// until it fits inside the plate's padding; the plate is then sized to the longest line, never past its maximum width
// (data-maxpx pixels, and data-maxu chart units so plates on the whole sky never overlap their neighbours' room).
// The full name stays in data-full (and in the tooltip and the sheet). Only the text node's value changes, so Blazor
// keeps owning the node.
function fitPlates(svg, s, force = false) {
  for (const g of svg.querySelectorAll('.fit')) {
    const texts = [...g.querySelectorAll('text[data-full]')];
    const maxw = Math.min(+g.dataset.maxpx || 220, (+g.dataset.maxu || 1e9) / s.u);
    const sig = texts.map(t => t.dataset.full).join('\n') + '|' + maxw.toFixed(1);
    if (!force && g._sig === sig) continue;
    const pad = +g.dataset.pad || 10;
    const avail = maxw - 2 * pad;
    let widest = 0;
    for (const t of texts) widest = Math.max(widest, fitText(t, avail));
    const w = clamp(widest + 2 * pad, +g.dataset.minpx || 0, maxw);
    const plate = g.querySelector('.plate');
    if (plate) { plate.setAttribute('x', (-w / 2).toFixed(1)); plate.setAttribute('width', w.toFixed(1)); }
    g._sig = sig;
  }
}

function fitText(t, avail) {
  const full = t.dataset.full ?? '';
  let node = t.firstChild;
  if (!node) { node = document.createTextNode(''); t.appendChild(node); }
  node.nodeValue = full;
  let w = t.getComputedTextLength();
  if (w <= avail) return w;
  // The longest start of the name that fits with the ellipsis.
  let lo = 1, hi = full.length - 1;
  while (lo < hi) {
    const mid = (lo + hi + 1) >> 1;
    node.nodeValue = full.slice(0, mid).trimEnd() + '…';
    if (t.getComputedTextLength() <= avail) lo = mid; else hi = mid - 1;
  }
  node.nodeValue = full.slice(0, lo).trimEnd() + '…';
  return t.getComputedTextLength();
}

// Hide any name plate that would overlap one more important than it (the docking plate always shows, star plates
// beat planet names, bigger and busier bodies beat smaller ones). A name already showing gets a small bonus, so
// names do not flicker when two are about level. Names never sit on the central star or planet either.
function cull(svg) {
  const hit = (a, b) => a.left < b.right && b.left < a.right && a.top < b.bottom && b.top < a.bottom;
  const kept = [...svg.querySelectorAll('g.body > circle:last-of-type')].map(c => c.getBoundingClientRect());
  const items = [...svg.querySelectorAll('.fit[data-prio]')].map(g => ({
    g, prio: (+g.dataset.prio || 0) + (g.classList.contains('off') ? 0 : 25),
  }));
  items.sort((a, b) => b.prio - a.prio);
  for (const it of items) {
    const plate = it.g.querySelector('.plate');
    if (!plate) continue;
    const r = plate.getBoundingClientRect();
    if (!r.width) continue;
    const b = { left: r.left - 3, right: r.right + 3, top: r.top - 2, bottom: r.bottom + 2 };
    const clash = it.prio < 5000 && kept.some(k => hit(k, b));
    it.g.classList.toggle('off', clash);
    if (!clash) kept.push(b);
  }
}

// Hover and focus tooltip in MudBlazor's tooltip look. MudTooltip cannot wrap SVG children, so one
// absolutely positioned element lives in the stage and follows the pointer over [data-tip] shapes.
function installTip(svg, s) {
  const stage = svg.parentElement;
  const tip = document.createElement('div');
  tip.className = 'mm-tip mud-tooltip mud-tooltip-default';
  tip.setAttribute('role', 'tooltip');
  const name = document.createElement('div');
  name.className = 'mm-tip-name';
  const sub = document.createElement('div');
  sub.className = 'mm-tip-sub';
  tip.append(name, sub);
  stage.appendChild(tip);

  const hide = () => tip.classList.remove('on');
  s.hideTip = hide;
  const place = (el, x, y) => {
    if (s.tipOff) return;
    if (name.textContent !== el.dataset.tip) name.textContent = el.dataset.tip;
    if (sub.textContent !== el.dataset.tipSub) sub.textContent = el.dataset.tipSub || '';
    const box = stage.getBoundingClientRect();
    const w = tip.offsetWidth, h = tip.offsetHeight;
    let px = x - box.left + 14, py = y - box.top + 18;
    if (px + w > box.width - 6) px = Math.max(6, x - box.left - w - 14);
    if (py + h > box.height - 6) py = Math.max(6, y - box.top - h - 12);
    tip.style.transform = `translate(${px.toFixed(1)}px,${py.toFixed(1)}px)`;
    tip.classList.add('on');
  };
  s.placeTip = place;
  const at = e => e.target.closest?.('[data-tip]');
  stage.addEventListener('pointermove', e => { const el = at(e); if (el) place(el, e.clientX, e.clientY); else hide(); });
  stage.addEventListener('pointerleave', hide);
  stage.addEventListener('pointerdown', hide);
  stage.addEventListener('focusin', e => {
    const el = at(e);
    if (!el || !el.matches(':focus-visible')) return;
    const r = el.getBoundingClientRect();
    place(el, r.left + r.width / 2, r.bottom);
  });
  stage.addEventListener('focusout', hide);
}

// Test hook (?tip=<name>): show the tooltip over the first shape whose name contains the text.
export function peek(svg, text) {
  const s = state.get(svg);
  const el = [...svg.querySelectorAll('[data-tip]')].find(e => e.dataset.tip.toLowerCase().includes(text.toLowerCase()));
  if (!s || !el) return;
  const r = el.getBoundingClientRect();
  s.placeTip(el, r.left + r.width / 2, r.top + r.height / 2);
}

// ---- Rides between levels -------------------------------------------------------------------------------
// A ride is one continuous camera move with a ship on it. Going in, the camera glides and zooms towards what was
// clicked while a ship runs a lane of blinking buoys up to a docking beacon whose plate names the destination; at the
// brightest moment of the jump the next level is drawn, small, exactly where the clicked body was, and it grows into
// the full view. Going out is the same move backwards: the view shrinks away into the jump, the outer level appears
// zoomed in on where you were and glides back out while the ship runs home to its beacon. The camera is a transform on
// the level's g.cam (the starfield behind it drifts a little, for depth); ship, lane, beacon and plate are drawn on a
// canvas over the stage. About 3.7 s each way. Reduced motion: a short dim cross-fade that shows the plate.
//
// The page gives the ride its target: the clicked body going in, or where the reader came from going out (a point in
// chart units, or an orbiting body's key), with the zoom on it at which it lines up with the inner view drawn at
// `start` of its full size. ride() resolves at the jump, when the page draws the next level; surface() lets the ride
// finish and resolves when it has.
const IN = { glide: 2300, jump: 380, settle: 1050 };
const OUT = { shrink: 950, jump: 330, glide: 2150, beat: 380, fade: 300 };
const JUMP_ZOOM = 1.12;    // the camera keeps pushing in a little through the jump
const FAILSAFE = 3000;     // finish even if the page never surfaces the ride
const rides = new WeakMap();
const FONT = '"Fira Sans", ui-sans-serif, system-ui, sans-serif';

const ease = k => (k < .5 ? 4 * k * k * k : 1 - Math.pow(-2 * k + 2, 3) / 2);
const easeOut = k => 1 - Math.pow(1 - k, 3);
const easeIn = k => k * k * k;
const lerp = (a, b, k) => a + (b - a) * k;
const unit = k => clamp(k, 0, 1);

function hash(i, k) {
  const x = Math.sin(i * 127.1 + k * 311.7) * 43758.5453;
  return x - Math.floor(x);
}

// Skip animations: the next level is already drawn; bring it in with a short fade.
export function fade(svg) {
  if (reducedMotion() || !svg.animate) return;
  svg.animate([{ opacity: .25 }, { opacity: 1 }], { duration: 150, easing: 'ease-out' });
}

export function ride(svg, opts, freezeMs = -1) {
  if (rides.has(svg)) return Promise.resolve(0);
  const st = state.get(svg);
  if (st) { st.tipOff = true; st.hideTip?.(); st.paused = true; st.riding = true; }
  const stage = svg.parentElement;
  const canvas = document.createElement('canvas');
  canvas.className = 'dock-run';
  // Positioned here rather than only in the stylesheet, so a browser holding an older cached stylesheet still shows it.
  Object.assign(canvas.style, { position: 'absolute', left: '0', top: '0', width: '100%', height: '100%', pointerEvents: 'none', zIndex: '2' });
  stage.appendChild(canvas);
  const r = {
    svg, stage, canvas, ctx: canvas.getContext('2d'), opts, dir: opts.dir >= 0 ? 1 : -1, reduced: reducedMotion(),
    t: 0, held: false, heldFor: 0, surfaced: false, freezeMs, W: 0, H: 0, began: performance.now(),
    target: opts.target ? { ...opts.target } : null, land: opts.land, focus: document.activeElement,
    hadFocus: svg.contains(document.activeElement) && document.activeElement.matches(':focus-visible'),
    streaks: Array.from({ length: 70 }, (_, i) => ({ a: hash(i, 1) * Math.PI * 2, d: hash(i, 2), b: .3 + hash(i, 3) * .7 })),
  };
  if (r.dir > 0 && r.target?.key) Object.assign(r.target, livePos(svg, r.target.key));
  if (r.reduced) { r.swapAt = 200; r.end = r.swapAt + 320 + 240; }
  else if (r.dir > 0) { r.swapAt = IN.glide + IN.jump; r.end = r.swapAt + IN.settle; }
  else { r.swapAt = OUT.shrink; r.end = r.swapAt + OUT.glide + OUT.beat + OUT.fade; }
  rides.set(svg, r);
  const swap = new Promise(res => { r.resolve = res; });
  r.finished = new Promise(res => { r.done = res; });
  if (freezeMs >= 0) { runFrozen(r); return swap; }
  let last = 0;
  const frame = now => {
    const dt = last ? Math.min(48, now - last) : 16;
    last = now;
    if (tick(r, dt)) requestAnimationFrame(frame);
  };
  requestAnimationFrame(frame);
  return swap;
}

export function surface(svg, level, freezeMs = -1) {
  const r = rides.get(svg);
  if (!r) return Promise.resolve();
  // Going out, an orbiting origin is wherever its orbit has it on the level just drawn.
  if (r.dir < 0 && r.target?.key) Object.assign(r.target, livePos(svg, r.target.key));
  r.level = level;
  r.surfaced = true;
  r.held = false;
  if (r.freezeMs >= 0) runFrozen(r);
  return r.finished;
}

// Screenshot hook: step a synthetic clock so a frozen frame is the same on every machine (once the plate's font has
// loaded, so the frame shows the real type).
function runFrozen(r) {
  const fonts = document.fonts?.load ? Promise.all([document.fonts.load(`600 26px ${FONT}`), document.fonts.load(`500 14px ${FONT}`)]) : Promise.resolve();
  fonts.catch(() => {}).then(() => {
    for (let i = 0; i < 2000 && tick(r, 16) && !r.held; i++);
  });
}

// The live centre of an orbiting body, read from the transform the orbit loop gives it.
function livePos(svg, key) {
  const g = [...svg.querySelectorAll('g.orb')].find(e => e.dataset.key === key);
  const m = g?.getAttribute('transform')?.match(/translate\(\s*([-\d.e]+)[\s,]+([-\d.e]+)\s*\)/);
  return m ? { x: +m[1], y: +m[2] } : {};
}

// One step of the ride clock; false once it has finished or (screenshot hook) frozen. The clock stops at the jump
// until the page has drawn the next level.
function tick(r, dt) {
  if (!r.held) r.t += dt;
  if (r.t >= r.swapAt && !r.surfaced) {
    if (!r.held) { r.t = r.swapAt; r.held = true; r.resolve(1); }
    r.heldFor += dt;
    if (r.heldFor > FAILSAFE) { r.surfaced = true; r.held = false; }
  }
  if (r.t >= r.end) { finish(r); return false; }
  draw(r);
  return !(r.freezeMs >= 0 && r.t >= r.freezeMs);
}

function finish(r) {
  applyCam(r, null);
  const st = state.get(r.svg);
  if (st) { st.tipOff = false; st.paused = reducedMotion(); st.riding = false; }
  // Keyboard riders keep their place: going in, focus moves to the first target of the new level; going out, to the
  // body they came from (the target nearest the ride's origin), unless they have moved focus themselves meanwhile.
  if (r.hadFocus && (document.activeElement === document.body || !document.activeElement || !r.svg.contains(document.activeElement))) {
    const all = [...r.svg.querySelectorAll('g.cam [tabindex="0"]')];
    const areas = all.filter(el => el.matches('.area'));
    const targets = areas.length ? areas : all;
    let next = r.focus?.isConnected && r.svg.contains(r.focus) ? r.focus : targets[0];
    if (r.dir < 0 && r.target && isFinite(r.target.x)) {
      const m = mapper(r), T = m.px(r.target), box = r.stage.getBoundingClientRect();
      const dist = el => { const b = el.getBoundingClientRect(); return Math.hypot(b.left + b.width / 2 - box.left - T.x, b.top + b.height / 2 - box.top - T.y); };
      next = targets.reduce((a, b) => (dist(b) < dist(a) ? b : a), targets[0]);
    }
    next?.focus({ preventScroll: true });
  }
  r.canvas.remove();
  rides.delete(r.svg);
  (window.mmRides ??= []).push({ dir: r.dir, label: r.opts.label, ms: Math.round(performance.now() - r.began) });
  r.done();
}

// Where the camera is at the ride's current time: the anchor point a (chart units) is shown at p, scaled by s.
function camera(r) {
  const t = r.t, land = r.land, T = r.target;
  if (r.reduced || !T || !isFinite(T.x)) return null;
  const start = T.start || .6;
  if (r.dir > 0) {
    if (t < r.swapAt) {
      const e = ease(unit(t / IN.glide));
      const push = 1 + (JUMP_ZOOM - 1) * easeIn(unit((t - IN.glide) / IN.jump));
      return { a: T, p: { x: lerp(T.x, land.x, e), y: lerp(T.y, land.y, e) }, s: Math.pow(T.zoom, e) * push };
    }
    // The new level starts exactly where and as big as the clicked body was, and grows to fill the view.
    const e = easeOut(unit((t - r.swapAt) / IN.settle));
    return { a: land, p: land, s: Math.pow(start * JUMP_ZOOM, 1 - e) };
  }
  if (t < r.swapAt) {
    const e = ease(unit(t / OUT.shrink));
    return { a: land, p: land, s: Math.pow(start, e) };
  }
  // The outer level starts zoomed in on where you were, lined up with the view that just shrank away.
  const e = ease(unit((t - r.swapAt) / OUT.glide));
  return { a: T, p: { x: lerp(land.x, T.x, e), y: lerp(land.y, T.y, e) }, s: Math.pow(T.zoom, 1 - e) };
}

function applyCam(r, cam) {
  const g = r.svg.querySelector('g.cam');
  const sky = r.svg.querySelector('g.sky');
  if (!cam) {
    g?.removeAttribute('transform');
    g?.style.removeProperty('--u');
    sky?.removeAttribute('transform');
    return;
  }
  const f = v => v.toFixed(3);
  g?.setAttribute('transform', `translate(${f(cam.p.x)},${f(cam.p.y)}) scale(${cam.s.toFixed(5)}) translate(${f(-cam.a.x)},${f(-cam.a.y)})`);
  // Name plates keep their screen size while the camera zooms.
  g?.style.setProperty('--u', ((state.get(r.svg)?.u ?? 1) / cam.s).toFixed(5));
  // The starfield is far away: it drifts a little the same way.
  const k = Math.pow(cam.s, .07);
  sky?.setAttribute('transform', `translate(${f(cam.p.x)},${f(cam.p.y)}) scale(${k.toFixed(5)}) translate(${f(-cam.p.x)},${f(-cam.p.y)})`);
}

// Chart units to canvas pixels.
function mapper(r) {
  const sr = r.stage.getBoundingClientRect(), vr = r.svg.getBoundingClientRect();
  const vb = r.svg.viewBox.baseVal;
  const k = vr.width / (vb?.width || 1);
  const ox = vr.left - sr.left - (vb?.x ?? 0) * k, oy = vr.top - sr.top - (vb?.y ?? 0) * k;
  return { k, sr, px: q => ({ x: ox + q.x * k, y: oy + q.y * k }) };
}

function draw(r) {
  const m = mapper(r);
  const W = Math.max(1, Math.round(r.stage.clientWidth)), H = Math.max(1, Math.round(r.stage.clientHeight));
  if (W !== r.W || H !== r.H) {
    // The stage can change size mid-ride (a sheet opening, a rotated phone): keep the canvas sharp and in step.
    r.dpr = Math.min(2, window.devicePixelRatio || 1);
    r.W = W; r.H = H;
    r.canvas.width = Math.round(W * r.dpr);
    r.canvas.height = Math.round(H * r.dpr);
  }
  const { ctx } = r;
  ctx.setTransform(r.dpr, 0, 0, r.dpr, 0, 0);
  ctx.clearRect(0, 0, W, H);
  // Compose inside the part of the stage the reader can see.
  r.visTop = clamp(-m.sr.top, 0, H - 1);
  r.visBot = clamp(window.innerHeight - m.sr.top, r.visTop + 1, H);
  r.U = clamp(Math.min(W, r.visBot - r.visTop) / 900, .62, 1.1);
  const cam = camera(r);
  applyCam(r, cam);
  if (r.reduced) return drawReduced(r);
  if (r.dir > 0) drawIn(r, m, cam); else drawOut(r, m, cam);
}

// Where the ship sets off from (going in) or comes home to (going out): low in a corner of the visible stage, the one
// farther from where the ride heads, so the lane is long.
function home(r) {
  if (!r.home) {
    const T = r.target;
    const tx = T && isFinite(T.x) ? mapper(r).px(T).x : r.W / 2;
    r.home = { side: tx < r.W / 2 ? 1 : -1 };
  }
  return { x: r.W / 2 + r.home.side * (r.W / 2 - Math.max(70, r.W * .1)), y: r.visBot - 58 * r.U };
}

// Distance from the centre of an ellipse (rx, rx*k) to its edge along a unit direction.
function edge(rx, k, ux, uy) {
  if (rx <= 0) return 0;
  const ry = rx * (k || 1);
  return 1 / Math.sqrt((ux / rx) ** 2 + (uy / ry) ** 2);
}

function drawIn(r, m, cam) {
  const t = r.t;
  if (t < r.swapAt && cam) {
    const T = r.target;
    const Tp = m.px(cam.p);
    const P0 = home(r);
    let dx = Tp.x - P0.x, dy = Tp.y - P0.y;
    const len = Math.hypot(dx, dy) || 1;
    const ux = dx / len, uy = dy / len;
    const rim = edge((T.r || 12) * cam.s * m.k, T.k, ux, uy);
    const B = { x: Tp.x - ux * (rim + 26 * r.U), y: Tp.y - uy * (rim + 26 * r.U) };
    if ((B.x - P0.x) * ux + (B.y - P0.y) * uy < 60 * r.U) { B.x = P0.x + ux * 60 * r.U; B.y = P0.y + uy * 60 * r.U; }
    const glide = unit(t / IN.glide), jump = unit((t - IN.glide) / IN.jump);
    const speed = Math.sin(Math.PI * glide);
    const alpha = unit(t / 300) * (1 - jump);
    drawStreaks(r, Tp, speed * alpha, t);
    drawLane(r, P0, B, alpha, t);
    drawBeacon(r, B, t, unit((t - 650) / 300) * (1 - jump));
    // The ship pulls away, cruises up the lane and eases into the beacon, then dives into the jump.
    const f = ease(unit((t - 120) / (IN.glide - 250)));
    const stop = { x: B.x - ux * 40 * r.U, y: B.y - uy * 40 * r.U };
    let sx = lerp(P0.x, stop.x, f), sy = lerp(P0.y, stop.y, f), scale = 1;
    if (jump > 0) { const j = easeIn(jump); sx = lerp(stop.x, Tp.x, j); sy = lerp(stop.y, Tp.y, j); scale = 1 - j; }
    drawShip(r, sx, sy, Math.atan2(uy, ux), scale, speed, t);
    r.plateAt = sidePlate(r, B);
    drawPlate(r, r.plateAt.x, r.plateAt.y, unit((t - 950) / 300), B);
    veil(r, Tp, easeIn(jump));
    r.jumpAt = Tp;
  } else {
    const k = (t - r.swapAt) / IN.settle;
    const L = m.px(r.land);
    veil(r, L, 1 - easeOut(unit(k / .45)));
    if (r.plateAt) drawPlate(r, r.plateAt.x, r.plateAt.y, 1 - unit((k - .1) / .35));
  }
}

function drawOut(r, m, cam) {
  const t = r.t;
  const P0 = home(r);
  if (t < r.swapAt) {
    const L = m.px(r.land);
    const a = unit(t / 300);
    drawBeacon(r, P0, t, a);
    drawPlate(r, P0.x, P0.y - 74 * r.U, unit((t - 150) / 300), P0);
    // The ship lifts off from where you were, nose towards home.
    const ux = P0.x - L.x, uy = P0.y - L.y, len = Math.hypot(ux, uy) || 1;
    const lift = easeOut(unit(t / 500));
    drawShip(r, L.x + ux / len * 30 * r.U * lift, L.y + uy / len * 30 * r.U * lift, Math.atan2(uy, ux), lift, .3, t);
    veil(r, L, easeIn(unit((t - (OUT.shrink - OUT.jump)) / OUT.jump)));
    return;
  }
  const k = t - r.swapAt;
  const fadeOut = 1 - unit((k - OUT.glide - OUT.beat) / OUT.fade);
  const Op = cam ? m.px(cam.p) : m.px(r.land);
  const dx = P0.x - Op.x, dy = P0.y - Op.y, len = Math.hypot(dx, dy) || 1;
  const ux = dx / len, uy = dy / len;
  const B = { x: P0.x, y: P0.y };
  const glide = unit(k / OUT.glide);
  const speed = Math.sin(Math.PI * glide);
  const laneA = Math.min(unit((k - 80) / 300), fadeOut);
  drawStreaks(r, Op, speed * laneA * .7, t);
  drawLane(r, Op, { x: B.x - ux * 26 * r.U, y: B.y - uy * 26 * r.U }, laneA, t);
  drawBeacon(r, B, t, fadeOut);
  drawPlate(r, P0.x, P0.y - 74 * r.U, fadeOut, P0);
  // The ship runs home along the lane and settles at the beacon.
  const f = ease(unit((k - 60) / (OUT.glide - 60)));
  const start = { x: Op.x + ux * 24 * r.U, y: Op.y + uy * 24 * r.U };
  const stop = { x: B.x - ux * 44 * r.U, y: B.y - uy * 44 * r.U };
  drawShip(r, lerp(start.x, stop.x, f), lerp(start.y, stop.y, f), Math.atan2(uy, ux), fadeOut, speed, t);
  veil(r, m.px(r.land), 1 - easeOut(unit(k / 380)));
}

function drawReduced(r) {
  const { ctx, W } = r;
  const t = r.t;
  const a = t < r.swapAt ? unit(t / r.swapAt) : 1 - unit((t - r.swapAt - 320) / 240);
  ctx.save();
  ctx.globalAlpha = a;
  ctx.fillStyle = 'rgba(2,6,23,.94)';
  ctx.fillRect(0, 0, W, r.H);
  ctx.restore();
  drawPlate(r, W / 2, (r.visTop + r.visBot) / 2, a);
}

// The jump: a dark veil with a bright core where the ship went in. At its peak it hides the swap of levels.
function veil(r, c, a) {
  if (a <= 0) return;
  const { ctx, W, H } = r;
  ctx.save();
  ctx.globalAlpha = a;
  ctx.fillStyle = 'rgba(2,6,23,.93)';
  ctx.fillRect(0, 0, W, H);
  const g = ctx.createRadialGradient(c.x, c.y, 0, c.x, c.y, Math.hypot(W, H) * (.18 + .3 * a));
  g.addColorStop(0, 'rgba(254,243,199,.9)');
  g.addColorStop(.2, 'rgba(250,204,21,.38)');
  g.addColorStop(1, 'rgba(167,139,250,0)');
  ctx.fillStyle = g;
  ctx.fillRect(0, 0, W, H);
  ctx.restore();
}

// Stars streaking out from where the camera is heading, longer the faster it goes.
function drawStreaks(r, c, a, t) {
  if (a <= .02) return;
  const { ctx, W, H, U } = r;
  const reach = Math.hypot(W, H) * .6;
  ctx.save();
  ctx.lineCap = 'round';
  for (const s of r.streaks) {
    const d = ((s.d + t * .00035) % 1) * reach + 30 * U;
    const len = (14 + 90 * a) * U * (.4 + d / reach);
    const cos = Math.cos(s.a), sin = Math.sin(s.a);
    ctx.strokeStyle = `rgba(226,232,240,${(.45 * a * s.b).toFixed(3)})`;
    ctx.lineWidth = 1 + s.b;
    ctx.beginPath();
    ctx.moveTo(c.x + cos * d, c.y + sin * d);
    ctx.lineTo(c.x + cos * (d + len), c.y + sin * (d + len));
    ctx.stroke();
  }
  ctx.restore();
}

// The nav lane from A to B: faint edges, a dashed centre line running forward, and pairs of buoys whose lights chase
// towards B.
function drawLane(r, A, B, alpha, t) {
  const dx = B.x - A.x, dy = B.y - A.y, len = Math.hypot(dx, dy);
  if (alpha <= 0 || len < 24) return;
  const { ctx, U } = r;
  const ux = dx / len, uy = dy / len, nx = -uy, ny = ux, half = 30 * U;
  ctx.save();
  ctx.globalAlpha = alpha;
  ctx.strokeStyle = 'rgba(203,213,225,.28)';
  ctx.lineWidth = 1.5;
  for (const sgn of [-1, 1]) {
    ctx.beginPath();
    ctx.moveTo(A.x + nx * half * sgn, A.y + ny * half * sgn);
    ctx.lineTo(B.x + nx * half * sgn, B.y + ny * half * sgn);
    ctx.stroke();
  }
  ctx.setLineDash([14 * U, 20 * U]);
  ctx.lineDashOffset = -t * .09;
  ctx.strokeStyle = 'rgba(167,139,250,.6)';
  ctx.lineWidth = 2 * U;
  ctx.beginPath(); ctx.moveTo(A.x, A.y); ctx.lineTo(B.x, B.y); ctx.stroke();
  ctx.setLineDash([]);
  const pitch = 62 * U;
  for (let i = 1; i * pitch < len; i++) {
    const x = A.x + ux * i * pitch, y = A.y + uy * i * pitch;
    const on = .3 + .7 * Math.max(0, Math.sin(t / 140 - i * .8));
    buoy(ctx, x - nx * half, y - ny * half, on, '250,204,21', U);
    buoy(ctx, x + nx * half, y + ny * half, on, '167,139,250', U);
  }
  ctx.restore();
}

function buoy(ctx, x, y, on, c, U) {
  ctx.fillStyle = '#334155';
  ctx.beginPath(); ctx.moveTo(x, y - 7 * U); ctx.lineTo(x + 5 * U, y); ctx.lineTo(x, y + 7 * U); ctx.lineTo(x - 5 * U, y); ctx.closePath(); ctx.fill();
  const g = ctx.createRadialGradient(x, y, 0, x, y, 13 * U);
  g.addColorStop(0, `rgba(${c},${(.8 * on).toFixed(3)})`); g.addColorStop(1, `rgba(${c},0)`);
  ctx.fillStyle = g; ctx.fillRect(x - 13 * U, y - 13 * U, 26 * U, 26 * U);
  ctx.fillStyle = `rgba(${c},${(.5 + .5 * on).toFixed(3)})`;
  ctx.beginPath(); ctx.arc(x, y, 2.5 * U, 0, Math.PI * 2); ctx.fill();
}

function cutText(ctx, text, maxW) {
  if (ctx.measureText(text).width <= maxW) return text;
  let t = text;
  while (t.length > 1 && ctx.measureText(t.trimEnd() + '…').width > maxW) t = t.slice(0, -1);
  return t.trimEnd() + '…';
}

function plateWidth(r) {
  const { ctx, W } = r;
  ctx.save();
  ctx.font = `600 26px ${FONT}`;
  const w = clamp(ctx.measureText(r.opts.label || '').width + 56, 220, Math.min(W - 24, 520));
  ctx.restore();
  return w;
}

// The plate hangs beside the beacon, on the side with more room, inside the visible stage.
function sidePlate(r, B) {
  const pw = plateWidth(r);
  const side = B.x <= r.W / 2 ? 1 : -1;
  return {
    x: clamp(B.x + side * (pw / 2 + 46 * r.U), pw / 2 + 12, r.W - pw / 2 - 12),
    y: clamp(B.y - 10 * r.U, r.visTop + 48, r.visBot - 48),
  };
}

// The docking plate: gold kicker ("Docking at" / "Returning to") over the destination's name, with a tether to the
// beacon. The name shrinks to 18 px at most, then is cut, so it never leaves the plate.
function drawPlate(r, cx, cy, alpha, tether) {
  if (alpha <= 0) return;
  const { ctx, W } = r;
  const kicker = r.opts.kicker || '', label = r.opts.label || '';
  const pw = plateWidth(r), ph = 72;
  const x = clamp(cx - pw / 2, 12, W - 12 - pw), y = cy - ph / 2;
  ctx.save();
  ctx.globalAlpha *= alpha;
  if (tether) {
    ctx.strokeStyle = 'rgba(250,204,21,.85)'; ctx.lineWidth = 2;
    ctx.setLineDash([3, 5]);
    ctx.beginPath(); ctx.moveTo(tether.x, tether.y); ctx.lineTo(clamp(tether.x, x + 10, x + pw - 10), clamp(tether.y, y, y + ph)); ctx.stroke();
    ctx.setLineDash([]);
  }
  ctx.shadowColor = 'rgba(0,0,0,.6)'; ctx.shadowBlur = 16; ctx.shadowOffsetY = 4;
  ctx.fillStyle = 'rgba(2,6,23,.95)';
  ctx.beginPath(); ctx.roundRect(x, y, pw, ph, 12); ctx.fill();
  ctx.shadowColor = 'transparent';
  ctx.strokeStyle = '#FACC15'; ctx.lineWidth = 2; ctx.stroke();
  ctx.textAlign = 'center'; ctx.textBaseline = 'alphabetic';
  const mid = x + pw / 2, maxW = pw - 32;
  ctx.fillStyle = '#FACC15';
  ctx.font = `500 14px ${FONT}`;
  ctx.fillText(cutText(ctx, kicker, maxW), mid, y + 25);
  let size = 26;
  ctx.font = `600 ${size}px ${FONT}`;
  while (ctx.measureText(label).width > maxW && size > 18) { size -= 1; ctx.font = `600 ${size}px ${FONT}`; }
  ctx.fillStyle = '#F8FAFC';
  ctx.fillText(cutText(ctx, label, maxW), mid, y + 56);
  ctx.restore();
}

// The docking beacon: a ring with a turning dashed halo and a pulsing gold light.
function drawBeacon(r, p, t, alpha) {
  if (alpha <= 0) return;
  const { ctx, U } = r;
  const { x, y } = p;
  const pulse = .5 + .5 * Math.sin(t / 200);
  ctx.save();
  ctx.globalAlpha = alpha;
  ctx.strokeStyle = '#94A3B8'; ctx.lineWidth = 3 * U;
  ctx.beginPath(); ctx.arc(x, y, 22 * U, 0, Math.PI * 2); ctx.stroke();
  ctx.strokeStyle = 'rgba(250,204,21,.8)'; ctx.lineWidth = 1.5 * U;
  ctx.setLineDash([4 * U, 5 * U]);
  ctx.beginPath(); ctx.ellipse(x, y, 31 * U, 31 * U, t / 1500, 0, Math.PI * 2); ctx.stroke();
  ctx.setLineDash([]);
  ctx.fillStyle = '#1E293B';
  ctx.beginPath(); ctx.arc(x, y, 12 * U, 0, Math.PI * 2); ctx.fill();
  const g = ctx.createRadialGradient(x, y, 0, x, y, 24 * U);
  g.addColorStop(0, `rgba(250,204,21,${(.35 + .45 * pulse).toFixed(3)})`); g.addColorStop(1, 'rgba(250,204,21,0)');
  ctx.fillStyle = g; ctx.fillRect(x - 24 * U, y - 24 * U, 48 * U, 48 * U);
  ctx.fillStyle = '#FACC15';
  ctx.beginPath(); ctx.arc(x, y, 4.5 * U, 0, Math.PI * 2); ctx.fill();
  ctx.restore();
}

// The ship, nose along `angle`, with an engine flame that grows with speed and blinking wing-tip lights.
function drawShip(r, x, y, angle, scale, speed, t) {
  if (scale <= .02) return;
  const { ctx } = r;
  const U = r.U * scale;
  ctx.save();
  ctx.translate(x, y + Math.sin(t / 320) * 2 * U);
  ctx.rotate(angle);
  ctx.scale(U, U);
  const flame = 14 + 80 * speed;
  const fl = ctx.createLinearGradient(-40 - flame, 0, -40, 0);
  fl.addColorStop(0, 'rgba(167,139,250,0)'); fl.addColorStop(.6, 'rgba(167,139,250,.55)'); fl.addColorStop(1, 'rgba(254,243,199,.95)');
  ctx.fillStyle = fl;
  ctx.beginPath(); ctx.moveTo(-40, -7); ctx.lineTo(-40 - flame, 0); ctx.lineTo(-40, 7); ctx.closePath(); ctx.fill();
  ctx.fillStyle = '#64748B';
  ctx.beginPath(); ctx.moveTo(-10, -6); ctx.lineTo(-34, -26); ctx.lineTo(-26, -6); ctx.closePath(); ctx.fill();
  ctx.beginPath(); ctx.moveTo(-10, 6); ctx.lineTo(-34, 26); ctx.lineTo(-26, 6); ctx.closePath(); ctx.fill();
  const hull = ctx.createLinearGradient(0, -16, 0, 16);
  hull.addColorStop(0, '#E2E8F0'); hull.addColorStop(.6, '#94A3B8'); hull.addColorStop(1, '#475569');
  ctx.fillStyle = hull;
  ctx.beginPath();
  ctx.moveTo(52, 0);
  ctx.quadraticCurveTo(20, -15, -40, -11);
  ctx.lineTo(-40, 11);
  ctx.quadraticCurveTo(20, 15, 52, 0);
  ctx.closePath(); ctx.fill();
  ctx.fillStyle = '#FACC15';
  ctx.fillRect(-36, 3, 54, 2.4);
  ctx.fillStyle = '#A78BFA';
  ctx.beginPath(); ctx.ellipse(18, -4, 11, 4.5, 0, 0, Math.PI * 2); ctx.fill();
  ctx.fillStyle = 'rgba(255,255,255,.55)';
  ctx.beginPath(); ctx.ellipse(21, -5.5, 4, 1.6, 0, 0, Math.PI * 2); ctx.fill();
  const blink = Math.sin(t / 180) > 0 ? 1 : .25;
  ctx.fillStyle = `rgba(248,113,113,${blink})`; ctx.beginPath(); ctx.arc(-33, -25, 2.2, 0, Math.PI * 2); ctx.fill();
  ctx.fillStyle = `rgba(74,222,128,${blink})`; ctx.beginPath(); ctx.arc(-33, 25, 2.2, 0, Math.PI * 2); ctx.fill();
  ctx.restore();
}
