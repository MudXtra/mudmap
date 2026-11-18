// Planets drift along their orbits. Blazor draws everything; this only moves the planet groups.
const state = new WeakMap();

export function start(svg) {
  const planets = [...svg.querySelectorAll('g.planet')].map(g => ({
    g, key: g.dataset.key, cx: +g.dataset.cx, cy: +g.dataset.cy, orbit: +g.dataset.orbit, a: +g.dataset.angle, speed: +g.dataset.speed,
  }));
  for (const p of planets) { p.x = p.cx + Math.cos(p.a) * p.orbit; p.y = p.cy + Math.sin(p.a) * p.orbit * .5; }
  const existing = state.get(svg);
  if (existing) {
    // A live update: keep each planet's current angle, adopt the new set, reuse the running loop.
    const angles = new Map(existing.planets.map(p => [p.key, p.a]));
    for (const p of planets) if (angles.has(p.key)) p.a = angles.get(p.key);
    existing.planets = planets;
    return;
  }
  const s = { planets, paused: matchMedia('(prefers-reduced-motion: reduce)').matches };
  state.set(svg, s);
  let last = performance.now();
  const frame = now => {
    const dt = Math.min(50, now - last); last = now;
    for (const p of s.planets) {
      if (!s.paused) p.a += p.speed * dt / 1000;
      p.x = p.cx + Math.cos(p.a) * p.orbit;
      p.y = p.cy + Math.sin(p.a) * p.orbit * .5;
      p.g.setAttribute('transform', `translate(${p.x.toFixed(2)},${p.y.toFixed(2)})`);
    }
    requestAnimationFrame(frame);
  };
  requestAnimationFrame(frame);
}

export function hold(svg, key) {
  const s = state.get(svg);
  if (!s) return null;
  s.paused = true;
  const p = s.planets.find(x => x.key === key);
  return p ? [p.x, p.y] : null;
}

export function release(svg) {
  const s = state.get(svg);
  if (s) s.paused = matchMedia('(prefers-reduced-motion: reduce)').matches;
}
