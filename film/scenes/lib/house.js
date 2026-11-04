// The Ellis house as a silhouette: the main house and Margaret's back unit.
// house(svgGroup) draws it around a ground-center origin and returns the
// window elements (with their glows) so a scene can light them over time.
// Windows: 0 main left, 1 main right, 2 back unit (Margaret's).
(function () {
  const NS = 'http://www.w3.org/2000/svg';
  function el(name, attrs, parent) {
    const e = document.createElementNS(NS, name);
    for (const k in attrs) e.setAttribute(k, attrs[k]);
    if (parent) parent.appendChild(e);
    return e;
  }
  window.house = function (g, opts = {}) {
    const ink = opts.ink || '#0e0c10';
    const defs = el('defs', {}, g);
    const blur = el('filter', { id: 'winGlow', x: '-150%', y: '-150%', width: '400%', height: '400%' }, defs);
    el('feGaussianBlur', { stdDeviation: '18' }, blur);
    // Bushes and ground.
    el('rect', { x: -3000, y: 0, width: 6000, height: 600, fill: opts.ground || '#0b0a0d' }, g);
    for (const [cx, r] of [[-560, 70], [-500, 52], [640, 64], [700, 48]]) el('circle', { cx, cy: -r * 0.55, r, fill: ink }, g);
    // Main house.
    el('rect', { x: 20, y: -560, width: 54, height: 140, fill: ink }, g);
    el('polygon', { points: '-470,-372 -150,-600 170,-372', fill: ink }, g);
    el('rect', { x: -430, y: -380, width: 560, height: 380, fill: ink }, g);
    // Margaret's back unit.
    el('polygon', { points: '198,-252 372,-392 546,-252', fill: ink }, g);
    el('rect', { x: 222, y: -260, width: 300, height: 260, fill: ink }, g);
    el('rect', { x: 395, y: -128, width: 70, height: 128, fill: '#060507' }, g);
    // Door of the main house.
    el('rect', { x: -186, y: -176, width: 92, height: 176, fill: '#060507' }, g);
    const spots = [[-360, -300, 124, 112], [6, -300, 124, 112], [262, -206, 104, 92]];
    const windows = spots.map(([x, y, w, h]) => {
      const glow = el('rect', { x: x - 4, y: y - 4, width: w + 8, height: h + 8, fill: '#f7c46a', filter: 'url(#winGlow)', opacity: 0 }, g);
      const pane = el('rect', { x, y, width: w, height: h, rx: 4, fill: '#1c1820' }, g);
      const lit = el('rect', { x, y, width: w, height: h, rx: 4, fill: '#f7cf86', opacity: 0 }, g);
      el('line', { x1: x + w / 2, y1: y, x2: x + w / 2, y2: y + h, stroke: ink, 'stroke-width': 6 }, g);
      el('line', { x1: x, y1: y + h / 2, x2: x + w, y2: y + h / 2, stroke: ink, 'stroke-width': 6 }, g);
      return { glow, lit, set(v) { lit.setAttribute('opacity', (0.92 * v).toFixed(3)); glow.setAttribute('opacity', (0.85 * v).toFixed(3)); } };
    });
    return { windows };
  };
})();
