# scripts/ — supervised hardware bring-up

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
