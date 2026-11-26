// Colony map helpers. Blazor draws the colony; this module walks the ants, places the chamber labels,
// shows the hover tooltip and plays the ant ride between the surface and a chamber.
const NS = 'http://www.w3.org/2000/svg';

const running = new WeakMap();

export function start(svg, total, merged, fullWidth = 1200) {
  installTips(svg);
  const layer = svg.querySelector('#ants');
  layer.replaceChildren();
  const tunnels = [...svg.querySelectorAll('path.route')].map(p => ({ p, len: p.getTotalLength(), w: Math.sqrt(+p.dataset.heat || 0) }));
  const sum = tunnels.reduce((t, x) => t + x.w, 0);
  if (!sum) return;
  let seed = 5;
  const rnd = () => ((seed = (seed * 1664525 + 1013904223) >>> 0) / 4294967296);
  const ants = [];
  for (let i = 0; i < total; i++) {
    let x = rnd() * sum, k = 0;
    while (x > tunnels[k].w && k < tunnels.length - 1) { x -= tunnels[k].w; k++; }
    ants.push({ ...makeAnt(layer, i < merged), t: tunnels[k], pos: rnd() * tunnels[k].len, dir: rnd() < .5 ? 1 : -1, speed: .25 + rnd() * .35, ph: rnd() * 6 });
  }
  const base = 1.5;
  const prior = running.get(svg);
  if (prior) {
    // A live update: new ants on the new tunnels, same animation loop and zoom scale.
    prior.ants = ants;
    return;
  }
  const s = { ants, scale: base };
  running.set(svg, s);
  svg.addEventListener('mapzoom', e => { s.scale = Math.max(.25, base * e.detail.width / fullWidth); });
  const still = matchMedia('(prefers-reduced-motion: reduce)').matches;
  let last = performance.now();
  const frame = now => {
    const dt = Math.min(50, now - last); last = now;
    for (const a of s.ants) {
      if (!still) {
        a.pos += a.dir * a.speed * dt * .06;
        if (a.pos > a.t.len || a.pos < 0) { a.dir *= -1; a.pos = Math.max(0, Math.min(a.t.len, a.pos)); }
      }
      place(a, now, s.scale);
    }
    requestAnimationFrame(frame);
  };
  requestAnimationFrame(frame);
}

function makeAnt(layer, pink) {
  const g = document.createElementNS(NS, 'g');
  const add = (tag, attrs) => { const e = document.createElementNS(NS, tag); for (const k in attrs) e.setAttribute(k, attrs[k]); g.appendChild(e); return e; };
  const legs = [];
  for (let i = 0; i < 3; i++) for (const side of [-1, 1]) legs.push(add('line', { x1: -2 + i * 2.4, y1: 0, x2: -3 + i * 2.4, y2: side * 4.4, stroke: 'var(--ant)', 'stroke-width': .9, 'stroke-linecap': 'round' }));
  add('ellipse', { cx: -4, cy: 0, rx: 2.6, ry: 1.9, fill: 'var(--ant)' });
  add('ellipse', { cx: 0, cy: 0, rx: 1.8, ry: 1.3, fill: 'var(--ant)' });
  add('circle', { cx: 3.2, cy: 0, r: 1.6, fill: 'var(--ant)' });
  if (pink) add('circle', { cx: 6, cy: 0, r: 1.9, fill: 'var(--accent)' });
  layer.appendChild(g);
  return { g, legs };
}

function place(a, time, scale) {
  const p = a.t.p.getPointAtLength(a.pos);
  const q = a.t.p.getPointAtLength(Math.max(0, Math.min(a.t.len, a.pos + a.dir)));
  const ang = Math.atan2(q.y - p.y, q.x - p.x) * 180 / Math.PI;
  a.g.setAttribute('transform', `translate(${p.x},${p.y}) rotate(${ang}) scale(${scale})`);
  const w = Math.sin(time / 60 + a.ph) * .9;
  a.legs.forEach((l, i) => l.setAttribute('x2', (-3 + Math.floor(i / 2) * 2.4) + (((i % 2) ^ (Math.floor(i / 2) % 2)) ? w : -w)));
}

// ---------------------------------------------------------------------------------------------
// Labels: place chamber names where they fit, fit room names inside their rooms.
// ---------------------------------------------------------------------------------------------

const observed = new WeakSet();

export function layout(svg, fullWidth = 1600, fullHeight = 1150) {
  if (!observed.has(svg)) {
    observed.add(svg);
    let queued = 0;
    const again = () => { cancelAnimationFrame(queued); queued = requestAnimationFrame(() => layout(svg, fullWidth, fullHeight)); };
    new ResizeObserver(again).observe(svg);
    document.fonts?.ready.then(again);
  }
  placeNestLabels(svg, fullWidth);
  placeChamberLabels(svg, fullWidth, fullHeight);
  fitRoomNames(svg);
}

/**
 * Nest names on the surface: at least 13.5px (counts 12px) on screen whatever the stage width, in two rows.
 * Busy and big nests go first; a name that would collide steps aside and the tooltip still carries it.
 */
function placeNestLabels(svg, fullWidth) {
  const px = svg.getBoundingClientRect().width;
  if (!px) return;
  const scale = px / fullWidth;
  const nests = [...svg.querySelectorAll('g.nest')].map(g => {
    const [name, count] = g.querySelectorAll('text.nlab');
    return { g, name, count, tick: g.querySelector('line.ntick'), hit: g.querySelector('rect.nhit'),
      x: +g.dataset.x, upper: g.dataset.upper === '1', big: g.dataset.big === '1', prs: +g.dataset.prs };
  });
  if (!nests.length) return;
  const surface = +nests[0].g.querySelector('ellipse').getAttribute('cy') + 3;
  const nameFs = big => big ? Math.max(28, 17 / scale) : Math.max(22, 13.5 / scale);
  const countFs = Math.max(19, 12 / scale);
  const countY = surface - 21;
  const up = nameFs(true) * .95 + countFs + 14;           // the upper row sits one label height higher
  // On a narrow stage two rows do not fit in the sky: one row, and the names that do not fit step aside.
  const twoRows = countY - up - countFs - 3 - nameFs(true) > 2;
  const rows = [[], []];                                   // occupied x ranges per row: lower, upper
  const free = (row, a, b) => !rows[row].some(r => a < r[1] && b > r[0]);
  const order = [...nests].sort((p, q) => q.big - p.big || q.prs - p.prs || p.x - q.x);
  for (const n of order) {
    const upper = n.upper && twoRows;
    const lift = upper ? up : 0;
    n.name.setAttribute('font-size', nameFs(n.big));
    n.count.setAttribute('font-size', countFs);
    const w = Math.max(n.name.getComputedTextLength(), n.count.getComputedTextLength());
    // Keep the label inside the map, shifting it off-centre over its mound if it has to.
    const cx = clamp(n.x, w / 2 + 4, fullWidth - w / 2 - 4);
    const a = cx - w / 2 - 6, b = cx + w / 2 + 6;
    const row = upper ? 1 : 0;
    n.shown = free(row, a, b);
    n.upperShown = upper;
    for (const t of [n.name, n.count]) t.classList.toggle('off', !n.shown);
    if (!n.shown) { n.hit.setAttribute('width', 0); continue; }
    rows[row].push([a, b]);
    const y = countY - lift;
    n.count.setAttribute('x', cx); n.count.setAttribute('y', y);
    const nameY = y - countFs - 3 - nameFs(n.big) * .2;    // clear of the count line, descenders included
    n.name.setAttribute('x', cx); n.name.setAttribute('y', nameY);
    n.tick?.setAttribute('y2', y - countFs + 2);
    const top = nameY - nameFs(n.big) * .8;
    n.hit.setAttribute('x', a); n.hit.setAttribute('width', b - a);
    n.hit.setAttribute('y', top - 4); n.hit.setAttribute('height', y + 6 - top);
  }
  // An upper name's leader line down to its mound is drawn only where it does not cross a lower name.
  for (const n of nests) n.tick?.classList.toggle('off', !n.shown || !n.upperShown || !free(0, n.x - 2, n.x + 2));
}

function placeChamberLabels(svg, fullWidth, fullHeight) {
  const px = svg.getBoundingClientRect().width;
  if (!px) return;
  const scale = px / fullWidth;                 // screen pixels per map unit at the whole-colony view
  const fs = 12.5 / scale;                      // 12.5px on screen whatever the stage width
  const chambers = [...svg.querySelectorAll('g.chamber')].map(g => ({
    g, text: g.querySelector('text.clab'),
    x: +g.dataset.x, y: +g.dataset.y, rx: +g.dataset.rx, ry: +g.dataset.ry, prs: +g.dataset.prs,
  }));
  // Busy chambers first, then the biggest.
  const order = [...chambers].sort((a, b) => (b.prs > 0) - (a.prs > 0) || b.rx - a.rx || b.prs - a.prs);
  const placed = [];
  const clash = (box, self) =>
    placed.some(p => box.x1 < p.x2 && box.x2 > p.x1 && box.y1 < p.y2 && box.y2 > p.y1) ||
    chambers.some(c => c !== self && touchesEllipse(box, c));
  for (const c of order) {
    const t = c.text;
    if (!t) continue;
    // Quiet little chambers keep their names in the tooltip so the map stays calm.
    if (!c.prs && c.rx * scale < 15) { t.classList.add('off'); continue; }
    t.setAttribute('font-size', fs);
    const w = t.getComputedTextLength() + 8;
    const h = fs * 1.3;
    const spots = [
      { y1: c.y + c.ry + 3 },                    // below its chamber
      { y1: c.y - c.ry - 3 - h },                // above it
    ];
    let done = false;
    for (const s of spots) {
      const box = { x1: c.x - w / 2, x2: c.x + w / 2, y1: s.y1, y2: s.y1 + h };
      if (box.x1 < 4 || box.x2 > fullWidth - 4 || box.y1 < 0 || box.y2 > fullHeight - 2) continue;
      if (clash(box, c)) continue;
      t.setAttribute('x', c.x);
      t.setAttribute('y', box.y1 + fs * 1.0);
      placed.push(box);
      done = true;
      break;
    }
    t.classList.toggle('off', !done);
  }
}

function touchesEllipse(box, c) {
  const nx = Math.max(box.x1, Math.min(c.x, box.x2));
  const ny = Math.max(box.y1, Math.min(c.y, box.y2));
  const dx = (nx - c.x) / (c.rx + 2);
  const dy = (ny - c.y) / (c.ry + 2);
  return dx * dx + dy * dy < 1;
}

function fitRoomNames(svg) {
  const px = svg.getBoundingClientRect().width;
  if (!px) return;
  for (const t of svg.querySelectorAll('text.rname')) {
    // Laid out in screen pixels and scaled about its anchor, so small map units do not distort the letter spacing.
    const ppu = px / (+t.dataset.vw || 1600);    // screen pixels per map unit once the chamber view lands
    const max = +t.dataset.w * ppu;
    const x = +t.getAttribute('x'), y = +t.getAttribute('y');
    t.setAttribute('transform', `translate(${x} ${y}) scale(${(1 / ppu).toFixed(5)}) translate(${-x} ${-y})`);
    const full = t.dataset.full || t.textContent;
    t.textContent = full;
    t.setAttribute('font-size', 13);
    t.setAttribute('stroke-width', 3);
    let w = t.getComputedTextLength();
    if (w > max) {
      t.setAttribute('font-size', 12);
      w = t.getComputedTextLength();
    }
    // Never below 12px: a name that is still too long is cut, and the tooltip carries all of it.
    for (let n = full.length - 1; w > max && n > 3; n--) {
      t.textContent = full.slice(0, n).trimEnd() + '…';
      w = t.getComputedTextLength();
    }
    t.classList.toggle('off', w > max);
  }
}

// ---------------------------------------------------------------------------------------------
// Tooltip next to the pointer (or the focused shape) for anything with data-tip.
// ---------------------------------------------------------------------------------------------

const tips = new WeakMap();

function installTips(svg) {
  if (tips.has(svg)) return;
  const tip = svg.parentElement.querySelector('.mm-tip');
  if (!tip) return;
  let timer = 0;
  const hide = () => { clearTimeout(timer); tip.removeAttribute('data-on'); };
  const fill = el => {
    const head = document.createElement('b');
    head.textContent = el.dataset.tip;
    tip.replaceChildren(head);
    if (el.dataset.sub) {
      const sub = document.createElement('span');
      sub.textContent = el.dataset.sub;
      tip.append(sub);
    }
  };
  const place = (x, y) => {
    const stage = svg.parentElement.getBoundingClientRect();
    const w = tip.offsetWidth, h = tip.offsetHeight;
    let left = x - stage.left + 14, top = y - stage.top + 18;
    if (left + w > stage.width - 6) left = x - stage.left - w - 12;
    if (top + h > stage.height - 6) top = y - stage.top - h - 12;
    tip.style.transform = `translate(${Math.max(6, left)}px, ${Math.max(6, top)}px)`;
  };
  const show = (el, x, y) => { fill(el); place(x, y); tip.setAttribute('data-on', ''); };

  svg.addEventListener('pointerover', e => {
    if (svg.hasAttribute('data-riding')) return;
    const el = e.target.closest?.('[data-tip]');
    if (!el) return hide();
    show(el, e.clientX, e.clientY);
    clearTimeout(timer);
    if (e.pointerType === 'touch') timer = setTimeout(hide, 1800);
  });
  svg.addEventListener('pointermove', e => { if (tip.hasAttribute('data-on') && e.pointerType !== 'touch') place(e.clientX, e.clientY); });
  svg.addEventListener('pointerleave', hide);
  svg.addEventListener('pointerdown', e => { if (e.pointerType !== 'touch') hide(); });
  svg.addEventListener('focusin', e => {
    const el = e.target.closest?.('[data-tip]');
    if (!el || !el.matches(':focus-visible') || svg.hasAttribute('data-riding')) return;
    const r = el.getBoundingClientRect();
    show(el, r.left + r.width / 2, r.bottom - 6);
  });
  svg.addEventListener('focusout', hide);
  tips.set(svg, hide);
}


// ---------------------------------------------------------------------------------------------
// The ride: a winged ant flies the rider through the tunnels while the view follows, and a painted
// sign at the chamber entrance names the place as it lands.
// ---------------------------------------------------------------------------------------------

const reduced = () => matchMedia('(prefers-reduced-motion: reduce)').matches;
const clamp = (v, a, b) => Math.max(a, Math.min(b, v));
const lerp = (a, b, k) => a + (b - a) * k;
const quad = (a, c, b, k) => (1 - k) * (1 - k) * a + 2 * (1 - k) * k * c + k * k * b;
const smooth = (a, b, v) => { const k = clamp((v - a) / (b - a), 0, 1); return k * k * (3 - 2 * k); };
const inOut = t => t < .5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2;

const BEAT = 450;            // the ant hovers at the entrance this long before the view lands
const ANT_FRACTION = .17;    // ant length as a share of the visible width, so it looks the same at every zoom
const ANT_LENGTH = 125;      // the alate's length in its own drawing units

let active = null;

/**
 * Plays the ride and resolves 'done', 'aborted' (another ride took over) or 'frozen' (test hook).
 * o: { start, end, kind: 'down' | 'up', view: [x, y, w, h], freeze?: 0..1,
 *      sign?: { kicker, title }, target?: { x, y, rx, ry } (the chamber it flies into) }
 */
export function ride(svg, o) {
  tips.get(svg)?.();
  active?.stop();
  const view0 = svg.getAttribute('viewBox').split(/\s+/).map(Number);
  const view1 = o.view;
  const setView = v => {
    svg.setAttribute('viewBox', v.map(n => +n.toFixed(3)).join(' '));
    svg.dispatchEvent(new CustomEvent('mapzoom', { detail: { width: v[2] } }));
  };
  const route = o.freeze == null && reduced() ? null : buildRoute(svg, o);

  if (!route) {
    // Reduced motion (or no route): a short cross-fade to the new view.
    return new Promise(resolve => {
      const out = svg.animate([{ opacity: 1 }, { opacity: 0 }], { duration: reduced() ? 110 : 160, fill: 'forwards' });
      out.onfinish = () => {
        setView(view1);
        const back = svg.animate([{ opacity: 0 }, { opacity: 1 }], { duration: reduced() ? 160 : 220 });
        out.cancel();
        back.onfinish = () => resolve('done');
      };
    });
  }

  return new Promise(resolve => {
    const layer = svg.querySelector('#rider');
    layer.replaceChildren();
    layer.setAttribute('opacity', 1);
    const arrive = arrival(route, o);
    const sign = buildSign(layer, svg, o, view1, arrive);
    const ant = buildAlate(layer);
    // About 2.5 to 2.9 s in all, the same length down and up.
    const travel = clamp(2000 + route.length * .25, 2050, 2450), total = travel + BEAT;
    const ratio = view0[3] / view0[2];
    const c0 = [view0[0] + view0[2] / 2, view0[1] + view0[3] / 2];
    const cf = [view1[0] + view1[2] / 2, view1[1] + view1[3] / 2];
    const heading = { x: 1, y: 0, facing: 1, pitch: 0 };
    let raf = 0, prev = performance.now();
    svg.setAttribute('data-riding', '');

    const draw = (t, dt) => {
      const ms = t * total;
      const tt = Math.min(1, ms / travel), beat = clamp((ms - travel) / BEAT, 0, 1);
      const e = inOut(tt);
      const d = arrive.stop * e;
      const pt = route.at(d);
      const w = Math.exp(lerp(Math.log(view0[2]), Math.log(view1[2]), e));
      // A little smaller when the whole colony is in view, so it fits in the sky above the nests.
      const s = w * lerp(ANT_FRACTION, ANT_FRACTION * .62, smooth(400, 1600, w)) / ANT_LENGTH;
      // The camera first catches up with the ant, then eases onto the place it is heading for.
      const follow = smooth(0, .22, tt), settle = smooth(.6, 1, tt);
      const cx = lerp(lerp(c0[0], pt.x, follow), cf[0], settle);
      const cy = lerp(lerp(c0[1], pt.y, follow), cf[1], settle);
      setView([cx - w / 2, cy - w * ratio / 2, w, w * ratio]);
      const ahead = route.at(Math.min(arrive.stop, d + 55 * s));
      const behind = route.at(Math.max(0, d - 55 * s));
      const hx = ahead.x - behind.x, hy = ahead.y - behind.y, hl = Math.hypot(hx, hy);
      const k = Math.min(1, dt / 90);
      if (hl > .01) {
        heading.x = lerp(heading.x, hx / hl, k);
        heading.y = lerp(heading.y, hy / hl, k);
      }
      if (Math.abs(heading.x) > .25) heading.facing = lerp(heading.facing, Math.sign(heading.x), Math.min(1, dt / 70));
      // Nose follows the flight path, and levels out to hover at the entrance.
      const level = 1 - smooth(.8, 1, tt);
      heading.pitch = lerp(heading.pitch, clamp(Math.atan2(heading.y, Math.abs(heading.x) + .12), -.8, .8) * level, k);
      const hover = Math.sin(ms * .0055) * 4 * smooth(.85, 1, tt);
      ant.pose(pt.x, pt.y, s, heading.facing, heading.pitch * 180 / Math.PI, ms, hover);
      ant.root.setAttribute('opacity', (Math.min(1, ms / 160) * (1 - smooth(.5, 1, beat))).toFixed(3));
      sign?.setAttribute('opacity', smooth(o.kind === 'up' ? .5 : .4, o.kind === 'up' ? .78 : .68, tt).toFixed(3));
    };

    const finish = status => {
      cancelAnimationFrame(raf);
      svg.removeAttribute('data-riding');
      if (active === handle) active = null;
      resolve(status);
    };
    const handle = { stop: () => { layer.setAttribute('opacity', 0); finish('aborted'); } };
    active = handle;

    if (o.freeze != null) {
      // Run the last few frames so the heading settles, then hold the requested one. It is drawn again every
      // frame so a zoom still running from the page load cannot move the view away from it.
      const to = clamp(o.freeze, .02, .98);
      for (let i = 0; i <= 30; i++) draw(Math.max(.01, to - .05 * (1 - i / 30)), 30);
      svg.setAttribute('data-riding', 'frozen');
      const hold = () => { draw(to, 0); raf = requestAnimationFrame(hold); };
      raf = requestAnimationFrame(hold);
      resolve('frozen');
      return;
    }

    const t0 = performance.now();
    const frame = now => {
      const t = clamp((now - t0) / total, 0, 1);
      draw(t, Math.min(50, now - prev));
      prev = now;
      if (t < 1) raf = requestAnimationFrame(frame);
      else {
        setView(view1);
        layer.setAttribute('opacity', 0);
        finish('done');
      }
    };
    raf = requestAnimationFrame(frame);
  });
}

/** Where the ant stops to hover: just inside the chamber it flies into, or up in the sky beside its nest. */
function arrival(route, o) {
  if (!o.target) return { stop: route.length, door: route.door, outward: { x: 0, y: -1 }, centre: route.door };
  const c = o.target;
  const inside = p => ((p.x - c.x) / c.rx) ** 2 + ((p.y - c.y) / c.ry) ** 2 <= 1;
  let d = route.length;
  while (d > 0 && inside(route.at(d))) d -= 2;
  const door = route.at(d);
  const nx = (door.x - c.x) / c.rx, ny = (door.y - c.y) / c.ry, nl = Math.hypot(nx, ny) || 1;
  return { stop: d + (route.length - d) * .4, door, outward: { x: nx / nl, y: ny / nl }, centre: c };
}

/** Finds the tunnel route between two nodes (chambers, or 'nest:<repo>' surface entrances). */
function buildRoute(svg, o) {
  const nodes = new Map(), adj = new Map();
  const link = (a, b, seg) => { if (!adj.has(a)) adj.set(a, []); adj.get(a).push({ to: b, ...seg }); };
  for (const p of svg.querySelectorAll('path.route')) {
    const a = p.dataset.a, b = p.dataset.b;
    if (!a || !b) continue;
    const len = p.getTotalLength();
    nodes.set(a, p.getPointAtLength(0));
    nodes.set(b, p.getPointAtLength(len));
    link(a, b, { p, len, rev: false });
    link(b, a, { p, len, rev: true });
  }
  if (!nodes.has(o.start) || !nodes.has(o.end) || o.start === o.end) return null;

  // Nest entrances are joined over the surface so every chamber can be reached.
  const nests = [...nodes].filter(([k]) => k.startsWith('nest:')).sort((u, v) => u[1].x - v[1].x);
  for (let i = 1; i < nests.length; i++) {
    const [ka, pa] = nests[i - 1], [kb, pb] = nests[i];
    const len = Math.hypot(pb.x - pa.x, pb.y - pa.y);
    link(ka, kb, { line: [pa, pb], len, rev: false });
    link(kb, ka, { line: [pa, pb], len, rev: true });
  }
  const surface = nests.length ? nests[0][1].y + 2 : 170;

  // Dijkstra over a graph of ~130 nodes.
  const dist = new Map([[o.start, 0]]), from = new Map(), todo = new Set([o.start]), seen = new Set();
  while (todo.size) {
    let u = null;
    for (const n of todo) if (u === null || dist.get(n) < dist.get(u)) u = n;
    todo.delete(u);
    if (u === o.end) break;
    seen.add(u);
    for (const edge of adj.get(u) ?? []) {
      if (seen.has(edge.to)) continue;
      const d = dist.get(u) + edge.len;
      if (d < (dist.get(edge.to) ?? Infinity)) { dist.set(edge.to, d); from.set(edge.to, { node: u, edge }); todo.add(edge.to); }
    }
  }
  if (!from.has(o.end)) return null;
  const legs = [];
  for (let n = o.end; n !== o.start;) { const f = from.get(n); legs.unshift(f.edge); n = f.node; }

  const pts = [];
  const push = (x, y) => pts.push({ x, y });
  const sky = (nest, away) => {
    // A swoop through the sky down into the nest entrance (or up and out of it), on the side with room.
    const n = nodes.get(nest);
    const side = n.x > 800 ? -1 : 1;
    const a = { x: n.x + side * 200, y: n.y - 92 }, c = { x: n.x + side * 20, y: n.y - 100 };
    for (let i = 0; i <= 18; i++) {
      const k = away ? 1 - i / 18 : i / 18;
      push(quad(a.x, c.x, n.x, k), quad(a.y, c.y, n.y, k));
    }
  };
  if (o.kind === 'down' && o.start.startsWith('nest:')) sky(o.start, false);
  for (const leg of legs) {
    const n = Math.max(2, Math.ceil(leg.len / 4));
    const lift = leg.line ? Math.min(70, leg.len * .3) : 0;   // between nests it flies in an arc over the grass
    for (let i = 0; i <= n; i++) {
      const s = i / n * leg.len;
      if (leg.p) { const q = leg.p.getPointAtLength(leg.rev ? leg.len - s : s); push(q.x, q.y); }
      else {
        const [a, b] = leg.rev ? [leg.line[1], leg.line[0]] : leg.line;
        push(lerp(a.x, b.x, i / n), lerp(a.y, b.y, i / n) - lift * Math.sin(Math.PI * i / n));
      }
    }
  }
  if (o.kind === 'up' && o.end.startsWith('nest:')) sky(o.end, true);

  const cum = [0];
  for (let i = 1; i < pts.length; i++) cum.push(cum[i - 1] + Math.hypot(pts[i].x - pts[i - 1].x, pts[i].y - pts[i - 1].y));
  const length = cum[cum.length - 1];
  if (length < 5) return null;
  const at = d => {
    d = clamp(d, 0, length);
    let lo = 0, hi = cum.length - 1;
    while (hi - lo > 1) { const m = (lo + hi) >> 1; if (cum[m] <= d) lo = m; else hi = m; }
    const k = cum[hi] === cum[lo] ? 0 : (d - cum[lo]) / (cum[hi] - cum[lo]);
    return { x: lerp(pts[lo].x, pts[hi].x, k), y: lerp(pts[lo].y, pts[hi].y, k) };
  };
  return { at, length, surface, door: o.end.startsWith('nest:') ? nodes.get(o.end) : null };
}

const OUTLINE = 'var(--tunnel)', BONE = 'var(--ant)', SHADE = '#a39683', COAT = 'var(--accent)', COAT_DARK = '#b46c60', SKIN = '#ecd6bb';
const WING = 'rgb(214 228 240 / .2)', WING_EDGE = 'rgb(236 242 248 / .75)', VEIN = 'rgb(236 242 248 / .5)';
const WOOD = '#6b4529', WOOD_EDGE = '#2a1a0e', PAINT = '#fff6e6', PAINT_2 = '#ffe1d6';

function make(tag, attrs, parent) {
  const e = document.createElementNS(NS, tag);
  for (const k in attrs) e.setAttribute(k, attrs[k]);
  parent?.appendChild(e);
  return e;
}

const line = (d, w, colour, parent) =>
  make('path', { d, fill: 'none', stroke: colour, 'stroke-width': w, 'stroke-linecap': 'round', 'stroke-linejoin': 'round' }, parent);

/** Two-bone limb: the joint that bends outwards and up for a given root and tip. */
function knee(hx, hy, fx, fy, l1, l2, bias, vert = 1) {
  let dx = fx - hx, dy = fy - hy;
  let d = Math.hypot(dx, dy);
  const reach = l1 + l2 - .5;
  if (d > reach) { fx = hx + dx * reach / d; fy = hy + dy * reach / d; dx = fx - hx; dy = fy - hy; d = reach; }
  const a = (l1 * l1 - l2 * l2 + d * d) / (2 * d);
  const h = Math.sqrt(Math.max(0, l1 * l1 - a * a));
  const mx = hx + dx * a / d, my = hy + dy * a / d;
  const c1 = [mx + h * dy / d, my - h * dx / d], c2 = [mx - h * dy / d, my + h * dx / d];
  const score = c => bias * c[0] * .8 - vert * c[1];
  return { k: score(c1) >= score(c2) ? c1 : c2, f: [fx, fy] };
}

/**
 * The winged ant (an alate) and its rider, drawn facing right with the origin on the thorax: head with compound
 * eye, ocelli and elbowed antennae, thorax, waist node, banded gaster, six legs hanging in flight, and a veined
 * forewing and hindwing on each side that beat about their hinges.
 */
function buildAlate(layer) {
  const root = make('g', {}, layer);
  const body = make('g', {}, root);
  const stroke = { stroke: OUTLINE, 'stroke-width': 1.6, 'stroke-linejoin': 'round' };
  const outlined = (d, w, colour, parent) => { line(d, w + 2.6, OUTLINE, parent); return line(d, w, colour, parent); };

  const wingPair = (parent, hinge, ghost) => {
    const pair = { hinge, parts: [] };
    const add = (lag, amp, draw, opacity = 1) => {
      const g = make('g', { opacity }, parent);
      draw(g);
      pair.parts.push({ g, lag, amp });
    };
    const fore = g => {
      make('path', { d: 'M0,0 C-8,-9 -36,-17 -64,-15 C-79,-14 -84,-6 -76,-1 C-58,5 -26,5 0,0Z', fill: WING, stroke: WING_EDGE, 'stroke-width': 1.1, 'stroke-linejoin': 'round' }, g);
      line('M0,0 C-18,-9 -40,-13 -60,-13 M-3,-1 C-22,-4 -44,-5 -68,-6 M-8,1 C-26,2 -46,1 -64,0 M-30,-11 L-36,-3 M-46,-13 L-50,-5 M-36,-4 L-40,2', .8, VEIN, g);
      make('ellipse', { cx: -54, cy: -12.5, rx: 5, ry: 1.8, fill: 'rgb(90 62 44 / .7)' }, g);   // the dark spot near the wing tip
    };
    const hind = g => {
      make('path', { d: 'M0,0 C-9,-5 -32,-9 -48,-7 C-57,-6 -58,0 -50,2 C-34,4 -14,3 0,0Z', fill: WING, stroke: WING_EDGE, 'stroke-width': 1, 'stroke-linejoin': 'round' }, g);
      line('M-2,0 C-16,-4 -32,-5 -46,-4 M-24,-5 L-28,1', .7, VEIN, g);
    };
    add(.55, .85, hind);
    if (ghost) add(Math.PI, 1, fore, .28);   // a faint copy on the opposite stroke reads as a fast beat
    add(0, 1, fore);
    return pair;
  };

  // Far side first: wings, legs and antenna in shade behind the body.
  const farWings = wingPair(body, [-9, -12], false);
  const farLegs = make('g', {}, body);
  const farAntenna = make('g', {}, body);
  outlined('M28,-4 L32,-23 Q42,-27 51,-20', 2.2, SHADE, farAntenna);
  make('ellipse', { cx: 52.5, cy: -18.5, rx: 3, ry: 2.1, transform: 'rotate(40 52.5 -18.5)', fill: SHADE, ...stroke, 'stroke-width': 1.2 }, farAntenna);

  make('ellipse', { cx: -40, cy: 7, rx: 23, ry: 15, transform: 'rotate(14 -40 7)', fill: BONE, ...stroke }, body);     // gaster
  line('M-50,-5 Q-45,8 -52,20 M-38,-7 Q-33,8 -40,22 M-27,-4 Q-23,8 -28,18', 1.3, SHADE, body);                        // its bands
  line('M-61,15 L-66,18', 1.8, OUTLINE, body);
  make('ellipse', { cx: -17, cy: 3, rx: 4.5, ry: 6, fill: BONE, ...stroke }, body);                                       // waist node
  make('path', { d: 'M-12,2 C-14,-10 -4,-15 6,-14 C14,-13 17,-6 15,2 C12,9 -8,10 -12,2Z', fill: BONE, ...stroke }, body);  // thorax
  make('ellipse', { cx: 16, cy: 1, rx: 4, ry: 4, fill: BONE, ...stroke }, body);                                          // neck
  make('ellipse', { cx: 26, cy: 2, rx: 11, ry: 9.5, fill: BONE, ...stroke }, body);                                       // head
  make('path', { d: 'M34,6 Q44,6 43,13 Q39,10 33,10Z', fill: BONE, ...stroke }, body);                                   // mandible
  make('ellipse', { cx: 28.5, cy: -.5, rx: 3.8, ry: 4.6, fill: OUTLINE }, body);                                          // compound eye
  make('circle', { cx: 29.7, cy: -2.2, r: 1.1, fill: BONE }, body);
  for (const [x, y] of [[19, -5.5], [21.5, -7], [24, -7.4]]) make('circle', { cx: x, cy: y, r: .9, fill: OUTLINE }, body);  // ocelli
  const nearLegs = make('g', {}, body);

  // The rider: saddle, leg, leaning torso, scarf in the wind, head with a miner's helmet and lamp.
  make('ellipse', { cx: 3, cy: -13, rx: 10, ry: 3.6, fill: COAT_DARK, ...stroke, 'stroke-width': 1 }, body);
  outlined('M5,-12 L15,-9 L12,1', 4.4, COAT_DARK, body);
  const scarf = [line('', 5.6, OUTLINE, body), line('', 3.4, COAT_DARK, body)];
  outlined('M4,-13 L15,-29', 10, COAT, body);
  make('circle', { cx: 20.5, cy: -38, r: 7, fill: SKIN, ...stroke, 'stroke-width': 1.2 }, body);
  make('circle', { cx: 23.5, cy: -38.5, r: 1.1, fill: OUTLINE }, body);
  make('path', { d: 'M13.2,-39.5 A7.3,7.3 0 0 1 27.8,-39.5 L30,-39.5 L30,-37.5 L12.3,-37.5Z', fill: '#e2b04a', ...stroke, 'stroke-width': 1.2 }, body);
  make('circle', { cx: 28.5, cy: -42.5, r: 9, fill: 'rgb(255 238 170 / .25)' }, body);
  make('circle', { cx: 28.5, cy: -42.5, r: 2.2, fill: '#fff4b8' }, body);

  // The near antenna, elbowed: a long scape up from the head, then the beaded flagellum forward to a club.
  // The rider holds on to the scape.
  const base = [31, -4], elbow = [35, -24];
  const sway = make('g', {}, body);
  outlined(`M${base} L${elbow} Q45,-28 55,-20`, 2.5, BONE, sway);
  for (const [x, y] of [[40, -26], [45, -26.4], [50, -24]]) make('circle', { cx: x, cy: y, r: .7, fill: OUTLINE }, sway);
  make('ellipse', { cx: 56.5, cy: -18.5, rx: 3.4, ry: 2.4, transform: 'rotate(40 56.5 -18.5)', fill: BONE, ...stroke, 'stroke-width': 1.3 }, sway);
  const grips = [.42, .7].map(f => [lerp(base[0], elbow[0], f), lerp(base[1], elbow[1], f)]);
  const shoulders = [[15, -29], [13, -30]];
  const arms = shoulders.map(() => ({
    outline: line('', 6, OUTLINE, body),
    sleeve: line('', 3.8, COAT, body),
    hand: make('circle', { r: 3.1, fill: SKIN, ...stroke, 'stroke-width': 1 }, body),
  }));

  const nearWings = wingPair(body, [-6, -11], true);

  // Legs hang in flight, swinging a little: a hip on the thorax, a joint, a foot trailing below.
  const spec = [
    { hip: [-6, 7], foot: [-24, 27], bias: -1 }, { hip: [1, 9], foot: [-3, 32], bias: 0 }, { hip: [8, 8], foot: [20, 28], bias: 1 },
  ];
  const legs = [];
  for (const near of [true, false]) {
    spec.forEach((s, i) => {
      const parent = near ? nearLegs : farLegs;
      legs.push({
        s, i, near,
        out: line('', 5.6, OUTLINE, parent),
        inn: line('', 3.2, near ? BONE : SHADE, parent),
      });
    });
  }

  const beat = (pair, flap) => {
    for (const p of pair.parts) {
      const angle = 12 + 26 * p.amp * Math.sin(flap - p.lag);
      const sy = .62 + .38 * Math.abs(Math.cos(flap - p.lag));
      p.g.setAttribute('transform', `translate(${pair.hinge[0]} ${pair.hinge[1]}) rotate(${angle.toFixed(2)}) scale(1 ${sy.toFixed(3)})`);
    }
  };

  const pose = (x, y, scale, facing, pitch, ms, hover) => {
    root.setAttribute('transform', `translate(${x.toFixed(2)} ${y.toFixed(2)}) scale(${(scale * facing).toFixed(4)} ${scale.toFixed(4)}) rotate(${pitch.toFixed(2)})`);
    const flap = ms * .072;   // about 11 beats a second
    body.setAttribute('transform', `translate(0 ${(hover - Math.sin(flap) * 1.2).toFixed(2)})`);
    beat(farWings, flap + .2);
    beat(nearWings, flap);
    for (const leg of legs) {
      const off = leg.near ? [0, 0] : [3, -1];
      const hip = [leg.s.hip[0] + off[0], leg.s.hip[1] + off[1]];
      const fx = leg.s.foot[0] + off[0] + Math.sin(ms / 170 + leg.i * 1.3 + (leg.near ? 0 : 1)) * 2.5;
      const fy = leg.s.foot[1] + off[1] + Math.cos(ms / 190 + leg.i) * 1.5;
      const j = knee(hip[0], hip[1], fx, fy, 15, 17, leg.s.bias);
      const d = `M${hip[0]},${hip[1]} L${j.k[0].toFixed(2)},${j.k[1].toFixed(2)} L${j.f[0].toFixed(2)},${j.f[1].toFixed(2)}`;
      leg.out.setAttribute('d', d);
      leg.inn.setAttribute('d', d);
    }
    const f1 = Math.sin(ms / 85) * 2.6, f2 = Math.sin(ms / 85 - 1.2) * 3.4;
    const sd = `M15,-31 Q5,${(-33 + f1).toFixed(2)} -6,${(-29 + f2).toFixed(2)}`;
    scarf.forEach(p => p.setAttribute('d', sd));
    // The antenna sways about its base; the hands move with it.
    const a = Math.sin(ms / 260) * .05;
    sway.setAttribute('transform', `rotate(${(a * 180 / Math.PI).toFixed(2)} ${base[0]} ${base[1]})`);
    const cos = Math.cos(a), sin = Math.sin(a);
    arms.forEach((arm, i) => {
      const g = grips[i], sh = shoulders[i];
      const hx = base[0] + (g[0] - base[0]) * cos - (g[1] - base[1]) * sin;
      const hy = base[1] + (g[0] - base[0]) * sin + (g[1] - base[1]) * cos;
      const j = knee(sh[0], sh[1], hx, hy, 12, 12, 0, -1);
      const d = `M${sh[0]},${sh[1]} L${j.k[0].toFixed(2)},${j.k[1].toFixed(2)} L${hx.toFixed(2)},${hy.toFixed(2)}`;
      arm.outline.setAttribute('d', d);
      arm.sleeve.setAttribute('d', d);
      arm.hand.setAttribute('cx', hx.toFixed(2));
      arm.hand.setAttribute('cy', hy.toFixed(2));
    });
  };
  return { root, pose };
}

/** Cuts a text with an ellipsis until it fits the given width. */
function fitText(t, max) {
  const full = t.textContent;
  for (let n = full.length - 1; t.getComputedTextLength() > max && n > 3; n--) t.textContent = full.slice(0, n).trimEnd() + '…';
}

/**
 * The arrival marker: a painted wooden sign nailed into the soil beside the chamber entrance, pointing at it,
 * or on a post in the grass beside the nest on the way back up. Sized for the view the ride lands on, so its
 * text is 14 and 22px on screen when the ant arrives.
 */
function buildSign(layer, svg, o, view, arrive) {
  if (!o.sign || !arrive.door) return null;
  const px = svg.getBoundingClientRect().width || 800;
  const u = view[2] / px;                       // map units per screen pixel at the landing view
  const g = make('g', { opacity: 0 }, layer);
  const post = make('rect', { fill: WOOD, stroke: WOOD_EDGE, 'stroke-width': 1.5 * u }, g);
  // The board is drawn in screen pixels and scaled into the map, so its text is laid out at its real size.
  const board = make('g', {}, g);
  const plank = make('path', { fill: WOOD, stroke: WOOD_EDGE, 'stroke-width': 2, 'stroke-linejoin': 'round' }, board);
  const grain = line('', 1.2, 'rgb(0 0 0 / .22)', board);
  const kicker = make('text', { 'font-size': 14, 'font-weight': 600, fill: PAINT_2, 'text-anchor': 'middle' }, board);
  const title = make('text', { 'font-size': 22, 'font-family': 'Young Serif, serif', fill: PAINT, 'text-anchor': 'middle' }, board);
  kicker.textContent = o.sign.kicker;
  title.textContent = o.sign.title;
  const maxText = Math.min(px * .62, 360);
  fitText(kicker, maxText);
  fitText(title, maxText);
  const W = Math.max(kicker.getComputedTextLength(), title.getComputedTextLength()) + 36, H = 62, tip = 15;

  const { door, outward: n } = arrive;
  const nest = !o.target;
  const t = { x: -n.y, y: n.x };
  const room = (cx, cy) => {
    const mx = (W / 2 + tip + 10) * u, my = (H / 2 + 10) * u;
    const x = clamp(cx, view[0] + mx, view[0] + view[2] - mx), y = clamp(cy, view[1] + my, view[1] + view[3] - my);
    return { x, y, moved: Math.hypot(x - cx, y - cy) };
  };
  const side = (W / 2 + tip + 22) * u, off = (H / 2 + 12) * u;
  const spot = k => nest
    ? room(door.x + k * (W / 2 + tip + 30) * u, door.y - (H / 2 + 16) * u)
    : room(door.x + n.x * off + t.x * k * side, door.y + n.y * off + t.y * k * side);
  // By a nest the sign stands on the side away from where the ant climbs into the sky.
  const a = spot(1), b = spot(-1);
  const p = nest ? (door.x > 800 ? a : b) : a.moved <= b.moved ? a : b;

  // The point of the sign aims at the entrance.
  const dir = door.x >= p.x ? 1 : -1;
  const L = -W / 2, R = W / 2, T = -H / 2, B = H / 2;
  plank.setAttribute('d', dir > 0
    ? `M${L},${T} L${R},${T} L${R + tip},0 L${R},${B} L${L},${B}Z`
    : `M${R},${T} L${L},${T} L${L - tip},0 L${L},${B} L${R},${B}Z`);
  grain.setAttribute('d', `M${L + 8},${T + 7} L${R - 10},${T + 7} M${L + 14},${B - 6} L${R - 24},${B - 6}`);
  kicker.setAttribute('x', dir * -tip / 4); kicker.setAttribute('y', T + 22);
  title.setAttribute('x', dir * -tip / 4); title.setAttribute('y', T + 49);
  for (const x of [L + 7, R - 7]) make('circle', { cx: x, cy: 0, r: 2, fill: '#c9b089', stroke: WOOD_EDGE, 'stroke-width': .8 }, board);
  board.setAttribute('transform', `translate(${p.x.toFixed(3)} ${p.y.toFixed(3)}) rotate(${nest ? 0 : dir * -2}) scale(${u.toFixed(5)})`);
  if (nest) {
    post.setAttribute('x', p.x - 4 * u); post.setAttribute('width', 8 * u);
    post.setAttribute('y', p.y); post.setAttribute('height', Math.max(0, door.y + 4 * u - p.y));
  } else post.remove();
  return g;
}
