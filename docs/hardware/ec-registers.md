# GPD EC register map (working notes)

Interoperability data for the `Fan/` and `Broker/` subsystems. Sourced from the `gpd-fan` Linux driver
(GPL-2) and FanControl.GPDPlugin (GPL-2+). **Reproduced as data; must be verified on real hardware before
shipping** — a wrong offset writes garbage into EC RAM.

## Model detection
Select the map by DMI. Our device: `Win32_ComputerSystemProduct.Name = "G1618-04"` (GPD Win 4).
Log the DMI product + BIOS/EC version at startup and pick the row below; refuse fan writes if the model is
unknown (fall back to EC auto, `pwm1_enable = 2`).

## Register map
| Model | Cmd addr / data port | EC RAM base | RPM read | PWM write | PWM max |
|---|---|---|---|---|---|
| Win 4 6800U | `0x2E` / `0x2F` | `0xC880` | `0xC311` | `0xC311` | 127 |
| Win 4 7840U (v1.0) | `0x4E` / `0x4F` | `0x0218` | `0x1809` | `0x0275` | 184 |
| Win Mini 7840U / 8840U / HX370 | `0x4E` / `0x4F` | `0x0478` | `0x047A` | `0x047A` | verify |
| **Win 4 2025 HX370 (our device)** | verify (start from 7840U + Win Mini HX370) | verify | verify | verify | verify |

> The 2025 HX370 Win 4 row is **unverified**. Phase-1 task: on-device, confirm RPM readback tracks a known
> PWM sweep before enabling manual control by default. Until then, ship read-only telemetry + EC auto.

## `pwm1_enable` semantics (match gpd-fan hwmon)
- `0` = full speed
- `1` = manual PWM (write duty `0..pwmMax` to the PWM register)
- `2` = automatic (EC controls)

## Access rules
- All reads/writes go through the **PawnIO broker**, never raw port I/O.
- Whitelist: only the command/data ports and EC-RAM offsets for the detected model. Fail closed on anything else.
- **Re-init the EC on driver load AND on resume** — the Win 4 firmware leaves it uninitialized (this is the
  root cause of "fan does nothing / abnormal at startup").

## Safety
- Clamp duty to `[minSafe, pwmMax]`. Never leave the fan at 0 with rising temps.
- Watchdog: if RPM readback stays 0 after a non-zero PWM write, revert to `pwm1_enable = 2` so the device
  can't overheat.

## References
- https://github.com/Cryolitia/gpd-fan-driver · https://docs.kernel.org/hwmon/gpd-fan.html
- https://github.com/chenx-dust/FanControl.GPDPlugin
