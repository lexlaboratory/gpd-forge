# Binding the overlay to a "Home" button (GPD Win 4)

The GPD Win 4 has **no dedicated Home button** (MotionAssistant opens its overlay with `Alt+R`,
GPD Tool with `Ctrl+Shift+F3` — keyboard chords). GPD Forge instead lets you pick a **physical
button**: we remap a back paddle (L4/R4) or Menu to a rare key, and a resident listener catches
that key to toggle the Quick Access Menu (`/overlay.html`).

Everything here runs under **Smart App Control**: `powershell.exe`, `python.exe`, `msedge`/`chrome`
are all code-signed. Our own Tauri binary is not (yet), which is why the overlay opens as a signed
browser app-window rather than a native transparent window (see `ROADMAP.md`).

## The chain

```
L4 paddle ──(firmware map, WinControls)──▶ F24 key ──(RegisterHotKey)──▶ overlay-hotkey.ps1
                                                                              │ toggles
                                                                              ▼
                                                          overlay-launch.ps1 → /overlay.html
```

We map the paddle to a **single unused key (F24)** — a single keycode fires a global hotkey
reliably, unlike a modifier chord routed through the paddle's macro slots.

## Step 1 — map a paddle to F24

**Option A — GPD's official WinControls app (no install, safest):** open WinControls, set the
**L4** back button to **F24**, apply. Done.

**Option B — scripted, with automatic backup (our wrapper around the proven `gpdconfig`):**

```powershell
# one-time: install the (signed-python) tool
powershell -ExecutionPolicy Bypass -File scripts\gpd-winctl.ps1 -Setup

# back up the current controller config, then map L4 -> F24, then read back to confirm
powershell -ExecutionPolicy Bypass -File scripts\gpd-winctl.ps1 -MapHome
```

`-MapHome` always writes a full backup to `%LOCALAPPDATA%\GPDForge\controller-backups\` **before**
touching the firmware, and reads the config back afterwards. To undo:
`scripts\gpd-winctl.ps1 -Restore <that-backup.txt>` (or `gpdconfig -r` to reset to defaults).

> The controller config write is reversible but it *is* firmware — the backup is taken first by design.

## Step 2 — run the resident listener

```powershell
powershell -ExecutionPolicy Bypass -WindowStyle Hidden -File scripts\overlay-hotkey.ps1 -Modifiers "" -Key F24
```

Now pressing **L4** opens the overlay; pressing it again closes it. To auto-start it, drop a shortcut
to that command in `shell:startup`.

For a keyboard-only test (no paddle), the listener defaults to **Ctrl+Alt+Home**:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\overlay-hotkey.ps1   # Ctrl+Alt+Home toggles the overlay
```

## Notes / pending

- Over **borderless** games the app-window overlay shows fine; over **exclusive-fullscreen** it
  won't float on top — that needs a native transparent topmost window (code-signed Tauri), tracked
  in `ROADMAP.md`.
- Device confirmed on the Win 4: USB `2f24:0135`, config interface at HID usage page `0xff00`
  (interface `MI_02`).
