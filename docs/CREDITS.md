# Credits & upstream

GPD Forge stands on years of community reverse-engineering. This file records what we
learn from, reuse, or derive — and under which license — so GPL-3 compliance is auditable.

## Tooling / engines
- **[RyzenAdj](https://github.com/FlyGoat/RyzenAdj)** (LGPL-2.1) — SMU mailbox + PM table; the TDP engine.
- **[PawnIO](https://pawnio.eu/)** — signed kernel modules in a restricted VM; replaces WinRing0. See its
  redistribution terms before bundling.
- **[LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)** (MPL-2.0) — sensors.
- **[PresentMon](https://github.com/GameTechDev/PresentMon)** (MIT) — FPS/frametime via ETW.
- **[ViGEmBus](https://github.com/nefarius/ViGEmBus)** / **ViGEm.NET** (MIT) — virtual controllers.
- **[HidHide](https://github.com/nefarius/HidHide)** (GPL-3) — hides the physical controller.
- **RTSSSharedMemoryNET** (MIT) — RivaTuner OSD / frame-limit bridge.

## Reference implementations (behavior & data we mirror)
- **[HandheldCompanion](https://github.com/Valkirie/HandheldCompanion)** (GPL-3) — QuickTools overlay,
  Auto-TDP-to-FPS, focus-process profiles, IMU/gyro profiles.
- **[Universal x86 Tuning Utility](https://github.com/JamesCJ60/Universal-x86-Tuning-Utility)** (GPL-3) —
  power presets, per-game rules, PawnIO adoption.
- **[gpd-fan-driver](https://github.com/Cryolitia/gpd-fan-driver)** (GPL-2) — the per-model EC register map.
  Upstream since Linux kernel 6.18; see `docs/hardware/ec-registers.md` (to be authored in Phase 1).
- **[FanControl.GPDPlugin](https://github.com/chenx-dust/FanControl.GPDPlugin)** (GPL-2+) — Windows EC access
  via PawnIO wrappers (EcRam / FanSensor / FanController).
- **[GPD-LinuxControls](https://github.com/Cryolitia/GPD-LinuxControls)** (MIT) / **[pyWinControls](https://github.com/pelrun/pyWinControls)** —
  HID `SET_REPORT`/`GET_REPORT` button/deadzone protocol (VID 0x2F24 / PID 0x0135, 1024-byte config blob).

## Known EC register map (GPD Win 4, from gpd-fan) — to verify on real hardware in Phase 1
| Model | Cmd addr / data | EC RAM | RPM | PWM write | PWM max |
|---|---|---|---|---|---|
| Win 4 6800U | 0x2E / 0x2F | 0xC880 | 0xC311 | 0xC311 | 127 |
| Win 4 7840U (v1.0) | 0x4E / 0x4F | 0x0218 | 0x1809 | 0x0275 | 184 |
| Win Mini 7840U/8840U/HX370 | 0x4E / 0x4F | 0x0478 | 0x047A | 0x047A | — |

> These are interoperability facts (data), reproduced for correctness. Any code that reads/writes
> them and is adapted from a GPL/LGPL upstream carries an in-file attribution header.
