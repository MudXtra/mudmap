// Walking ants for the colony map. Blazor draws the colony; this only moves the ants.
const NS = 'http://www.w3.org/2000/svg';

const running = new WeakMap();

export function start(svg, total, merged, fullWidth = 1200) {
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
