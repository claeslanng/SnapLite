'use strict';

const DEFAULTS = { cleanup: true, smartSteps: true, useApp: true };
const $ = (id) => document.getElementById(id);

(async () => {
  // Options
  const opts = await chrome.storage.local.get(DEFAULTS);
  for (const key of Object.keys(DEFAULTS)) {
    $(key).checked = !!opts[key];
    $(key).addEventListener('change', () => chrome.storage.local.set({ [key]: $(key).checked }));
  }

  // Shortcuts as actually set (the user can change them at chrome://extensions/shortcuts)
  const commands = await chrome.commands.getAll();
  for (const kbd of document.querySelectorAll('kbd[data-command]')) {
    kbd.textContent = commands.find((c) => c.name === kbd.dataset.command)?.shortcut || '';
  }

  // Buttons
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  for (const button of document.querySelectorAll('button[data-kind]')) {
    button.addEventListener('click', async () => {
      await chrome.runtime.sendMessage({ cmd: 'capture', kind: button.dataset.kind, tabId: tab?.id });
      window.close(); // progress shows on the toolbar icon
    });
  }

  // Last result / error from this browser session
  const { lastError, lastResult } = await chrome.storage.session.get(['lastError', 'lastResult']);
  if (lastError) { $('last').className = 'bad'; $('last').textContent = `Last capture failed: ${lastError}`; }
  else if (lastResult) { $('last').textContent = lastResult; }

  // Is the desktop app reachable?
  const s = await chrome.runtime.sendMessage({ cmd: 'status' });
  const app = $('app');
  if (s?.connected) {
    app.innerHTML = `<b class="ok">● Connected to SnapLite ${s.version || ''}</b><br>` +
      (s.running ? 'Captures are saved to your SnapLite folder.' : 'SnapLite will start when you capture.');
  } else {
    app.innerHTML = '<b class="warn">○ SnapLite app not connected</b><br>' +
      'Captures are downloaded instead. To connect: SnapLite → Settings → Browser → Set up.';
    $('useApp').closest('label').title = s?.error || '';
  }
})();
