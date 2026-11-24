// Tests for extension/lib.js.  Run:  node --test tests/extension
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { canvasSize, sliceFor, scrollStepFor, simpleSteps, safeName, timestamp, toBase64, isRestrictedUrl } = require('../../extension/lib.js');

/**
 * Simulates a capture the way background.js does it; returns the device-pixel rows each frame fills.
 * frameShortBy: the real screenshot can be a device pixel shorter than viewport*scale.
 */
function simulate(pageHeight, viewport, scale, frameShortBy = 1) {
  const size = canvasSize(1000, pageHeight, scale);
  const frameHeight = Math.floor(viewport * scale) - frameShortBy + 1;
  const maxScroll = pageHeight - viewport;
  const fills = [];
  for (let y = 0; y < pageHeight; y += scrollStepFor(viewport)) {
    const scrolledTo = Math.min(y, maxScroll);
    const s = sliceFor(y, scrolledTo, viewport, pageHeight, scale, frameHeight, size.height);
    if (s.rows > 0) fills.push(s);
    if (s.destY + s.rows >= size.height) break;
  }
  return { size, fills, frameHeight, maxScroll };
}

test('frames cover the whole image with no blank rows (any zoom, screenshots a pixel short)', () => {
  for (const scale of [1, 1.1, 1.25, 1.5, 1.75, 2, 0.9])
    for (const short of [0, 1])
      for (const [pageHeight, viewport] of [[5000, 900], [901, 900], [900, 900], [12345, 777], [2000, 1003], [30000, 845]]) {
        const { size, fills } = simulate(pageHeight, viewport, scale, short);
        const where = `(scale ${scale}, page ${pageHeight}, vp ${viewport}, short ${short})`;
        let covered = 0;
        for (const f of fills) {
          assert.ok(f.destY <= covered, `blank rows ${covered}-${f.destY} ${where}`);
          assert.ok(f.destY >= covered - 4 * scale, `needless overlap at ${f.destY} ${where}`);
          covered = Math.max(covered, f.destY + f.rows);
        }
        // background.js trims the image to the rows filled; a short last screenshot may cost the final row.
        assert.ok(covered >= size.height - short, `image not filled: ${covered} of ${size.height} ${where}`);
      }
});

test('the last frame takes its rows from lower in the screenshot when the page cannot scroll further', () => {
  // page 2000, viewport 900: frames at 0, 900, then 1800 can only scroll to 1100 -> rows 700..900 of the shot
  const s = sliceFor(1800, 1100, 900, 2000, 1, 900, 2000);
  assert.deepEqual(s, { srcY: 700, destY: 1800, rows: 200 });
  const hi = sliceFor(1800, 1100, 900, 2000, 2, 1800, 4000);
  assert.deepEqual(hi, { srcY: 1400, destY: 3600, rows: 400 });
});

test('page pixels map to the right screenshot pixels', () => {
  // Page row r ends up at canvas row round(r*scale); the screenshot shows it at round((r - scrolledTo)*scale).
  const scale = 1.25, viewport = 800, pageHeight = 3000;
  const s = sliceFor(800, 800, viewport, pageHeight, scale, 1000, canvasSize(100, pageHeight, scale).height);
  assert.equal(s.srcY, 0);
  assert.equal(s.destY, 1000);
  assert.equal(s.rows, 1000);
});

test('canvas is capped by the browser limits and says so', () => {
  assert.deepEqual(canvasSize(1280, 10000, 1), { width: 1280, height: 10000, truncated: false });
  const tall = canvasSize(1280, 30000, 1.5);
  assert.equal(tall.width, 1920);
  assert.equal(tall.height, 32000);
  assert.equal(tall.truncated, true);
  const wide = canvasSize(20000, 20000, 1);
  assert.ok(wide.width * wide.height <= 250_000_000);
  assert.equal(wide.truncated, true);
});

test('simple steps cover the image exactly', () => {
  assert.deepEqual(simpleSteps(2500, 1000), [{ top: 0, height: 1000 }, { top: 1000, height: 1000 }, { top: 2000, height: 500 }]);
  assert.deepEqual(simpleSteps(1000, 1000), [{ top: 0, height: 1000 }]);
});

test('file names are safe and readable', () => {
  assert.equal(safeName('Getting Started: Install & Configure | Example Docs'), 'Getting Started Install & Configure Example Docs');
  assert.equal(safeName('  ...  '), 'Webpage');
  assert.equal(safeName(''), 'Webpage');
  assert.equal(safeName('a'.repeat(100)).length, 60);
  assert.equal(safeName('trailing dots...'), 'trailing dots');
  assert.match(timestamp(new Date(2026, 10, 24, 9, 5, 7)), /^2026-11-24_09-05-07$/);
});

test('base64 handles large arrays', () => {
  const bytes = new Uint8Array(300000).map((_, i) => i % 256);
  assert.equal(toBase64(bytes), Buffer.from(bytes).toString('base64'));
});

test('restricted pages are recognised', () => {
  for (const url of ['chrome://extensions', 'edge://settings', 'https://chromewebstore.google.com/detail/x', 'about:blank', undefined])
    assert.equal(isRestrictedUrl(url), true, url);
  for (const url of ['https://example.com/guide/', 'http://localhost:3000', 'file:///C:/x.html'])
    assert.equal(isRestrictedUrl(url), false, url);
});
