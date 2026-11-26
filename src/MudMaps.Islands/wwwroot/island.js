// A small sailing ship for the sea chart. Blazor draws the map; this module draws the ship, its wake, the quay and
// the dock board in a separate SVG laid over the chart, and steers the chart's viewBox so the view follows the ship.
// The ship and the board are drawn in screen pixels (scaled every frame), so the name on the sail and the island name
// on the board stay readable however far the chart is zoomed.
const NS = 'http://www.w3.org/2000/svg';
const WAKE_POINTS = 26;
// Where the ship lies at its mooring, in screen pixels from the end of the quay: off to the right and below.
const MOOR = [70, 30];

let moored = null;

export function reducedMotion() {
  return matchMedia('(prefers-reduced-motion: reduce)').matches;
}

function el(name, attrs, parent) {
  const node = document.createElementNS(NS, name);
  for (const k in attrs) node.setAttribute(k, attrs[k]);
  if (parent) parent.appendChild(node);
  return node;
}

// The ship and the board scale a little with the stage: full size on a desktop, never so small the names drop under 13 px.
const shipSize = stagePx => Math.max(.85, Math.min(1.2, stagePx / 1000));
const clamp01 = x => Math.max(0, Math.min(1, x));
const smooth = x => { x = clamp01(x); return x * x * (3 - 2 * x); };
const ease = t => t < .5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2;

// The bend of the course. It depends on the two ends only, not on which way the ship goes, so the voyage
// back out retraces the voyage in. The course bows toward the top of the chart.
function control(a, b) {
  const [p, q] = (a[0] > b[0] || (a[0] === b[0] && a[1] > b[1])) ? [b, a] : [a, b];
  const dx = q[0] - p[0], dy = q[1] - p[1], d = Math.hypot(dx, dy) || 1;
  let nx = -dy / d, ny = dx / d;
  if (ny > 0) { nx = -nx; ny = -ny; }
  const bow = Math.min(160, d * .2);
  return [(p[0] + q[0]) / 2 + nx * bow, (p[1] + q[1]) / 2 + ny * bow];
}

// The repo name on the sail, on two lines when it is long and has a natural break.
function sailLines(name) {
  if (!name || name.length <= 12) return [name || ''];
  const mid = name.length / 2;
  let best = -1;
  for (let i = 1; i < name.length - 1; i++) {
    if ((name[i] === ' ' || name[i] === '.') && (best < 0 || Math.abs(i - mid) < Math.abs(best - mid))) best = i;
  }
  if (best < 0) return [name];
  return name[best] === '.' ? [name.slice(0, best + 1), name.slice(best + 1)] : [name.slice(0, best), name.slice(best + 1)];
}

// The ship in screen pixels, waterline at the origin, bow to the right. The hull flips with the heading;
// the square sail and its lettering never do, so the name always reads left to right.
function drawShip(parent, name, heading) {
  const ship = el('g', { class: 'ship' }, parent);
  const bob = el('g', { class: 'bob' }, ship);
  const hull = el('g', { transform: `scale(${heading},1)` }, bob);
  const lines = sailLines(name);
  const lineH = 18;
  const text = el('g', {}, bob);
  const widths = lines.map((line, i) => {
    const t = el('text', {
      x: 0, y: 0, 'text-anchor': 'middle', 'font-family': 'Anton, Impact, sans-serif', 'font-size': 16,
      fill: 'var(--ink)', 'letter-spacing': 0,
    }, text);
    t.textContent = line;
    t.dataset.line = i;
    return t.getComputedTextLength() || line.length * 7.5;
  });
  const sw = Math.max(64, Math.max(...widths) + 24);
  const sh = lines.length * lineH + 16;
  const foot = -16, top = foot - sh;
  const mastTop = top - 16;
  el('path', { d: `M-54,-4 L60,-4 Q52,12 34,15 L-42,15 Q-54,11 -54,-4 Z`, fill: 'var(--ink)' }, hull);
  el('path', { d: 'M-48,3.5 L54,3.5', stroke: 'var(--paper)', 'stroke-width': 1.4, fill: 'none' }, hull);
  el('path', { d: `M0,${mastTop} L58,-4 M0,${mastTop} L-50,-4`, stroke: 'var(--ink)', 'stroke-width': 1, fill: 'none' }, hull);
  el('line', { x1: 0, y1: -4, x2: 0, y2: mastTop, stroke: 'var(--ink)', 'stroke-width': 2.6 }, hull);
  el('path', { class: 'pennant', d: `M0,${mastTop} l20,4.5 l-20,4.5 z`, fill: 'var(--red)' }, hull);
  const sail = el('path', {
    d: `M${-sw / 2},${top} Q0,${top - 5} ${sw / 2},${top} Q${sw / 2 + 7},${(top + foot) / 2} ${sw / 2},${foot} ` +
       `Q0,${foot + 6} ${-sw / 2},${foot} Q${-sw / 2 - 7},${(top + foot) / 2} ${-sw / 2},${top} Z`,
    fill: 'var(--paper)', stroke: 'var(--ink)', 'stroke-width': 2, 'stroke-linejoin': 'round',
  });
  bob.insertBefore(sail, text);
  el('path', { d: `M${-sw / 2 - 4},${top} L${sw / 2 + 4},${top} M${-sw / 2 - 2},${foot} L${sw / 2 + 2},${foot}`, stroke: 'var(--ink)', 'stroke-width': 2.6 }, bob)
    .after(text);
  [...text.children].forEach((t, i) => t.setAttribute('y', top + 8 + lineH * (i + 1) - 4));
  return { ship, bob };
}

// The quay at an island: a jetty out from the coast (chart units) and, at its end, the dock board naming the island
// (screen pixels). Returns the parts so the voyage can raise them as the ship comes in.
function drawQuay(parent, v, px) {
  const quay = el('g', { class: 'quay' }, parent);
  const jetty = el('rect', {
    x: v.tx - 5 * px, y: v.coast, width: 10 * px, height: Math.max(0, v.ty - v.coast),
    fill: 'var(--paper-2)', stroke: 'var(--ink)', 'stroke-width': 1.5 * px,
  }, quay);
  const head = el('g', { transform: `translate(${v.tx},${v.ty}) scale(${px})` }, quay);
  const deck = el('g', {}, head);
  const board = el('g', { class: 'dock-board' }, head);
  const name = el('text', {
    x: 0, y: 0, 'text-anchor': 'middle', 'font-family': 'Newsreader, Georgia, serif', 'font-weight': 600, 'font-size': 18,
    fill: 'var(--ink)', 'letter-spacing': 0,
  }, board);
  name.textContent = v.sign;
  // The board hangs under the left end of the quay head, clear of the ship lying alongside on the right.
  const bw = Math.max(80, (name.getComputedTextLength() || v.sign.length * 9) + 28);
  const bx = 2 - bw, by = 14;
  el('rect', { x: bx - 8, y: -5, width: 58 - bx, height: 10, fill: 'var(--paper-2)', stroke: 'var(--ink)', 'stroke-width': 1.6 }, deck);
  for (let x = bx - 2; x <= 50; x += 26) el('line', { x1: x, y1: 5, x2: x, y2: 12, stroke: 'var(--ink)', 'stroke-width': 2 }, deck);
  el('path', { d: `M${bx + 12},5 L${bx + 12},${by} M${bx + bw - 12},5 L${bx + bw - 12},${by}`, stroke: 'var(--ink)', 'stroke-width': 1.4 }, board);
  board.insertBefore(el('rect', { x: bx, y: by, width: bw, height: 34, fill: 'var(--paper)', stroke: 'var(--ink)', 'stroke-width': 2 }), name);
  board.insertBefore(el('rect', { x: bx, y: by, width: bw, height: 5, fill: 'var(--red)' }), name);
  name.setAttribute('x', bx + bw / 2);
  name.setAttribute('y', by + 27);
  board.style.transformBox = 'fill-box';
  board.style.transformOrigin = 'center top';
  return { quay, jetty, head, board };
}

function crossfade(svg, view) {
  return new Promise(resolve => {
    svg.style.transition = 'opacity 120ms linear';
    svg.style.opacity = '0';
    setTimeout(() => {
      svg.setAttribute('viewBox', view.join(' '));
      svg.dispatchEvent(new CustomEvent('mapzoom', { detail: { width: view[2] } }));
      svg.style.opacity = '1';
      setTimeout(() => { svg.style.transition = ''; svg.style.opacity = ''; }, 150);
      resolve(1);
    }, 130);
  });
}

function overlayFor(svg, view) {
  const overlay = el('svg', { class: 'sail', viewBox: view.join(' '), 'aria-hidden': 'true' }, svg.parentElement);
  overlay.style.cssText = 'position:absolute;left:0;top:0;width:100%;height:100%;pointer-events:none;';
  return overlay;
}

// Keep the mooring (quay, board, ship at rest) on the chart while the island is open; it follows the chart's view.
function keepMoored(svg, overlay) {
  const observer = new MutationObserver(() => overlay.setAttribute('viewBox', svg.getAttribute('viewBox')));
  observer.observe(svg, { attributes: true, attributeFilter: ['viewBox'] });
  moored = { overlay, observer };
}

// Village names at a readable screen size (12.5 px): split at word and CamelCase breaks onto one or two lines that
// fit the village box, the last line cut with an ellipsis if it still runs over. Runs after each render while landed.
const fitting = new WeakSet();
export function fitVillages(svg) {
  if (!fitting.has(svg)) {
    // Follow the view: refit once per frame while a zoom runs.
    fitting.add(svg);
    let queued = false;
    svg.addEventListener('mapzoom', () => {
      if (queued) return;
      queued = true;
      requestAnimationFrame(() => { queued = false; fitVillages(svg); });
    });
  }
  const vb = svg.getAttribute('viewBox').split(/\s+/).map(Number);
  const u = vb[2] / (svg.getBoundingClientRect().width || 1);
  const size = 12.5 * u;
  for (const t of svg.querySelectorAll('text.village')) {
    const name = t.dataset.name || '';
    const key = `${name}|${size.toFixed(3)}`;
    if (t.dataset.fit === key) continue;
    t.dataset.fit = key;
    const maxW = +t.dataset.w * .96;
    t.setAttribute('font-size', size.toFixed(3));
    t.setAttribute('stroke-width', (size * .3).toFixed(3));
    const words = name.split(/(?<=[a-z0-9])(?=[A-Z])|(?<=[ ._-])/).filter(Boolean);
    const width = s => { t.textContent = s; return t.getComputedTextLength(); };
    const lines = 2;
    const out = [];
    let i = 0;
    for (let l = 0; l < lines && i < words.length; l++) {
      let line = words[i++];
      while (i < words.length && width(line + words[i]) <= maxW) line += words[i++];
      out.push(line.trim());
    }
    if (i < words.length || width(out[out.length - 1]) > maxW) {
      let last = out[out.length - 1];
      while (last.length > 2 && width(last + '…') > maxW) last = last.slice(0, -1);
      out[out.length - 1] = last + '…';
    }
    t.textContent = '';
    const x = t.getAttribute('x');
    out.forEach((line, k) => {
      const span = el('tspan', { x, dy: k === 0 ? `${(.35 - (out.length - 1) * .58).toFixed(2)}em` : '1.16em' }, t);
      span.textContent = line;
    });
  }
}

// Take the mooring down: the ship leaves at once (a voyage redraws it), the quay and board fade.
export function unmoor(fadeMs = 400) {
  if (!moored) return;
  const { overlay, observer } = moored;
  moored = null;
  observer.disconnect();
  overlay.querySelector('.ship')?.remove();
  overlay.style.transition = `opacity ${fadeMs}ms ease-out`;
  overlay.style.opacity = '0';
  setTimeout(() => overlay.remove(), fadeMs + 50);
}

// Draw the mooring at rest without a voyage (deep links straight to an island).
export function moor(svg, v) {
  unmoor(0);
  const stagePx = svg.getBoundingClientRect().width || 1;
  const px = v.view[2] / stagePx * shipSize(stagePx);
  const overlay = overlayFor(svg, svg.getAttribute('viewBox').split(/\s+/).map(Number));
  drawQuay(overlay, v, px);
  const { ship } = drawShip(overlay, v.name, 1);
  ship.setAttribute('transform', `translate(${v.tx + MOOR[0] * px},${v.ty + MOOR[1] * px}) scale(${px})`);
  ship.classList.add('at-rest');
  keepMoored(svg, overlay);
}

// Sail from the quay at (fx, fy) to the quay at (tx, ty) while the chart view travels from where it is now to
// v.view. Three movements: the camera eases back to show open water, follows the ship across it, then closes in
// on the island as the board rises on its quay and the ship comes alongside. The sailing takes v.ms (2.4 to 2.8 s,
// ease-in-out), then a half-second arrival beat holds before the promise resolves and the caller lands.
// v.freeze in [0, 1) stops there and leaves the frame up (screenshot hook). Reduced motion: a short cross-fade.
export function sail(svg, v) {
  const target = v.view;
  const home = !v.dest;
  if (reducedMotion()) {
    unmoor(0);
    return crossfade(svg, target).then(r => { if (!home) moor(svg, v); return r; });
  }

  const stagePx = svg.getBoundingClientRect().width || 1;
  const size = shipSize(stagePx);
  const v0 = svg.getAttribute('viewBox').split(/\s+/).map(Number);
  const u0 = v0[2] / stagePx, u1 = target[2] / stagePx;
  // The ship leaves from where it lies at its mooring, if it is moored, and comes alongside the new quay.
  const fromMoor = moored !== null;
  const start = fromMoor ? [v.fx + MOOR[0] * u0 * size, v.fy + MOOR[1] * u0 * size] : [v.fx, v.fy];
  const end = home ? [v.tx, v.ty] : [v.tx + MOOR[0] * u1 * size, v.ty + MOOR[1] * u1 * size];
  unmoor(600);

  const overlay = overlayFor(svg, v0);
  const caps = el('g', { stroke: 'var(--blue)', 'stroke-width': 2, fill: 'none', 'stroke-linecap': 'round' }, overlay);
  const surf = el('g', { fill: 'none', stroke: 'var(--blue)' }, overlay);
  const quay = home ? null : drawQuay(overlay, v, u1 * size);
  const wake = el('g', { 'stroke-linecap': 'round', fill: 'none' }, overlay);
  const trail = Array.from({ length: WAKE_POINTS - 1 }, () => ({
    mid: el('line', { stroke: 'var(--blue)' }, wake),
    port: el('line', { stroke: 'var(--ink)' }, wake),
    starboard: el('line', { stroke: 'var(--ink)' }, wake),
  }));
  const heading = end[0] >= start[0] ? 1 : -1;
  const { ship, bob } = drawShip(overlay, v.name, heading);

  const c = control(start, end);
  const bez = u => {
    const a = (1 - u) * (1 - u), b = 2 * (1 - u) * u, d = u * u;
    return [a * start[0] + b * c[0] + d * end[0], a * start[1] + b * c[1] + d * end[1]];
  };
  const dist = Math.hypot(end[0] - start[0], end[1] - start[1]);

  // Whitecaps scattered either side of the course: open water, showing mid-voyage.
  const capList = Array.from({ length: 16 }, (_, i) => {
    const u = (i + .5) / 16;
    const side = (i % 2 ? 1 : -1) * (40 + ((i * 37) % 70));
    return { u, side, node: el('path', { d: 'M-9,0 q4.5,-5 9,0 q4.5,-5 9,0' }, caps) };
  });
  const surfRings = home ? [] : [0, .5].map(phase => ({ phase, node: el('ellipse', { cx: v.isle[0], cy: v.isle[1] }, surf) }));
  const islandG = home ? null : [...svg.querySelectorAll('g.area')].find(g => g.getAttribute('data-hood') === v.dest);

  // The camera: from the current view out (or in) to a voyage width that shows the sea around the ship, along with
  // the ship, then in to the island's view. Its centre starts on the old view and ends on the new one.
  const c0 = [v0[0] + v0[2] / 2, v0[1] + v0[3] / 2];
  const c1 = [target[0] + target[2] / 2, target[1] + target[3] / 2];
  const off0 = [c0[0] - start[0], c0[1] - start[1]], off1 = [c1[0] - end[0], c1[1] - end[1]];
  const cruise = Math.min(Math.max(v0[2], target[2]), Math.max(Math.min(v0[2], target[2]), Math.min(1100, Math.max(520, dist * .8))));
  const aspect = target[3] / target[2];

  const draw = (t, now) => {
    const e = ease(t);
    const [x, y] = bez(e);
    const out = smooth(t / .32), inn = smooth((t - .6) / .4);
    const w = v0[2] + (cruise - v0[2]) * out + (target[2] - cruise) * inn;
    const h = w * aspect;
    const cx = x + off0[0] * (1 - out) + off1[0] * inn;
    const cy = y + off0[1] * (1 - out) + off1[1] * inn;
    const box = `${cx - w / 2} ${cy - h / 2} ${w} ${h}`;
    svg.setAttribute('viewBox', box);
    overlay.setAttribute('viewBox', box);
    svg.dispatchEvent(new CustomEvent('mapzoom', { detail: { width: w } }));
    // Close in, the chart's names would swell past the ship and the board: they fade while the camera is near.
    svg.classList.toggle('near', w < 640);

    const px = w / stagePx;                 // chart units per screen pixel right now
    const k = px * size;
    // Slight heel and pitch under way; settles as she comes alongside.
    const sea = 1 - smooth((t - .85) / .15) * .7;
    ship.setAttribute('transform', `translate(${x.toFixed(2)},${y.toFixed(2)}) scale(${k.toFixed(4)})`);
    bob.setAttribute('transform', `translate(0,${(Math.sin(now / 230) * 1.6 * sea).toFixed(2)}) rotate(${(Math.sin(now / 310) * 3 * sea).toFixed(2)})`);
    ship.style.opacity = fromMoor ? '1' : String(Math.min(1, .15 + t / .06));

    // Open water: whitecaps along the course, brightest mid-voyage, drifting with the swell.
    const capsOn = Math.sin(Math.PI * clamp01(t)) * .75;
    for (const cap of capList) {
      const [qx, qy] = bez(cap.u);
      const [rx, ry] = bez(Math.min(1, cap.u + .01));
      const len = Math.hypot(rx - qx, ry - qy) || 1;
      const nx = -(ry - qy) / len, ny = (rx - qx) / len;
      const drift = Math.sin(now / 900 + cap.u * 9) * 6;
      cap.node.setAttribute('transform', `translate(${(qx + nx * cap.side * px + drift * px).toFixed(2)},${(qy + ny * cap.side * px).toFixed(2)}) scale(${px.toFixed(4)})`);
      cap.node.setAttribute('opacity', (capsOn * (.4 + .6 * Math.abs(Math.sin(now / 700 + cap.u * 13)))).toFixed(2));
    }

    if (!home) {
      // The island grows into view as the ship approaches; surf breaks on its shore.
      if (islandG) islandG.style.transform = `scale(${(.86 + .14 * smooth((t - .2) / .8)).toFixed(4)})`;
      const surfOn = smooth((t - .45) / .3);
      for (const ring of surfRings) {
        const p = ((now / 1500) + ring.phase) % 1;
        ring.node.setAttribute('rx', (v.isle[2] * (1 + p * .35)).toFixed(2));
        ring.node.setAttribute('ry', (v.isle[3] * (1 + p * .35)).toFixed(2));
        ring.node.setAttribute('stroke-width', (2 * px).toFixed(3));
        ring.node.setAttribute('opacity', (surfOn * (1 - p) * .8).toFixed(2));
      }
      // The quay and its board rise as the island comes close, so the name is up before the ship is alongside.
      const rise = smooth((t - .45) / .25);
      quay.quay.setAttribute('opacity', rise.toFixed(2));
      quay.head.setAttribute('transform', `translate(${v.tx},${v.ty}) scale(${k.toFixed(4)})`);
      quay.jetty.setAttribute('x', (v.tx - 5 * k).toFixed(2));
      quay.jetty.setAttribute('width', (10 * k).toFixed(2));
      quay.jetty.setAttribute('stroke-width', (1.5 * k).toFixed(3));
      quay.board.style.transform = `scaleY(${rise.toFixed(3)})`;
    }

    // The wake: a blue line along the course just sailed, with two thin lines spreading out behind the stern.
    const back = Math.min(e, .3);
    let [ax, ay] = bez(e - back);
    for (let i = 0; i < trail.length; i++) {
      const u = e - back * (1 - (i + 1) / trail.length);
      const [qx, qy] = bez(u);
      const age = (i + 1) / trail.length;   // 0 at the tail, 1 at the stern
      const len = Math.hypot(qx - ax, qy - ay) || 1;
      const nx = -(qy - ay) / len, ny = (qx - ax) / len;
      const spread = (1 - age) * 26 * k + 2 * px;
      const s = trail[i];
      const line = (node, x1, y1, x2, y2, width, opacity) => {
        node.setAttribute('x1', x1.toFixed(2)); node.setAttribute('y1', y1.toFixed(2));
        node.setAttribute('x2', x2.toFixed(2)); node.setAttribute('y2', y2.toFixed(2));
        node.setAttribute('stroke-width', width.toFixed(3)); node.setAttribute('opacity', opacity.toFixed(2));
      };
      const fade = Math.min(1, t / .08) * (.15 + .75 * age) * (1 - smooth((t - .9) / .1) * .6);
      line(s.mid, ax, ay, qx, qy, k * (2 + 5 * age), Math.min(1, fade * 1.1));
      const ps = (1 - age + 1 / trail.length) * 26 * k + 2 * px;
      line(s.port, ax + nx * spread, ay + ny * spread, qx + nx * ps, qy + ny * ps, k * 1.4, fade * .8);
      line(s.starboard, ax - nx * spread, ay - ny * spread, qx - nx * ps, qy - ny * ps, k * 1.4, fade * .8);
      [ax, ay] = [qx, qy];
    }
  };

  const arrival = 500;
  return new Promise(resolve => {
    const t0 = performance.now();
    const finish = () => {
      if (islandG) islandG.style.transform = '';
      if (home) svg.classList.remove('near');
      caps.remove();
      surf.remove();
      wake.remove();
      if (home) {
        // Home: the ship sails into port and fades.
        overlay.style.transition = 'opacity 450ms ease-out';
        overlay.style.opacity = '0';
        setTimeout(() => overlay.remove(), 500);
      } else {
        ship.setAttribute('transform', `translate(${end[0]},${end[1]}) scale(${u1 * size})`);
        bob.removeAttribute('transform');
        ship.classList.add('at-rest');
        keepMoored(svg, overlay);
      }
      resolve(1);
    };
    const frame = now => {
      const t = Math.min(1, (now - t0) / v.ms);
      if (v.freeze >= 0 && t >= v.freeze) { draw(v.freeze, now); resolve(v.freeze); return; }
      draw(t, now);
      if (t < 1) { requestAnimationFrame(frame); return; }
      // Arrival beat: alongside, the board up; hold the readable frame before the caller lands.
      const held = performance.now();
      const hold = n => {
        draw(1, n);
        if (n - held < arrival) requestAnimationFrame(hold); else finish();
      };
      requestAnimationFrame(hold);
    };
    draw(0, t0);
    requestAnimationFrame(frame);
  });
}
