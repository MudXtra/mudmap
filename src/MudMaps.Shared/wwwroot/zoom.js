// Smooth viewBox zoom for the map SVGs. Respects reduced motion.
const frames = new WeakMap();

export function reducedMotion() {
  return matchMedia('(prefers-reduced-motion: reduce)').matches;
}

export function zoomTo(svg, x, y, w, h, ms) {
  if (!svg) return;
  const from = svg.getAttribute('viewBox').split(/\s+/).map(Number);
  const to = [x, y, w, h];
  cancelAnimationFrame(frames.get(svg));
  const duration = reducedMotion() ? 0 : ms;
  const start = performance.now();
  const step = now => {
    const k = duration ? Math.min(1, (now - start) / duration) : 1;
    const e = 1 - Math.pow(1 - k, 3);
    svg.setAttribute('viewBox', from.map((v, i) => v + (to[i] - v) * e).join(' '));
    svg.dispatchEvent(new CustomEvent('mapzoom', { detail: { width: from[2] + (to[2] - from[2]) * e } }));
    if (k < 1) frames.set(svg, requestAnimationFrame(step));
  };
  frames.set(svg, requestAnimationFrame(step));
}
