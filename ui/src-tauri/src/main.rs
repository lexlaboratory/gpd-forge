// GPD Forge — Tauri 2 desktop shell entry point. GPL-3.0-or-later.
//
// Thin client: it hosts the web UI (../dist), which talks to the local daemon API
// (http://127.0.0.1:8787). No hardware access lives here.
//
// Self-heal: when the user opens the desktop shortcut the shell probes the API; if it does not
// answer, it spawns `GpdForge.Service.dll` next to itself via the signed `dotnet.exe` host (SAC-safe),
// waits briefly, and tries again. If the daemon is still unreachable after three attempts, the window
// opens anyway — the UI shows an honest "service offline" banner with a Retry button instead of
// an indefinite spinner. Closes the loop on the previous "telemetry never appears" symptom.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use std::io::{Read, Write};
use std::net::{TcpStream, Shutdown};
use std::path::PathBuf;
use std::process::Command;
use std::thread;
use std::time::Duration;

use tauri::menu::{Menu, MenuItem};
use tauri::tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent};
use tauri::{Manager, WindowEvent};

const HEALTH_HOST: &str = "127.0.0.1";
const HEALTH_PORT: u16 = 8787;
const PROBE_TIMEOUT_MS: u64 = 500;
const SPAWN_SETTLE_MS: u64 = 1500;
const MAX_ATTEMPTS: usize = 3;

/// Open a TCP socket to 127.0.0.1:8787 with a tight timeout. Tells us whether something is
/// listening on the daemon port without paying a HTTP round-trip's worth of latency.
fn probe_port() -> bool {
    let addr = format!("{}:{}", HEALTH_HOST, HEALTH_PORT);
    match TcpStream::connect_timeout(
        &addr.parse().expect("static parse"),
        Duration::from_millis(PROBE_TIMEOUT_MS),
    ) {
        Ok(stream) => {
            let _ = stream.shutdown(Shutdown::Both);
            true
        }
        Err(_) => false,
    }
}

/// Open the same socket, write a minimal `GET /health HTTP/1.0`, read the status line.
/// Used on the final retry so we don't claim success against a half-started daemon.
fn probe_http_health() -> bool {
    let addr = format!("{}:{}", HEALTH_HOST, HEALTH_PORT);
    let mut stream = match TcpStream::connect_timeout(
        &addr.parse().expect("static parse"),
        Duration::from_millis(PROBE_TIMEOUT_MS),
    ) {
        Ok(s) => s,
        Err(_) => return false,
    };
    let _ = stream.set_read_timeout(Some(Duration::from_millis(PROBE_TIMEOUT_MS)));
    let _ = stream.set_write_timeout(Some(Duration::from_millis(PROBE_TIMEOUT_MS)));
    if stream
        .write_all(b"GET /health HTTP/1.0\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n")
        .is_err()
    {
        return false;
    }
    let mut buf = [0u8; 64];
    let mut total = String::new();
    while total.len() < 64 {
        match stream.read(&mut buf) {
            Ok(0) => break,
            Ok(n) => {
                total.push_str(&String::from_utf8_lossy(&buf[..n]));
                if total.contains("\r\n") {
                    break;
                }
            }
            Err(_) => break,
        }
    }
    total.starts_with("HTTP/1.") && total.contains(" 200 ")
}

fn which_dotnet() -> Option<PathBuf> {
    let paths = std::env::var_os("PATH")?;
    for p in std::env::split_paths(&paths) {
        let candidate = p.join(if cfg!(windows) { "dotnet.exe" } else { "dotnet" });
        if candidate.is_file() {
            return Some(candidate);
        }
    }
    None
}

/// Resolve the daemon binary location. Try the installed layout first
/// (`C:\Program Files\GPD Forge\service\GpdForge.Service.dll` next to `GPD Forge.exe`),
/// then fall back to the repo dev layout so `cargo run` from `ui/src-tauri/` works too.
fn resolve_daemon() -> Option<(PathBuf, PathBuf)> {
    let exe = std::env::current_exe().ok()?;
    let exe_dir = exe.parent()?.to_path_buf();

    // 1) Installed layout.
    let installed = exe_dir.join("service").join("GpdForge.Service.dll");
    if installed.is_file() {
        let dotnet = which_dotnet().unwrap_or_else(|| PathBuf::from("dotnet.exe"));
        return Some((dotnet, installed));
    }

    // 2) Repo dev layout: .../ui/src-tauri/target/debug/gpd-forge.exe → walk up to repo root.
    let mut p = exe_dir;
    for _ in 0..6 {
        let candidate = p.join("core").join("bin").join("Debug").join("net9.0-windows").join("GpdForge.Service.dll");
        if candidate.is_file() {
            return Some((PathBuf::from("dotnet.exe"), candidate));
        }
        match p.parent() {
            Some(parent) => p = parent.to_path_buf(),
            None => break,
        }
    }
    None
}

/// Spawn `dotnet <dll>` with the right env for SAC-safe hosting. Returns the PID; never panics.
fn spawn_daemon(dotnet: &PathBuf, dll: &PathBuf) -> Option<u32> {
    Command::new(dotnet)
        .arg(dll)
        .env("GPDFORGE_AUTO_PROFILES", "1")
        // No GPDFORGE_ENABLE_HARDWARE here — that flag must be set by the installer (HKLM write),
        // which we cannot reach from a non-elevated Tauri shell. Driverless WMI telemetry still
        // works without it.
        .spawn()
        .ok()
        .map(|c| c.id())
}

fn wait_for_daemon() -> bool {
    for i in 0..MAX_ATTEMPTS {
        // Cheap TCP probe first (fast). On the last attempt, confirm with an HTTP read.
        if probe_port() {
            return if i + 1 == MAX_ATTEMPTS { probe_http_health() } else { true };
        }
        thread::sleep(Duration::from_millis(SPAWN_SETTLE_MS));
    }
    false
}

/// Show the main window and bring it to the front. Used by the tray icon and its menu.
///
/// `set_focus` after `show` matters: a window unhidden without focus can come back BEHIND the
/// fullscreen game the user was in, which reads exactly like the tray click did nothing.
fn show_main_window(app: &tauri::AppHandle) {
    if let Some(window) = app.get_webview_window("main") {
        let _ = window.show();
        let _ = window.unminimize();
        let _ = window.set_focus();
    }
}

fn main() {
    tauri::Builder::default()
        // Closing the window HIDES it to the tray instead of exiting.
        //
        // Worth being precise about why, because the obvious reason is wrong: the thing that
        // controls this handheld is the Windows service, not this window. TDP, fan and profile
        // enforcement continue whether or not anything is on screen — closing the shell has never
        // stopped the control loop, and quitting from the tray does not stop it either.
        //
        // The real reasons are that a controller's UI which vanishes from the taskbar looks like the
        // control went with it, and that reopening it costs a Start-Menu round trip while a game is
        // running. Hiding keeps it one click away and keeps the state visible.
        //
        // "Quit" in the tray menu therefore closes the WINDOW, and says so — it does not, and must
        // not, pretend to be an off switch for the daemon. Uninstalling or stopping the service is
        // the way to actually stop controlling the machine.
        .on_window_event(|window, event| {
            if let WindowEvent::CloseRequested { api, .. } = event {
                if window.label() == "main" {
                    api.prevent_close();
                    let _ = window.hide();
                }
            }
        })
        .setup(|app| {
            // 1) Cheap TCP probe: anything listening on 127.0.0.1:8787 yet?
            let mut daemon_alive = probe_port();

            // 2) If not, try to spawn the service next to ourselves.
            if !daemon_alive {
                if let Some((dotnet, dll)) = resolve_daemon() {
                    if let Some(pid) = spawn_daemon(&dotnet, &dll) {
                        eprintln!("[forge] spawned GpdForge.Service (pid={pid}); waiting…");
                        daemon_alive = wait_for_daemon();
                        if !daemon_alive {
                            eprintln!("[forge] daemon did not respond after {MAX_ATTEMPTS} attempts");
                        }
                    } else {
                        eprintln!("[forge] could not spawn '{}' '{}'", dotnet.display(), dll.display());
                    }
                } else {
                    eprintln!("[forge] could not locate GpdForge.Service.dll next to the shell");
                }
            }

            // 2b) The tray icon. This is what makes closing-to-hide honest rather than a
            // disappearing act: there is always a visible way back, and the tooltip states the
            // thing users get wrong — that the daemon keeps working with the window closed.
            let show = MenuItem::with_id(app, "show", "Open GPD Forge", true, None::<&str>)?;
            let quit = MenuItem::with_id(app, "quit", "Close window (daemon keeps running)", true, None::<&str>)?;
            let menu = Menu::with_items(app, &[&show, &quit])?;

            TrayIconBuilder::with_id("gpd-forge-tray")
                .icon(app.default_window_icon().expect("bundled icon").clone())
                .tooltip("GPD Forge — the daemon runs as a Windows service, with or without this window")
                .menu(&menu)
                // The menu must NOT open on a left click, or the primary click has two meanings and
                // the common action (show the window) becomes the awkward one.
                .show_menu_on_left_click(false)
                .on_menu_event(|app, event| match event.id().as_ref() {
                    "show" => show_main_window(app),
                    // Hides rather than app.exit(): the window is the only thing being closed, which
                    // is what the label promises. Exiting the process would also tear down the tray
                    // icon and leave no way back to a service that is still very much running.
                    "quit" => {
                        if let Some(window) = app.get_webview_window("main") {
                            let _ = window.hide();
                        }
                    }
                    _ => {}
                })
                .on_tray_icon_event(|tray, event| {
                    // Left click toggles: down-then-up on the same icon is the Windows convention for
                    // "restore me", and matching Up specifically avoids firing twice per click.
                    if let TrayIconEvent::Click {
                        button: MouseButton::Left,
                        button_state: MouseButtonState::Up,
                        ..
                    } = event
                    {
                        let app = tray.app_handle();
                        if let Some(window) = app.get_webview_window("main") {
                            if window.is_visible().unwrap_or(false) {
                                let _ = window.hide();
                            } else {
                                show_main_window(app);
                            }
                        }
                    }
                })
                .build(app)?;

            // 3) Stamp the window title with the daemon state so App.tsx's offline banner can
            // recover instantly without waiting for the first polled /telemetry to fail.
            if let Some(window) = app.get_webview_window("main") {
                let label = if daemon_alive { "daemon=up" } else { "daemon=down" };
                let _ = window.set_title(&format!("GPD Forge — {label}"));

                // Devtools only in debug builds (was: always-open via the dev session).
                #[cfg(debug_assertions)]
                {
                    window.open_devtools();
                }
            }

            Ok(())
        })
        .run(tauri::generate_context!())
        .expect("error while running GPD Forge");
}
