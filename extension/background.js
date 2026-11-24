// SnapLite extension service worker: captures the current tab (visible part, full page or steps)
// and hands the image to the SnapLite desktop app through native messaging. Without the app it
// downloads the image(s) instead.
'use strict';
importScripts('lib.js');

const HOST = 'com.snaplite.host';
const DEFAULTS = { cleanup: true, smartSteps: true, useApp: true, maxHeight: 30000 };
const CAPTURE_GAP_MS = 550;      // Chrome allows two captureVisibleTab calls per second
const CHUNK_BYTES = 768 * 1024;  // ~1 MB per native message once base64-encoded
let busy = false;
let lastCaptureAt = 0;

class AppUnavailable extends Error { }

// ---- Entry points ---------------------------------------------------------------------------

chrome.runtime.onMessage.addListener((msg, _sender, reply) => {
  if (msg?.target === 'offscreen') return false;
  if (msg?.cmd === 'capture') {
    const accepted = !busy;
    startCapture(msg.kind, msg.tabId);
    reply({ accepted });
    return false;
  }
  if (msg?.cmd === 'status') {
    appStatus().then(reply);
    return true; // async reply
  }
  return false;
});

chrome.commands.onCommand.addListener((command, tab) => {
  const kind = { 'capture-full': 'full', 'capture-steps': 'steps', 'capture-visible': 'visible' }[command];
  if (kind) startCapture(kind, tab?.id);
});

async function appStatus() {
  try {
    const r = await chrome.runtime.sendNativeMessage(HOST, { type: 'hello' });
    return { connected: true, version: r?.version, running: !!r?.running };
  } catch (e) {
    return { connected: false, error: String(e?.message || e) };
  }
}

// ---- Capture --------------------------------------------------------------------------------

async function startCapture(kind, tabId) {
  if (busy) return;
  busy = true;
  const opts = await chrome.storage.local.get(DEFAULTS);
  try {
    const tab = tabId ? await chrome.tabs.get(tabId) : (await chrome.tabs.query({ active: true, currentWindow: true }))[0];
    if (!tab) throw new Error('There is no tab to capture.');
    if (isRestrictedUrl(tab.url)) throw new Error("The browser doesn't let extensions capture this page.");
    await chrome.storage.session.set({ lastError: null, lastResult: null });
    await badge('…', '#0067c0');

    let shot;
    if (kind === 'visible') {
      shot = await captureVisible(tab);
    } else {
      try {
        shot = await captureFullPage(tab, kind === 'steps', opts);
      } catch (e) {
        if (!(e instanceof NotScriptable) || kind === 'steps') throw e;
        shot = await captureVisible(tab); // e.g. the PDF viewer: the visible part is the best we can do
        shot.note = "This page can't be scrolled by an extension, so only the visible part was saved.";
      }
    }

    if (kind === 'steps' && !shot.layout) throw new Error("Couldn't measure the page's sections for steps.");
    const message = await deliver(shot, kind, opts);
    await chrome.storage.session.set({ lastResult: shot.note ? `${message} ${shot.note}` : message });
    await badge('✓', '#107c10');
    setTimeout(() => badge('', '#000'), 2500);
    chrome.action.setTitle({ title: 'SnapLite' });
  } catch (e) {
    const text = String(e?.message || e);
    console.error('SnapLite capture failed:', e);
    await chrome.storage.session.set({ lastError: text });
    await badge('!', '#c42b1c');
    chrome.action.setTitle({ title: `SnapLite: ${text}` });
  } finally {
    busy = false;
  }
}

class NotScriptable extends Error { }

async function captureTab(windowId) {
  const wait = lastCaptureAt + CAPTURE_GAP_MS - Date.now();
  if (wait > 0) await sleep(wait);
  try {
    lastCaptureAt = Date.now();
    return await chrome.tabs.captureVisibleTab(windowId, { format: 'png' });
  } catch (e) {
    if (!/MAX_CAPTURE_VISIBLE_TAB_CALLS_PER_SECOND/.test(String(e?.message))) throw e;
    await sleep(1000);
    lastCaptureAt = Date.now();
    return await chrome.tabs.captureVisibleTab(windowId, { format: 'png' });
  }
}

async function captureVisible(tab) {
  const dataUrl = await captureTab(tab.windowId);
  const blob = await (await fetch(dataUrl)).blob();
  const bmp = await createImageBitmap(blob);
  const canvas = new OffscreenCanvas(bmp.width, bmp.height);
  canvas.getContext('2d').drawImage(bmp, 0, 0);
  bmp.close();
  return { canvas, blob, title: tab.title, url: tab.url, scale: 1, viewportHeight: canvas.height, truncated: false };
}

/** Scrolls the real tab screen by screen, stitching the screenshots; puts the page back afterwards. */
async function captureFullPage(tab, steps, opts) {
  const target = { tabId: tab.id };
  const run = async (func, ...args) => (await chrome.scripting.executeScript({ target, func, args }))[0]?.result;
  try {
    await chrome.scripting.executeScript({ target, files: ['shared/cleanup.js', 'shared/sections.js'] });
  } catch (e) {
    throw new NotScriptable(String(e?.message || e));
  }

  let info = null;
  try {
    info = await run(preparePage, { cleanup: opts.cleanup, maxHeight: opts.maxHeight, steps, maxStep: 1600 });
    const viewport = info.viewportHeight;
    const step = scrollStepFor(viewport);
    const pageHeight = Math.min(info.height, opts.maxHeight);
    let canvas = null, ctx = null, scale = 1, size = null, filled = 0;

    for (let y = 0; y < pageHeight; y += step) {
      const current = await chrome.tabs.get(tab.id);
      if (!current.active) throw new Error('Stopped: the tab was switched during the capture.');

      const scrolledTo = await run(scrollStep, y, y > 0);
      if (y > 0 && scrolledTo === 0) break; // the page doesn't scroll after all: keep the first screen

      const blob = await (await fetch(await captureTab(tab.windowId))).blob();
      const bmp = await createImageBitmap(blob);
      if (!canvas) {
        scale = bmp.width / info.innerWidth;
        size = canvasSize(info.width, pageHeight, scale);
        canvas = new OffscreenCanvas(size.width, size.height);
        ctx = canvas.getContext('2d');
      }
      const s = sliceFor(y, scrolledTo, viewport, pageHeight, scale, bmp.height, canvas.height);
      if (s.rows > 0) {
        ctx.drawImage(bmp, 0, s.srcY, canvas.width, s.rows, 0, s.destY, canvas.width, s.rows);
        filled = Math.max(filled, s.destY + s.rows);
      }
      bmp.close();
      await badge(`${Math.min(99, Math.round((100 * (y + viewport)) / pageHeight))}%`, '#0067c0');
      if (filled >= canvas.height) break;
    }

    if (filled < canvas.height) { // stopped early: trim the empty bottom
      const trimmed = new OffscreenCanvas(canvas.width, Math.max(1, filled));
      trimmed.getContext('2d').drawImage(canvas, 0, 0);
      canvas = trimmed;
    }
    await badge('…', '#0067c0');
    const blob = await canvas.convertToBlob({ type: 'image/png' });
    return {
      canvas, blob, title: info.title, url: info.url, scale, viewportHeight: viewport,
      truncated: size.truncated || info.height > opts.maxHeight, layout: info.layout,
    };
  } finally {
    await run(restorePage, info?.startX ?? 0, info?.startY ?? 0).catch(() => { });
  }
}

// These run inside the page (isolated world), where cleanup.js/sections.js defined window.__snaplite.

async function preparePage(o) {
  const S = window.__snaplite;
  const startX = window.scrollX, startY = window.scrollY;
  S.disableAnimations();
  if (o.cleanup) S.hideBanners();
  await S.autoScroll(o.maxHeight, 15000); // load lazy images and sections
  await S.waitForImages(5000);
  if (o.cleanup) { S.hideBanners(); S.fixFloating(); }
  S.expandInnerScroller();
  window.scrollTo(0, 0);
  await new Promise((r) => setTimeout(r, 300));
  const se = document.scrollingElement || document.documentElement;
  const info = {
    title: document.title || location.hostname,
    url: location.href,
    width: document.documentElement.clientWidth,  // without the scrollbar
    innerWidth: window.innerWidth,                  // what the screenshot covers
    viewportHeight: document.documentElement.clientHeight,
    height: Math.max(se.scrollHeight, document.body ? document.body.scrollHeight : 0),
    startX, startY,
  };
  if (o.steps) info.layout = S.measureLayout(o.maxStep);
  return info;
}

async function scrollStep(y, hideFixed) {
  const frame = () => Promise.race([
    new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r))),
    new Promise((r) => setTimeout(r, 250)),
  ]);
  window.scrollTo(0, y);
  await frame();
  // After the first screen, fixed headers are hidden and sticky ones put back in place, so they appear once.
  if (hideFixed && window.scrollY > 0) { window.__snaplite.fixedAfterFirstScreen(); await frame(); }
  await new Promise((r) => setTimeout(r, 80));
  return Math.round(window.scrollY);
}

function restorePage(x, y) {
  window.__snaplite?.restore();
  window.scrollTo(x, y);
  return true;
}

// ---- Delivery -------------------------------------------------------------------------------

async function deliver(shot, kind, opts) {
  if (opts.useApp) {
    try {
      const r = await sendToApp(shot, kind, opts);
      return kind === 'steps' ? `Saved ${r.count} steps with SnapLite.` : 'Saved with SnapLite.';
    } catch (e) {
      if (!(e instanceof AppUnavailable)) throw e;
      console.info('SnapLite app not available, downloading instead:', e.message);
    }
  }
  return download(shot, kind);
}

/** Streams the PNG to SnapLite.exe (native messaging host), which saves it like a desktop capture. */
async function sendToApp(shot, kind, opts) {
  let port;
  try {
    port = chrome.runtime.connectNative(HOST);
  } catch (e) {
    throw new AppUnavailable(String(e?.message || e));
  }
  const id = crypto.randomUUID();
  const waitFor = (type, ms) => new Promise((resolve, reject) => {
    const timer = setTimeout(() => { cleanup(); reject(new Error('SnapLite took too long to answer.')); }, ms);
    const onMsg = (m) => {
      if (m?.id !== id) return;
      if (m.type === type) { cleanup(); resolve(m); }
      else if (m.type === 'done' && !m.ok) { cleanup(); reject(new Error(m.error || 'SnapLite could not save it.')); }
    };
    const onGone = () => {
      cleanup();
      const why = chrome.runtime.lastError?.message || 'SnapLite closed the connection.';
      reject(type === 'ready' ? new AppUnavailable(why) : new Error(why));
    };
    const cleanup = () => { clearTimeout(timer); port.onMessage.removeListener(onMsg); port.onDisconnect.removeListener(onGone); };
    port.onMessage.addListener(onMsg);
    port.onDisconnect.addListener(onGone);
  });

  try {
    const ready = waitFor('ready', 10000);
    port.postMessage({
      type: 'begin', id,
      meta: {
        kind: kind === 'steps' ? 'steps' : 'image',
        title: shot.title, url: shot.url, scale: shot.scale, viewportHeight: shot.viewportHeight,
        truncated: shot.truncated, smart: opts.smartSteps, layout: kind === 'steps' ? shot.layout : undefined,
      },
    });
    await ready;
    const bytes = new Uint8Array(await shot.blob.arrayBuffer());
    const done = waitFor('done', 180000);
    for (let i = 0; i < bytes.length; i += CHUNK_BYTES) {
      port.postMessage({ type: 'chunk', id, data: toBase64(bytes.subarray(i, i + CHUNK_BYTES)) });
    }
    port.postMessage({ type: 'end', id });
    return await done;
  } finally {
    try { port.disconnect(); } catch { }
  }
}

/** Without the app: download into Downloads\SnapLite (steps: one image per screen, in a subfolder). */
async function download(shot, kind) {
  const name = safeName(shot.title);
  const stamp = timestamp();
  if (kind !== 'steps') {
    await downloadBlob(shot.blob, `SnapLite/${name}_${stamp}.png`);
    return 'Downloaded to your Downloads\\SnapLite folder.';
  }
  const step = Math.max(1, Math.round(shot.viewportHeight * shot.scale));
  const pieces = simpleSteps(shot.canvas.height, step);
  const digits = Math.max(2, String(pieces.length).length);
  for (let i = 0; i < pieces.length; i++) {
    const p = pieces[i];
    const c = new OffscreenCanvas(shot.canvas.width, p.height);
    c.getContext('2d').drawImage(shot.canvas, 0, p.top, shot.canvas.width, p.height, 0, 0, shot.canvas.width, p.height);
    const blob = await c.convertToBlob({ type: 'image/png' });
    await downloadBlob(blob, `SnapLite/${name}_${stamp}/${name}_${String(i + 1).padStart(digits, '0')}.png`);
  }
  return `Downloaded ${pieces.length} steps (one per screen; splitting at sections needs the SnapLite app).`;
}

/**
 * chrome.downloads needs a URL. Data URLs over 2 MB don't make it through, and service workers
 * can't make blob URLs, so a hidden offscreen page turns the bytes into a blob URL.
 */
async function downloadBlob(blob, filename) {
  await ensureOffscreen();
  const base64 = toBase64(new Uint8Array(await blob.arrayBuffer()));
  const { url } = await chrome.runtime.sendMessage({ target: 'offscreen', cmd: 'makeUrl', base64, type: blob.type });
  try {
    const id = await chrome.downloads.download({ url, filename, conflictAction: 'uniquify' });
    await new Promise((resolve) => {
      const onChanged = (d) => {
        if (d.id === id && d.state && d.state.current !== 'in_progress') { chrome.downloads.onChanged.removeListener(onChanged); resolve(); }
      };
      chrome.downloads.onChanged.addListener(onChanged);
    });
  } finally {
    await chrome.runtime.sendMessage({ target: 'offscreen', cmd: 'revoke', url }).catch(() => { });
  }
}

async function ensureOffscreen() {
  const existing = await chrome.runtime.getContexts({ contextTypes: ['OFFSCREEN_DOCUMENT'] });
  if (existing.length) return;
  await chrome.offscreen.createDocument({
    url: 'offscreen.html', reasons: ['BLOBS'], justification: 'Turn the captured image into a file to download.',
  });
}

// ---- Helpers --------------------------------------------------------------------------------

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function badge(text, color) {
  try {
    await chrome.action.setBadgeBackgroundColor({ color });
    await chrome.action.setBadgeText({ text });
  } catch { /* the badge is only decoration */ }
}
