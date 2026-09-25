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

> ⚠️ **Known limitation (Win 4 HX370 / G1618-04):** the scripted path B below fails on this unit —
> `gpdconfig` (pyWinControls) errors with `HidD_SetFeature: (0x1) Incorrect function` on the very
> first config request, i.e. this newer firmware's config protocol differs from what pyWinControls
> supports. It also means we can't back up or read the config, so **do not** attempt a scripted write
> here. **Use Option A (GPD's own WinControls / GPD Tool), which speaks this firmware's protocol.**
> Option B remains valid for the GPD models pyWinControls supports.

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

## Step 2 — make the listener resident (installer)

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-gpd-forge.ps1 -EnableHotkeys -OverlayHotkey F24
```

`-OverlayHotkey` takes the key you mapped in Step 1 (a bare key such as `F24`, or a chord such as
`Ctrl+Alt+Home`, which is the default). The installer puts a startup shortcut in `shell:startup`
that runs `overlay-hotkey.ps1 -Modifiers None -Key F24` through `conhost --headless`, so no console
flashes at logon. Pressing **L4** then opens the overlay; pressing it again closes it.

- The chord is validated (`Ctrl`/`Alt`/`Shift`/`Win` plus one key name) and the key is checked
  against the Windows key names before anything is installed, so a typo fails at install time
  rather than silently at logon.
- The same key as R4 or Menu works the same way: map that button to F24 (or F13–F23) instead.
- `-OverlayHotkey` without `-EnableHotkeys` changes nothing and says so. Reinstalling without
  `-EnableHotkeys` removes the startup shortcut, so no listener is left holding the key.
- The TDP and mode chords (`forge-hotkeys.ps1`: `Ctrl+Alt+Up`/`Down`, `Ctrl+Alt+M`) are unchanged.

### Without the installer

```powershell
powershell -ExecutionPolicy Bypass -WindowStyle Hidden -File scripts\overlay-hotkey.ps1 -Modifiers None -Key F24
```

For a keyboard-only test (no paddle), the listener defaults to **Ctrl+Alt+Home**:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\overlay-hotkey.ps1   # Ctrl+Alt+Home toggles the overlay
```

`-SelfTest` registers and unregisters the hotkey. It fails while the resident listener is running,
because the listener already holds the key: that failure means the listener is working.

## Notes / pending

- Over **borderless** games the app-window overlay shows fine; over **exclusive-fullscreen** it
  won't float on top — that needs a native transparent topmost window (code-signed Tauri), tracked
  in `ROADMAP.md`.
- Device confirmed on the Win 4: USB `2f24:0135`, config interface at HID usage page `0xff00`
  (interface `MI_02`).
