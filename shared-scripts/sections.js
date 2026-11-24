// SnapLite page-structure measurement for "Steps" mode.
// Shared by the desktop app and the Chrome extension. Load cleanup.js first.
// Only *measures*; deciding where to cut happens in SnapLite.Core.StepSplitter (unit-tested C#).
(() => {
  if (!window.__snaplite || window.__snaplite.measureLayout) return;

  const KIND = { section: 0, h1: 1, h2: 2, h3: 3 }; // matches BoundaryKind in C#
  const SECTION_SELECTOR = [
    'header', 'footer', 'section', 'article', 'main', 'aside',
    'main > *', '[role="main"] > *', '[role="region"]', '[role="banner"]', '[role="contentinfo"]',
    'body > *', 'body > * > *', '[class*="section" i]',
  ].join(',');
  const REPLACED = 'img,svg,video,canvas,iframe,picture,embed,object,input,button,select,textarea';
  const LIMIT = 60000; // cap work on enormous pages

  /**
   * Heading text with a space between separately styled parts, so a kicker like
   * <span>Las Vegas GP</span><span>Verstappen wins</span> reads "Las Vegas GP · Verstappen wins".
   */
  function headingText(el) {
    const parts = [];
    const w = document.createTreeWalker(el, NodeFilter.SHOW_TEXT);
    for (let t = w.nextNode(); t; t = w.nextNode()) {
      const s = t.nodeValue.replace(/\s+/g, ' ').trim();
      const p = t.parentElement;
      if (!s || !p || getComputedStyle(p).display === 'none') continue;
      const r = p.getBoundingClientRect();
      if (r.width <= 2 || r.height <= 2) continue; // screen-reader-only text ("visually hidden")
      parts.push(s);
    }
    let text = parts.join(' ').replace(/\s+([,.;:!?])/g, '$1');
    if (text.length > 90) text = text.slice(0, 89).trimEnd() + '…';
    return text;
  }

  /**
   * Returns everything StepSplitter needs, in page pixels (scroll offset added):
   *  height, width,
   *  boundaries: [[y, kind], ...]        section edges and h1–h3 tops
   *  headings:   [[y, level, text], ...]
   *  zones:      [[top, bottom], ...]    text lines, images, form controls, background-image boxes
   */
  function measureLayout(maxBackgroundZone = 1600) {
    const se = document.scrollingElement || document.documentElement;
    const sy = window.scrollY;
    const W = window.innerWidth;
    const H = Math.max(se.scrollHeight, document.body ? document.body.scrollHeight : 0);
    const styleCache = new WeakMap();
    const style = (el) => { let s = styleCache.get(el); if (!s) { s = getComputedStyle(el); styleCache.set(el, s); } return s; };
    const shown = (el) => { const s = style(el); return s.display !== 'none' && s.visibility !== 'hidden' && s.position !== 'fixed'; };
    const top = (r) => Math.round(r.top + sy);
    const bottom = (r) => Math.round(r.bottom + sy);

    const boundaries = [];
    const headings = [];
    const zones = [];

    // Structural boundaries: wide, reasonably tall blocks.
    let n = 0;
    for (const el of document.querySelectorAll(SECTION_SELECTOR)) {
      if (++n > LIMIT) break;
      if (!shown(el)) continue;
      const r = el.getBoundingClientRect();
      if (r.width < W * 0.6 || r.height < 60) continue;
      boundaries.push([top(r), KIND.section], [bottom(r), KIND.section]);
    }

    // Headings.
    for (const el of document.querySelectorAll('h1,h2,h3,[role="heading"][aria-level]')) {
      const level = /^H[1-3]$/.test(el.tagName) ? Number(el.tagName[1]) : Number(el.getAttribute('aria-level'));
      if (!(level >= 1 && level <= 3) || !shown(el)) continue;
      const r = el.getBoundingClientRect();
      if (r.height <= 0 || r.width <= 0) continue;
      const text = headingText(el);
      boundaries.push([top(r), KIND['h' + level]]);
      if (text) headings.push([top(r), level, text]);
    }

    // Text lines: one rect per rendered line of every visible text node.
    const range = document.createRange();
    const walker = document.createTreeWalker(document.body || document.documentElement, NodeFilter.SHOW_TEXT);
    n = 0;
    for (let t = walker.nextNode(); t && n < LIMIT; t = walker.nextNode()) {
      if (!/\S/.test(t.nodeValue) || !t.parentElement || !shown(t.parentElement)) continue;
      range.selectNodeContents(t);
      for (const r of range.getClientRects()) {
        if (r.height > 0 && r.width > 0) { zones.push([top(r), bottom(r)]); n++; }
      }
    }

    // Images, video, embeds and form controls.
    for (const el of document.querySelectorAll(REPLACED)) {
      const r = el.getBoundingClientRect();
      if (r.height > 0 && r.width > 0 && shown(el)) zones.push([top(r), bottom(r)]);
    }

    // Boxes with a background image (hero banners, cards) that are small enough to keep whole.
    n = 0;
    for (const el of document.querySelectorAll('body *')) {
      if (++n > LIMIT) break;
      const s = style(el);
      if (s.backgroundImage === 'none' || !s.backgroundImage.includes('url(') || s.display === 'none') continue;
      const r = el.getBoundingClientRect();
      if (r.width >= 100 && r.height > 0 && r.height <= maxBackgroundZone) zones.push([top(r), bottom(r)]);
    }

    return { height: H, width: W, boundaries, headings, zones };
  }

  window.__snaplite.measureLayout = measureLayout;
})();
