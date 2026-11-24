// SnapLite extension: pure helpers (no chrome.* calls), so they can be tested with Node.
'use strict';

/** Chrome's canvas limits: 32,767 px per side and ~268 M pixels in total. */
const MAX_CANVAS_SIDE = 32000;
const MAX_CANVAS_AREA = 250_000_000;

/**
 * Size of the stitched image in device pixels.
 * @param {number} cssWidth  page width without the scrollbar (CSS px)
 * @param {number} cssHeight page height to capture (CSS px, already capped at the user's maximum)
 * @param {number} scale     device pixels per CSS pixel, measured from the first screenshot
 */
function canvasSize(cssWidth, cssHeight, scale) {
  const width = Math.max(1, Math.round(cssWidth * scale));
  const wanted = Math.max(1, Math.round(cssHeight * scale));
  const height = Math.min(wanted, MAX_CANVAS_SIDE, Math.floor(MAX_CANVAS_AREA / width));
  return { width, height, truncated: height < wanted };
}

/**
 * Which rows of one screenshot go where in the stitched image.
 * The screenshot shows page rows [scrolledTo, scrolledTo + viewport); we want page rows starting at y.
 * Near the bottom the page can't scroll all the way, so scrolledTo < y and the rows sit lower in the shot.
 * @returns {{srcY:number, destY:number, rows:number}} in device pixels (rows <= 0: nothing to draw)
 */
function sliceFor(y, scrolledTo, viewport, pageHeight, scale, frameHeight, canvasHeight) {
  const endCss = Math.min(y + viewport, pageHeight, scrolledTo + viewport);
  const srcY = Math.max(0, Math.round((y - scrolledTo) * scale));
  const destY = Math.round(y * scale);
  let rows = Math.round(endCss * scale) - destY;
  rows = Math.min(rows, frameHeight - srcY, canvasHeight - destY);
  return { srcY, destY, rows };
}

/**
 * How far to scroll between screenshots (CSS px). A little less than one screen: at zoom levels like
 * 125% a screenshot can be one device pixel shorter than the CSS maths says, and the small overlap
 * makes sure the next screenshot covers that row instead of leaving a blank line.
 */
function scrollStepFor(viewport) {
  return Math.max(1, viewport - 2);
}

/** Top edges of fixed-height pieces for the standalone "steps" fallback. */
function simpleSteps(height, step) {
  const out = [];
  for (let y = 0; y < height; y += step) out.push({ top: y, height: Math.min(step, height - y) });
  return out;
}

/** A file-name-safe version of a page title. */
function safeName(title) {
  const t = String(title || '').replace(/[<>:"/\\|?*\u0000-\u001f]+/g, ' ').replace(/\s+/g, ' ').trim().replace(/[. ]+$/, '');
  return (t.length > 60 ? t.slice(0, 60).trim() : t) || 'Webpage';
}

/** yyyy-MM-dd_HH-mm-ss in local time, like the desktop app. */
function timestamp(d = new Date()) {
  const p = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}_${p(d.getHours())}-${p(d.getMinutes())}-${p(d.getSeconds())}`;
}

/** Base64 of a byte array, in slices so huge images don't overflow the call stack. */
function toBase64(bytes) {
  let s = '';
  for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
  return btoa(s);
}

/** Pages no extension may script or capture. */
function isRestrictedUrl(url) {
  return !url || /^(chrome|edge|brave|about|devtools|view-source|chrome-extension|chrome-search):/i.test(url) ||
    /^https:\/\/(chrome\.google\.com\/webstore|chromewebstore\.google\.com|microsoftedge\.microsoft\.com\/addons)/i.test(url);
}

if (typeof module !== 'undefined') module.exports = { canvasSize, sliceFor, scrollStepFor, simpleSteps, safeName, timestamp, toBase64, isRestrictedUrl };
