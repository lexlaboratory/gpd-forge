# GPD Forge — the PACKAGED shell, driven through Windows UI Automation. GPL-3.0-or-later.
#
# WHY THIS EXISTS. Every incident of the week of 2026-08-28 lived in the shipped shell, and the test
# suite structurally could not see any of them:
#
#   * The installed GPD Forge.exe embedded a bundle whose asset BASE was relative. Inside Tauri the
#     origin is http://tauri.localhost, so every fetch 404'd and all tiles read "--". The daemon was
#     healthy the whole time. Establishing this needed diffing the installed binary against a fresh
#     build hunting for marker strings.
#   * install-gpd-forge.ps1 claimed to build the shell and actually only copied whatever was in
#     target/release.
#   * A reinstall erased GPDFORGE_ENABLE_FAN_CONTROL from the service environment.
#
# Playwright cannot reach any of it: it runs `vite preview` in Chromium, which is a different
# artefact from the Tauri binary in Program Files.
#
# WHAT UIA CAN AND CANNOT SEE — the honest scope of this file.
#
# GPD Forge is Tauri, so its content is WebView2. Measured on 2026-08-31, the UIA tree under the
# window is: Tauri Window > WRY_WEBVIEW > Chrome_WidgetWin_1 > BrowserRootView > ... — Chromium's own
# view hierarchy, with EVERY node blank. Not one DOM element is exposed. So:
#
#   UIA verifies the WINDOW layer:  it opens, what it is titled, that a webview was actually
#                                   mounted, and that closing hides to tray instead of exiting.
#   Playwright verifies the DOM:    everything rendered inside it.
#
# Neither substitutes for the other, and a test here that claimed to check page content would be
# asserting against blanks. That division is why the ECC `windows-desktop-e2e` skill says to send
# WebView2 apps to browser automation for the HTML layer — this file takes the other half.
#
# Run:
#   python -m venv .venv-desktop && .venv-desktop\Scripts\pip install -r tests/desktop/requirements.txt
#   .venv-desktop\Scripts\python -m pytest tests/desktop -v
#
# On this machine the interpreter must be the winget-signed Python: Smart App Control blocks the
# CPython that uv manages (os error 4551).

import os
import subprocess
import time

import pytest
from pywinauto import Desktop

INSTALLED_SHELL = r"C:\Program Files\GPD Forge\GPD Forge.exe"

# Generous because a cold start pays for WebView2 initialisation, and a flaky desktop suite gets
# switched off faster than it finds bugs.
WINDOW_TIMEOUT_S = 30.0
POLL_S = 0.5


def _windows_titled(fragment: str, pid: int | None = None):
    """Top-level windows whose title contains `fragment`, optionally filtered to one process."""
    found = []
    for w in Desktop(backend="uia").windows():
        try:
            if fragment in w.window_text() and (pid is None or w.process_id() == pid):
                found.append(w)
        except Exception:
            # A window can vanish between enumeration and inspection. That is normal on a live
            # desktop and is not a test failure.
            continue
    return found


def _wait_for_window(fragment: str, pid: int, timeout: float = WINDOW_TIMEOUT_S):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        hits = _windows_titled(fragment, pid)
        if hits:
            return hits[0]
        time.sleep(POLL_S)
    return None


def _wait_until_gone(fragment: str, pid: int, timeout: float = 10.0) -> bool:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if not _windows_titled(fragment, pid):
            return True
        time.sleep(POLL_S)
    return False


@pytest.fixture(scope="module")
def shell():
    """
    Starts its OWN instance of the installed shell and kills it afterwards.

    Deliberately not reusing whatever the user already has running: that instance may be minimised to
    the tray (it usually is), and a test that closes someone's window as a side effect is a test
    people learn to avoid running.

    Reusing it is also not possible in the way you might expect — measured 2026-08-31, the shell does
    NOT enforce single-instance, so a second launch opens a second window rather than focusing the
    first. That makes this fixture straightforward, and it is worth knowing about the product.
    """
    if not os.path.exists(INSTALLED_SHELL):
        pytest.skip(f"GPD Forge is not installed at {INSTALLED_SHELL}; these tests describe the "
                    "packaged artefact and have nothing to say without one.")

    proc = subprocess.Popen([INSTALLED_SHELL])
    window = _wait_for_window("GPD Forge", proc.pid)

    if window is None:
        proc.kill()
        pytest.fail(
            f"The installed shell started (pid {proc.pid}) but produced no window within "
            f"{WINDOW_TIMEOUT_S:.0f}s. This is the failure mode that reached Program Files on "
            "2026-08-28 — a binary that runs and shows nothing usable."
        )

    yield proc, window

    try:
        proc.kill()
        proc.wait(timeout=10)
    except Exception:
        pass


def test_the_installed_shell_opens_a_window(shell):
    _, window = shell
    assert window.is_visible(), "The shell's window exists but is not visible."
    assert window.class_name() == "Tauri Window", (
        f"Expected the Tauri window class, got {window.class_name()!r}. If this changed, the shell "
        "is no longer the artefact these tests describe."
    )


def test_the_title_states_whether_the_daemon_is_reachable(shell):
    """
    The title is the shell's one honest self-report, and it exists because of a specific failure: on
    2026-08-28 the app showed no telemetry for hours while the daemon was healthy, and NOTHING on
    screen could say which build was running or whether it had a daemon at all.

    Asserted as "says one or the other", not "says up". A test demanding daemon=up would fail on a
    machine where the service is legitimately stopped, and would be measuring the service rather than
    the shell's ability to report on it.
    """
    _, window = shell
    title = window.window_text()

    assert "GPD Forge" in title, f"Window title does not name the app: {title!r}"
    assert ("daemon=up" in title) or ("daemon=down" in title), (
        f"The title carries no daemon state: {title!r}. Without it, an app showing empty tiles is "
        "indistinguishable from an app that cannot reach its daemon — which is exactly the state "
        "that took hours to diagnose on 2026-08-28."
    )


def test_a_webview_is_actually_mounted(shell):
    """
    A Tauri window with no webview is a grey rectangle: the process runs, the window opens, and every
    check above still passes. This asserts the host control exists.

    It cannot go further. WRY_WEBVIEW's children are Chromium's own view hierarchy with blank names,
    and no DOM node is exposed to UIA — verified by walking the tree on 2026-08-31. What is INSIDE
    the page belongs to the Playwright suite.
    """
    _, window = shell
    classes = []
    for d in window.descendants():
        try:
            classes.append(d.class_name())
        except Exception:
            continue

    assert "WRY_WEBVIEW" in classes, (
        "No WRY_WEBVIEW under the shell window, so no webview host was created. "
        f"Found instead: {sorted(set(c for c in classes if c))[:12]}"
    )


def test_closing_the_window_hides_to_tray_instead_of_exiting(shell):
    """
    The behaviour confirmed with the user on 2026-08-30, and the one most likely to be undone by an
    innocent-looking change to the window handler.

    It matters beyond convenience: the thing controlling the handheld is the Windows service, and the
    shell is its UI. But a user who closes the window and sees the process die reasonably concludes
    that TDP and fan enforcement died with it. Two assertions, because either alone is satisfied by
    the wrong behaviour — a window that vanishes because the app exited passes the first; an app that
    survives with its window still on screen passes the second.
    """
    proc, window = shell

    window.close()

    assert _wait_until_gone("GPD Forge", proc.pid), (
        "The window was still on screen after close(); it neither hid nor exited."
    )
    assert proc.poll() is None, (
        "Closing the window killed the shell process. It is supposed to hide to the tray — a user "
        "who closes the window and sees the process end will assume power management stopped too."
    )
