// Hidden page that turns image bytes into blob: URLs for chrome.downloads (service workers can't).
'use strict';

chrome.runtime.onMessage.addListener((msg, _sender, reply) => {
  if (msg?.target !== 'offscreen') return false;
  if (msg.cmd === 'makeUrl') {
    const bin = atob(msg.base64);
    const bytes = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
    reply({ url: URL.createObjectURL(new Blob([bytes], { type: msg.type || 'image/png' })) });
  } else if (msg.cmd === 'revoke') {
    URL.revokeObjectURL(msg.url);
    reply({ ok: true });
  }
  return false;
});
