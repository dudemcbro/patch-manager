Mod Patch Manager - Windows / macOS build

These builds are produced on Linux and have NOT been tested on Windows or macOS.
Please report problems.

The Windows launcher is a work in progress and may not start or behave correctly yet.

Windows: unzip and run ModPatchManager.exe. SmartScreen may warn because the
program is not signed ("More info" -> "Run anyway").

macOS: the app is not signed or notarized. After unzipping, either right-click
the app and choose Open, or run:
    xattr -dr com.apple.quarantine "Mod Patch Manager.app"

What it does: choose your WoW 3.3.5a folder, add patches (the client's own,
a folder, or drag and drop), click Merge, answer any questions, then Save the
merged patch or Install it. Install renames your existing patches to
*.mpm-off (nothing is deleted); Uninstall puts them back.
