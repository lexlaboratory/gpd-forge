# GPD Forge — MCP server

A zero-dependency [Model Context Protocol](https://modelcontextprotocol.io) server that exposes the
local GPD Forge daemon's telemetry and control as tools, so an agent can drive the handheld —
read thermals/power, switch modes, set TDP, arbitrate the fan, and queue **constraint-gated** batch
jobs (*"run this only on AC, under 80 °C, 02:00–07:00"*).

It speaks MCP's newline-delimited JSON-RPC 2.0 over **stdio** and calls the daemon over HTTP
(`http://127.0.0.1:8787` by default; override with `GPDFORGE_API`). Requires the GPD Forge service
running and Node 18+.

## Register

**Claude Code:**
```
claude mcp add gpd-forge -- node C:\Users\Alex\gpd-forge\mcp\server.mjs
```

**Any MCP client (JSON config):**
```json
{
  "mcpServers": {
    "gpd-forge": {
      "command": "node",
      "args": ["C:\\Users\\Alex\\gpd-forge\\mcp\\server.mjs"],
      "env": { "GPDFORGE_API": "http://127.0.0.1:8787" }
    }
  }
}
```

## Tools

| Tool | Kind | What it does |
|------|------|--------------|
| `get_telemetry` | read | CPU/GPU °C, watts, clock, fan RPM, FPS, battery %, discharge, AC, tdpVerified |
| `get_mode` | read | active power mode |
| `set_mode` | **write** | switch mode (gaming/ai/windows/battery/standby) → applies TDP + fan |
| `set_tdp` | **write** | set sustained TDP (W); returns `verified` |
| `get_battery_budget` | read | runtime estimate + what-if projections |
| `get_profiles` | read | per-mode TDP presets |
| `set_fan` | **write** | fan preference (Auto/Quiet/Balanced/Aggressive/Manual) |
| `set_auto_fps` | **write** | enable + target for Auto-TDP-to-FPS |
| `freeze_process` / `thaw_process` / `get_frozen` | **write**/read | suspend/resume background apps |
| `submit_job` / `get_jobs` | **write**/read | constraint-gated scheduler (requireAC / maxTempC / window) |
| `get_standby` / `restore_standby` | read/**write** | drain diagnostics; re-apply TDP+fan+HID |

Read tools are safe. Tools marked **write** change real hardware power/thermal state — the same
closed-loop, conflict-guarded path the UI uses (GPD Forge yields if another controller owns TDP).

## Test

```
GPDFORGE_API=http://127.0.0.1:8787 node mcp/test-mcp.mjs   # 9 checks against a live daemon
```
