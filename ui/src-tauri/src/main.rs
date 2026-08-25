// GPD Forge — Tauri 2 desktop shell entry point. GPL-3.0-or-later.
//
// Thin client: it only hosts the web UI (../dist), which talks to the local daemon API
// (http://127.0.0.1:8787). No hardware access lives here.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

fn main() {
    tauri::Builder::default()
        .run(tauri::generate_context!())
        .expect("error while running GPD Forge");
}
