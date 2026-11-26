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

// On narrow screens the detail sheet comes up from the bottom: scroll the map (and its trail) to the top so it stays
// in sight above the sheet. Wide screens use a side sheet and need nothing.
export function revealMap() {
  if (innerWidth >= 960) return;
  const stage = document.querySelector('.mm-stage');
  if (!stage) return;
  const anchor = document.querySelector('.mm-trail') ?? stage;
  const top = anchor.getBoundingClientRect().top;
  if (top >= 0 && stage.getBoundingClientRect().bottom <= innerHeight * 0.5) return;
  scrollTo({ top: scrollY + top - 8, behavior: reducedMotion() ? 'auto' : 'smooth' });
}

// ---- Labels that fit their shape ---------------------------------------------------------------------------
// SVG labels are measured in the browser and cut with an ellipsis so they never leave their plate or shape:
//   <text data-fit data-full="Full name" data-maxw="120" data-font="20" [data-minpx="12"] [data-maxfont="30"]>
// data-maxw is the widest the text may run, in map units. The font grows from data-font (map units) so it renders
// at least data-minpx on screen (12 by default), up to data-maxfont. A label whose previous sibling is a
// <rect data-tag data-cx data-cy data-pad> gets that plate fitted around it, centred on (cx, cy).
// The app keeps the full name in a tooltip, a <title> or the detail panel.
// A map that animates its camera can pass the screen pixels per map unit it is about to show as scaleOverride.
export function fitLabels(svg, scaleOverride) {
  if (!svg?.isConnected) return;
  const ctm = scaleOverride ? null : svg.getScreenCTM();
  const scale = scaleOverride || (ctm ? Math.hypot(ctm.a, ctm.b) : 0);
  if (!scale) return;
  for (const el of svg.querySelectorAll('text[data-fit]')) fitOne(el, scale);
}

function fitOne(el, scale) {
  const d = el.dataset;
  const full = d.full ?? el.textContent;
  const maxw = +d.maxw || Infinity;
  const base = +d.font || parseFloat(el.getAttribute('font-size')) || 12;
  const cap = +d.maxfont || base * 2;
  const font = Math.min(cap, Math.max(base, (+d.minpx || 12) / scale));
  el.setAttribute('font-size', font.toFixed(2));
  // Keep Blazor's own text node: it only rewrites it when the name itself changes, and then this runs again.
  let node = el.firstChild;
  if (!node || node.nodeType !== Node.TEXT_NODE) { el.textContent = full; node = el.firstChild; }
  node.nodeValue = full;
  let width = el.getComputedTextLength();
  if (width > maxw) {
    let lo = 0, hi = full.length - 1;
    while (lo < hi) {
      const mid = (lo + hi + 1) >> 1;
      node.nodeValue = full.slice(0, mid).trimEnd() + '…';
      if (el.getComputedTextLength() <= maxw) lo = mid; else hi = mid - 1;
    }
    node.nodeValue = full.slice(0, Math.max(1, lo)).trimEnd() + '…';
    width = el.getComputedTextLength();
  }
  const plate = el.previousElementSibling;
  if (plate?.hasAttribute('data-tag')) {
    const cx = +plate.dataset.cx, cy = +plate.dataset.cy, pad = +plate.dataset.pad || font * .5;
    const w = width + pad * 2, h = font * 1.5;
    plate.setAttribute('x', (cx - w / 2).toFixed(2));
    plate.setAttribute('y', (cy - h / 2).toFixed(2));
    plate.setAttribute('width', w.toFixed(2));
    plate.setAttribute('height', h.toFixed(2));
    plate.setAttribute('rx', (h / 2).toFixed(2));
    el.setAttribute('x', cx.toFixed(2));
    el.setAttribute('y', (cy + font * .35).toFixed(2));
  }
}

// "Skip animations": the map jumps to its new view and fades back in over a moment instead of travelling there.
export function fadeIn(svg, ms = 150) {
  if (!svg?.animate || reducedMotion()) return;
  svg.animate([{ opacity: 0.15 }, { opacity: 1 }], { duration: ms, easing: 'ease-out' });
}
