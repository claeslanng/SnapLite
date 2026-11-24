// SnapLite page helpers.
// Shared by the desktop app (injected into WebView2 via CDP) and the Chrome extension.
// Safe to inject more than once. Everything hangs off window.__snaplite.
(() => {
  if (window.__snaplite) return;

  const HIDDEN = 'data-snaplite-hidden';
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const scroller = () => document.scrollingElement || document.documentElement;

  function addStyle(id, css) {
    let s = document.getElementById(id);
    if (!s) {
      s = document.createElement('style');
      s.id = id;
      (document.head || document.documentElement).appendChild(s);
    }
    s.textContent = css;
  }
  addStyle('snaplite-base', `[${HIDDEN}]{display:none!important}`);

  const hide = (el) => el.setAttribute(HIDDEN, '');

  // Every inline style change is recorded so restore() can put the page back exactly as it was
  // (the extension works on the user's real tab; the desktop app throws its page away).
  const undo = [];
  function setStyle(el, prop, value) {
    undo.push([el, prop, el.style.getPropertyValue(prop), el.style.getPropertyPriority(prop)]);
    el.style.setProperty(prop, value, 'important');
  }
  const pausedVideos = [];

  /** All position:fixed / position:sticky elements, with their computed style and rect. */
  function fixedElements() {
    const out = [];
    const walker = document.createTreeWalker(document.body || document.documentElement, NodeFilter.SHOW_ELEMENT);
    for (let el = walker.currentNode; el; el = walker.nextNode()) {
      if (el.hasAttribute && el.hasAttribute(HIDDEN)) continue;
      const cs = getComputedStyle(el);
      if (cs.position === 'fixed' || cs.position === 'sticky') {
        const r = el.getBoundingClientRect();
        if (r.width > 0 && r.height > 0 && cs.display !== 'none' && cs.visibility !== 'hidden') {
          out.push({ el, cs, r });
        }
      }
    }
    return out;
  }

  // Well-known consent managers and cookie bars.
  const BANNER_SELECTORS = [
    '#onetrust-consent-sdk', '#onetrust-banner-sdk', '.onetrust-pc-dark-filter',
    '#CybotCookiebotDialog', '#CybotCookiebotDialogBodyUnderlay',
    '.fc-consent-root', '#qc-cmp2-container', '.qc-cmp2-container',
    '#didomi-host', '#didomi-popup', '#usercentrics-root', '#usercentrics-cmp-ui',
    '#truste-consent-track', '.truste_box_overlay', '.truste_overlay',
    '#cmpbox', '#cmpbox2', '#cmpwrapper', '.cmpboxBG',
    '[id^="sp_message_container"]', '.sp_veil',
    '#gdpr-consent-tool-wrapper', '#iubenda-cs-banner', '.iubenda-cs-container',
    '.cc-window', '.cc-banner', '.cookie-consent', '.cookie-banner', '#cookie-banner',
    '#cookie-notice', '.cookie-notice', '#cookieConsent', '#cookie-law-info-bar',
    '.osano-cm-window', '#termly-code-snippet-support', '.termly-styles-root',
    '#hs-eu-cookie-confirmation', '#BorlabsCookieBox', '.borlabs-cookie',
    '#moove_gdpr_cookie_info_bar', '.cky-consent-container', '.cky-overlay',
    '#axeptio_overlay', '.klaro', '#klaro', '.cookiefirst-root',
  ];
  const CONSENT_TEXT = /\b(cookies?|consent|gdpr|we value your privacy|privacy (settings|preferences)|accept all|reject all|manage (preferences|options))\b/i;
  const OVERLAY_NAME = /(modal|popup|pop-up|overlay|newsletter|subscribe|backdrop|lightbox|interstitial|paywall|signup|sign-up)/i;

  /** Removes the scroll lock modals put on <html>/<body>. */
  function unlockScroll() {
    for (const el of [document.documentElement, document.body]) {
      if (!el) continue;
      const cs = getComputedStyle(el);
      if (cs.overflowY === 'hidden' || cs.overflow === 'hidden') setStyle(el, 'overflow', 'visible');
      if (el === document.body && cs.position === 'fixed') {
        setStyle(el, 'position', 'static');
        setStyle(el, 'top', 'auto');
      }
    }
  }

  /** Hides cookie banners and obvious pop-up overlays. Returns how many elements were hidden. */
  function hideBanners() {
    let n = 0;
    for (const sel of BANNER_SELECTORS) {
      try { document.querySelectorAll(sel).forEach((el) => { hide(el); n++; }); } catch { /* bad selector in old engines */ }
    }
    const bodyText = (document.body && document.body.innerText.length) || 1;
    const vpArea = innerWidth * innerHeight;
    for (const { el, cs, r } of fixedElements()) {
      if (cs.position !== 'fixed') continue;
      const text = el.innerText || '';
      // Small-ish fixed boxes that talk about cookies/consent.
      if (text.length < 3000 && CONSENT_TEXT.test(text)) { hide(el); n++; continue; }
      // Large fixed overlays that look like modals. Never hide something holding most of the page's text.
      const name = `${el.id} ${el.className && el.className.baseVal !== undefined ? el.className.baseVal : el.className} ${el.getAttribute('role') || ''}`;
      const looksModal = el.getAttribute('aria-modal') === 'true' || el.getAttribute('role') === 'dialog' || OVERLAY_NAME.test(name);
      if (looksModal && (r.width * r.height) / vpArea > 0.3 && text.length < bodyText * 0.5 && !el.querySelector('main, article')) {
        hide(el); n++;
      }
    }
    unlockScroll();
    return n;
  }

  /**
   * For a single full-page shot (CDP captureBeyondViewport): top headers render once at the top,
   * which is fine; bottom bars, chat bubbles and "back to top" buttons would float in the middle
   * of the image, so hide them.
   */
  function fixFloating() {
    let n = 0;
    for (const { el, cs, r } of fixedElements()) {
      if (cs.position !== 'fixed') continue;
      const bottomAnchored = r.top >= innerHeight * 0.5;
      const smallFloater = r.width < 260 && r.height < 260 && r.top > 80;
      if (bottomAnchored || smallFloater) { hide(el); n++; }
    }
    return n;
  }

  /**
   * For scroll-and-stitch capture: call after the first screen so headers don't repeat.
   * Fixed elements are hidden; sticky ones go back into normal flow.
   */
  function fixedAfterFirstScreen() {
    let n = 0;
    for (const { el, cs } of fixedElements()) {
      if (cs.position === 'fixed') hide(el);
      else setStyle(el, 'position', 'relative');
      n++;
    }
    return n;
  }

  function disableAnimations() {
    addStyle('snaplite-noanim',
      '*,*::before,*::after{animation-duration:0s!important;animation-delay:0s!important;' +
      'transition:none!important;scroll-behavior:auto!important;caret-color:transparent!important}');
    try { document.getAnimations().forEach((a) => { try { a.finish(); } catch { a.cancel(); } }); } catch { /* old engine */ }
    document.querySelectorAll('video').forEach((v) => { try { if (!v.paused) { v.pause(); pausedVideos.push(v); } } catch { } });
    return true;
  }

  /** Scrolls to the bottom (or maxHeight) once to trigger lazy loading, then back to the top. */
  async function autoScroll(maxHeight = 30000, timeoutMs = 20000) {
    const se = scroller();
    document.querySelectorAll('img[loading="lazy"]').forEach((i) => { i.loading = 'eager'; });
    const start = performance.now();
    let y = 0, stable = 0, lastH = -1;
    while (performance.now() - start < timeoutMs) {
      y = Math.min(y + Math.max(200, Math.floor(innerHeight * 0.8)), se.scrollHeight);
      window.scrollTo(0, y);
      await sleep(120);
      if (y >= maxHeight) break;
      if (y + innerHeight >= se.scrollHeight - 2) {
        // At the bottom: wait a little for infinite-scroll / lazy sections to extend the page.
        if (se.scrollHeight === lastH) { if (++stable >= 3) break; } else stable = 0;
        lastH = se.scrollHeight;
        await sleep(250);
      }
    }
    window.scrollTo(0, 0);
    await sleep(200);
    return { height: se.scrollHeight, ms: Math.round(performance.now() - start) };
  }

  /** Resolves when images and web fonts are loaded (or after timeoutMs). */
  async function waitForImages(timeoutMs = 6000) {
    const pending = [...document.images].filter((i) => !i.complete && i.src)
      .map((i) => new Promise((r) => { i.addEventListener('load', r, { once: true }); i.addEventListener('error', r, { once: true }); }));
    const fonts = document.fonts ? document.fonts.ready : Promise.resolve();
    await Promise.race([Promise.all([...pending, fonts]), sleep(timeoutMs)]);
    return pending.length;
  }

  /**
   * Some sites scroll an inner <div> instead of the document, so the "page" is one screen tall.
   * Let that container grow to its full height so the whole page can be captured.
   */
  function expandInnerScroller() {
    const se = scroller();
    if (se.scrollHeight > innerHeight + 5) return false;
    let best = null, bestH = 0;
    for (const el of document.querySelectorAll('body *')) {
      if (el.scrollHeight <= el.clientHeight + 50 || el.clientWidth < innerWidth * 0.5) continue;
      const oy = getComputedStyle(el).overflowY;
      if ((oy === 'auto' || oy === 'scroll') && el.scrollHeight > bestH) { best = el; bestH = el.scrollHeight; }
    }
    if (!best) return false;
    for (let el = best; el && el !== document.documentElement; el = el.parentElement) {
      setStyle(el, 'height', 'auto');
      setStyle(el, 'max-height', 'none');
      setStyle(el, 'overflow', 'visible');
    }
    setStyle(document.documentElement, 'height', 'auto');
    return true;
  }

  function pageInfo() {
    const se = scroller();
    return { title: document.title || location.hostname, url: location.href, width: innerWidth, height: se.scrollHeight };
  }

  /** Undoes everything above: hidden elements, inline styles, added style sheets, paused videos. */
  function restore() {
    for (let i = undo.length - 1; i >= 0; i--) {
      const [el, prop, value, priority] = undo[i];
      if (value) el.style.setProperty(prop, value, priority); else el.style.removeProperty(prop);
    }
    undo.length = 0;
    document.querySelectorAll(`[${HIDDEN}]`).forEach((el) => el.removeAttribute(HIDDEN));
    for (const id of ['snaplite-base', 'snaplite-noanim']) document.getElementById(id)?.remove();
    pausedVideos.forEach((v) => { try { v.play()?.catch?.(() => { }); } catch { } });
    pausedVideos.length = 0;
    delete window.__snaplite; // injecting again starts fresh
    return true;
  }

  window.__snaplite = {
    hideBanners, fixFloating, fixedAfterFirstScreen, disableAnimations,
    autoScroll, waitForImages, expandInnerScroller, unlockScroll, pageInfo, restore,
  };
})();
