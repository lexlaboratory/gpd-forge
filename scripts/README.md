# scripts/

## Install (recommended)

`install-gpd-forge.ps1` installs GPD Forge and sets it to run automatically. It self-elevates, then:
- publishes the core service and registers it as a **Windows Service** (SYSTEM, autostart) so the local
  API + real telemetry come up at boot,
- installs the **desktop app** (runs the built NSIS setup if present, else drops a Start-Menu shortcut),
- starts the service and verifies the API.

```
powershell -ExecutionPolicy Bypass -File scripts\install-gpd-forge.ps1
```
Flags: `-Substitute` also stops + disables MotionAssistant / GPD Tool (the takeover — two power
controllers must not run together); `-Restore` **undoes that takeover**; `-DryRun` rehearses the
restore and writes nothing; `-EnableGpuProfiles` lets GPD Forge set the Radeon 3D settings;
`-EnableHotkeys` registers the resident global hotkeys at logon; `-NoHardware` installs telemetry in
driverless WMI mode only; `-Uninstall` removes everything (and runs the restore first). TDP/fan
writes stay gated regardless.

### Resident helpers

Two things must run in YOUR session rather than in the service, and not for convenience: a Windows
service in session 0 cannot register a user-session hotkey, and cannot reach ADLX at all.

- **GPU agent** (`-EnableGpuProfiles`) — applies the Radeon profile for the active mode.
- **Hotkeys** (`-EnableHotkeys`) — `Ctrl+Alt+Home` toggles the overlay; `Ctrl+Alt+Up`/`Down` step TDP;
  `Ctrl+Alt+M` cycles mode. Opt-in because a global hotkey is a claim on chords the whole machine
  shares. Test either without going resident with `-SelfTest`.

Both are hosted by the Microsoft-signed `powershell.exe` or by `dotnet.exe` running an assembly
Windows already accepted, so Smart App Control has no unsigned binary of ours to refuse.

### Undoing the takeover

`-Substitute` renames the incumbents' `Run` keys, disables `GPDToolService` and disables their
scheduled tasks. Until 2026-08-29 there was no way to reverse any of it, and `-Uninstall` removed
GPD Forge while leaving it all disabled — leaving the machine with **no power controller at all** and
nothing on screen explaining why.

```
powershell -ExecutionPolicy Bypass -File scripts\install-gpd-forge.ps1 -DryRun    # rehearse
powershell -ExecutionPolicy Bypass -File scripts\install-gpd-forge.ps1 -Restore   # do it
```

`-Substitute` records the prior service start type in `%ProgramData%\GPD Forge\takeover-state.json`
(outside Program Files, which `-Uninstall` deletes) so the restore puts back what was actually there.
For a takeover performed before that record existed, the restore uses `Automatic` and **says that it
is guessing** rather than implying it knows.

Run `-DryRun` first. It prints each change it would make and writes nothing. The NSIS installer itself is built
with `npx tauri build` in `ui/` (output: `ui/src-tauri/target/release/bundle/nsis/*-setup.exe`).

### When the build dies with `os error 4551`

Smart App Control refuses to execute unsigned binaries it does not trust, and cargo's per-crate
**build-scripts** are exactly that. The failure reads:

```
could not execute process `...\target\release\build\<crate>-<hash>\build-script-build` (never executed)
Una directiva de Control de aplicaciones bloqueó este archivo. (os error 4551)
```

Two things about this are counter-intuitive and cost real time on 2026-08-29:

- **Deleting `target/release/build` so cargo regenerates the scripts does not fix it.** That cure works
  for the .NET service (a different hash gets a different verdict); here the freshly compiled scripts
  were blocked too, on three consecutive runs.
- **Building with `CARGO_TARGET_DIR` outside the repository does.** Same source, same toolchain — only
  the location changed, and the build that had been refused three times completed:

  ```
  CARGO_TARGET_DIR="$env:TEMP\gpd-forge-tauri-target" npm run tauri build
  ```

  So SAC's decision is not purely about file content: **where the binary runs from is part of it.**

`install-gpd-forge.ps1` now does this automatically as a fallback after the first failure. It is not
the default because it forfeits the incremental cache (~6 minutes from cold). **Do not turn Smart App
Control off to work around this** — it cannot be turned back on without reinstalling Windows.

A shell build failure no longer aborts the install: the service is registered and started regardless,
and the installer exits non-zero explaining that the desktop window was not updated. Before that fix,
a blocked build left the machine with **no daemon at all**, because step 1 removes the service before
step 3 builds the shell.

---

# Supervised hardware bring-up

These scripts let you take GPD Forge to the metal **safely and on your terms**. They are the
gate between "tested code" and "writing to your device". Read this before running any of them.

## Safety model (important — this machine has active agent work)
- **`ec-probe.ps1`** — 100% read-only. Detects your model + PawnIO. Touches NO hardware. Run anytime.
- **`tdp-live.ps1 -Read`** (default) — reads current TDP via `ryzenadj --info`. Reads only.
- **`tdp-live.ps1 -ReassertCurrent`** — writes the *current* values back. Proves the write path with
  **zero net change** → safe even while GPD Tool is actively managing power for other sessions.
- **`tdp-live.ps1 -TargetW <n> -Confirm`** — actually changes power for a few seconds then
  **auto-reverts**. Refuses to run while MotionAssistant / GPD Tool are open (they'd fight it and
  collapse performance — the clash you flagged). Close them first, and prefer a moment when no
  session needs full performance.

TDP changes via RyzenAdj are **reversible on reboot**. Fan/EC writes are **not** and can brick the EC —
so there is deliberately **no fan-write script here yet**; that path stays gated until the EC register
map is confirmed on this exact device (see `ec-probe.ps1` and `docs/hardware/ec-registers.md`).

## Current device reality (checked 2026-08-25)
- **GPD Tool + GPDToolService are running** and managing power/fan. Don't do a real `-TargetW` change
  without closing them. `-Read` and `-ReassertCurrent` are fine.
- **PawnIO is already installed** (GPD Tool ships it) — no install needed for the future read-only EC probe.
- `ryzenadj.exe` is at `C:\Program Files\Motion Assistant\amd\ryzenadj.exe`.

## Recommended first steps, in order
1. `powershell -ExecutionPolicy Bypass -File scripts\ec-probe.ps1`            # read-only, anytime
2. `powershell -ExecutionPolicy Bypass -File scripts\tdp-live.ps1 -Read`      # read current TDP (elevates)
3. `powershell -ExecutionPolicy Bypass -File scripts\tdp-live.ps1 -ReassertCurrent`   # prove the write path, zero change
4. Only when you can pause GPD Tool and spare performance:
   `... tdp-live.ps1 -TargetW 18 -Seconds 15 -Confirm`

## Running the full service against hardware
`run-service-hardware.ps1` launches the GPD Forge service **elevated** with
`GPDFORGE_ENABLE_HARDWARE=1`, so the `/tdp` API endpoint drives RyzenAdj for real. The service does
not auto-change power on its own — only an explicit `POST /tdp` does. Same conflict caveat applies.
