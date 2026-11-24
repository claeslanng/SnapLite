# Publishing the extension to the Chrome Web Store

Everything to upload and paste is in this folder. The package is built by `publish.ps1`:
`publish\SnapLite-extension-<version>-store.zip`.

## 1. Developer account (once)

1. Go to <https://chrome.google.com/webstore/devconsole> and sign in with the Google account that
   should own the listing.
2. Accept the developer agreement and pay the one-time **US$5** registration fee.
3. Verify the contact email address (Account tab).

## 2. Upload, then send the item ID back

1. Click **New item** and upload `publish\SnapLite-extension-1.0.0-store.zip`.
2. The dashboard now shows the item's **ID** (32 letters a–p). **Add it to
   `BrowserSetup.StoreExtensionIds`** in `src/SnapLite/Bridge/BrowserSetup.cs` and publish a new
   SnapLite version. Until then, the store version can't talk to the app and falls back to downloads.
   Installed copies pick up the new ID on their next start.

## 3. Store listing tab

| Field | What to enter |
|---|---|
| Description | the text under **Detailed description** below |
| Category | Productivity → Tools |
| Language | English |
| Store icon | `store-icon-128.png` |
| Screenshots | `screenshot-1-steps.png` … `screenshot-4-settings.png` (1280 × 800) |
| Small promo tile | `promo-tile-440x280.png` |
| Homepage URL | `https://github.com/claeslanng/SnapLite` |
| Support URL | `https://github.com/claeslanng/SnapLite/issues` |

The name ("SnapLite") and the short description come from the package's manifest.

### Detailed description

```
Full-page and step-by-step screenshots of the page you're on, including pages you're logged in to.

• Full page: scrolls the tab and stitches one tall image, with sticky headers shown once.
• Page in steps: one numbered image per section, each starting at its heading. Great for tutorials, documentation and support answers.
• Visible part: just what's on screen.
• Clean captures: cookie banners, pop-ups and floating chat buttons are hidden while capturing, then the page is put back exactly as it was.

Works on its own: images are saved to your Downloads folder.

Works best with the free SnapLite app for Windows (github.com/claeslanng/SnapLite): captures go straight to your SnapLite folder and clipboard, with a notification to open or copy them, and steps get a steps.md index. The app also captures regions, windows and your whole screen with global shortcuts.

Private by design: the extension only sees the tab you capture, when you click it or press its shortcut. Nothing is uploaded anywhere.

Shortcuts: Alt+Shift+F (full page), Alt+Shift+S (steps), Alt+Shift+V (visible part); change them at chrome://extensions/shortcuts.
```

## 4. Privacy practices tab

**Single purpose**

```
Capture screenshots of the current tab (the full page, the page split into steps, or the visible part) and save them on the user's computer, through the SnapLite desktop app when it's installed.
```

**Permission justifications**

| Permission | Justification to paste |
|---|---|
| activeTab | Captures and scripts only the tab the user invokes the extension on (toolbar button or keyboard shortcut). No access to other tabs. |
| scripting | Injects the extension's own page scripts into that tab to scroll it, temporarily hide cookie banners and floating buttons, and measure where sections start; the page is restored afterwards. |
| nativeMessaging | Sends the captured image to the SnapLite desktop app on the same computer, which saves it to the user's chosen folder and clipboard. Nothing leaves the computer. |
| downloads | Saves the captured image to the Downloads folder when the SnapLite desktop app isn't installed. |
| storage | Remembers the user's options in the popup (such as "hide cookie banners"). |
| offscreen | Creates a local blob URL so large images can be downloaded (service workers can't create blob URLs). |

**Remote code:** No, I am not using remote code.

**Data usage:** tick none of the data types. Then tick all three certifications: no selling or
transferring of user data, no use unrelated to the single purpose, and no use for
creditworthiness or lending.

**Privacy policy URL:** `https://github.com/claeslanng/SnapLite/blob/main/docs/privacy.md`

## 5. Submit

Click **Submit for review**. Reviews usually take a few days; you'll get an email. Visibility can
be Public or Unlisted (only people with the link).

## Microsoft Edge Add-ons (optional, free)

Same package, same texts: <https://partner.microsoft.com/dashboard/microsoftedge/overview>. Edge
also assigns its own ID: add that one to `StoreExtensionIds` as well.
