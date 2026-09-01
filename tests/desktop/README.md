# Desktop tests — the packaged shell

These drive the **installed `GPD Forge.exe`** through Windows UI Automation. They are the only tests
that touch the artefact a user actually runs.

## Why they exist

Every incident of the week of 2026-08-28 lived in the packaged shell, and the rest of the suite
structurally could not see any of them:

| Incident | Why the suite missed it |
|---|---|
| Installed shell embedded a bundle with a relative asset `BASE`, so every fetch resolved against `http://tauri.localhost` and 404'd — all tiles read `--` | Playwright runs `vite preview` in Chromium; the origin is different and the bundle is a different build |
| `install-gpd-forge.ps1` claimed to build the shell and only copied `target/release` | No test ran the installer |
| A reinstall erased `GPDFORGE_ENABLE_FAN_CONTROL` from the service environment | No test inspected the installed service |

Diagnosing the first one required diffing the installed binary against a fresh build hunting for
marker strings, because nothing on screen could say which build was on screen.

## What UIA can and cannot see

GPD Forge is Tauri, so its content is WebView2. Walking the tree on 2026-08-31 gives:

```
Tauri Window
└── WRY_WEBVIEW
    └── Chrome_WidgetWin_1
        └── BrowserRootView → NonClientView → BrowserView → ... (all names blank)
```

**Not one DOM node is exposed.** So the split is:

- **UIA (here)** — the window layer: it opens, what it is titled, that a webview was mounted, and
  that closing hides to tray rather than exiting.
- **Playwright (`tests/e2e`)** — everything rendered inside the page.

Neither substitutes for the other. A test here claiming to check page content would be asserting
against blanks — which is why the ECC `windows-desktop-e2e` skill routes WebView2 apps to browser
automation for the HTML layer.

## Running them

```powershell
python -m venv .venv-desktop
.venv-desktop\Scripts\pip install -r tests\desktop\requirements.txt
.venv-desktop\Scripts\python -m pytest tests\desktop -v
```

⚠️ On this reference machine the interpreter must be the **winget-signed Python** — Smart App Control
blocks the CPython that `uv` manages (`os error 4551`).

They **skip** when GPD Forge is not installed, so they are inert on a CI runner. That is deliberate
and worth stating plainly: **these do not protect CI.** They are a post-install check, run on the
device, alongside `scripts/verify-install.ps1`.

## Falsified, not assumed

Pointed at `charmap.exe` — a real Win32 window that is not the shell — all four fail with the right
reasons: window class `#32770` instead of `Tauri Window`, no `WRY_WEBVIEW`, no `daemon=` in the
title, and the process dying on close instead of hiding. A guard nobody has watched fail does not
count as a guard.

## Things measured here worth knowing

- **The shell does not enforce single-instance.** A second launch opens a second window against the
  same daemon rather than focusing the first. That is what lets these tests start their own instance
  instead of hijacking the user's, and it is a product behaviour someone should decide on
  deliberately rather than inherit.
- **The window is usually absent from the UIA tree**, because closing it hides to tray and that is
  where it normally sits. A probe that just looks for the window on a running machine will find
  nothing and conclude, wrongly, that the shell is not running.
