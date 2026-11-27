# patch-manager

Desktop tool for AzerothCore 3.3.5a WoW clients that merges many mod patches into one generated patch MPQ.

- Merges DBCs row by row (3-way against the stock client), with per-field Server/Local tiering.
- Reports every conflict; overrides are saved per profile.
- Never deletes existing patches: managed patches are renamed to a non-loading name and can be restored.

Status: Milestones 1-7 done: MPQ/DBC read/write, mod import store, stock baseline, field-level DBC merge with server/local tiers and questions, line-level Lua/XML/TOC merge, renumbering of clashing local-mod IDs (with their references), apply/unapply, and a minimal desktop GUI. Tested in-game on a ChromieCraft client.

## Desktop app

1. **Choose WoW folder** (the 3.3.5a folder with Wow.exe, or its Data folder).
2. Add patches: **Add this client's patches** (everything the client currently loads), **Add folder...** (every .mpq/.zip in it and its subfolders), **Add files...**, or drag and drop files and folders onto the list. Later in the list wins.
3. **Merge**. Anything the tool had to decide (conflicting edits, whether a patch is from your server, rows a patch leaves out, ...) is listed as a yes/no or multiple-choice question with the recommended answer already applied. Change answers and click **Apply answers and merge again**.
4. **Save merged patch...** writes `patch-Z.MPQ` (and `<locale>/patch-<locale>-Z.MPQ`; very large sets such as HD packs continue in `patch-Y`, `patch-X`, ... since one archive must stay under 4 GB, skipping slots other patches still use), or **Install into client** renames the merged patches to `*.mpm-off` (nothing is deleted) and installs the merged one. **Uninstall from client** reverses it.

The app remembers the patch list and your answers per client.

### Downloads / platforms

| Platform | Package | Status |
|---|---|---|
| Linux x86_64 | `ModPatchManager-<ver>-x86_64.AppImage` | Tested (Bazzite) |
| Windows x64 | `ModPatchManager-<ver>-win-x64.zip` | **Work in progress**: the Windows launcher is not finished. Built on Linux, **not tested** |
| macOS Apple Silicon / Intel | `ModPatchManager-<ver>-osx-arm64.zip` / `-osx-x64.zip` | Built on Linux, unsigned, **not tested** |

Build them with `packaging/build-all.sh` (needs `appimagetool` for the AppImage and `zip`); output goes to `dist/`. See `docs/PLAN.md`.

## Build and test

```bash
dotnet test ModPatchManager.slnx
dotnet run --project src/Mpm.App   # the desktop app
```

Tests that need real client files run when `MPM_CLIENT_DATA` points at a 3.3.5a `Data` directory (read-only):

```bash
MPM_CLIENT_DATA=/path/to/WoW/Data dotnet test ModPatchManager.slnx
```

## CLI (temporary, for inspection)

```bash
dotnet run --project src/Mpm.Cli -- ls <archive.mpq>
dotnet run --project src/Mpm.Cli -- dbc <archive.mpq> 'DBFilesClient\Spell.dbc'
dotnet run --project src/Mpm.Cli -- cat <archive.mpq> <file> <out>
dotnet run --project src/Mpm.Cli -- pack <out.mpq> <dir>
dotnet run --project src/Mpm.Cli -- import <mod.mpq|mod.zip|folder> [name]
dotnet run --project src/Mpm.Cli -- mods
dotnet run --project src/Mpm.Cli -- show <id>
dotnet run --project src/Mpm.Cli -- remove <id>
dotnet run --project src/Mpm.Cli -- client <WoW/Data>
dotnet run --project src/Mpm.Cli -- baseline <WoW/Data> enUS 'DBFilesClient\Spell.dbc'
dotnet run --project src/Mpm.Cli -- build <WoW/Data> enUS <out dir> <mod id>...   # last id = highest priority
dotnet run --project src/Mpm.Cli -- apply <WoW/Data> <out dir> [--disable <patch>]...
dotnet run --project src/Mpm.Cli -- status <WoW/Data>
dotnet run --project src/Mpm.Cli -- unapply <WoW/Data>
```

`apply` renames the client's custom patches that are identical to a mod in the build (and anything already in the build's slot) to `<name>.mpm-off`, which the client does not load, then installs the build. `unapply` reverses it from `Data/.mpm-ledger.json`. Nothing is deleted except the archives `apply` installed.

## License

Copyright (C) 2026 dudemcbro. Licensed under the GNU Affero General Public License v3.0 or later (`AGPL-3.0-or-later`); see `LICENSE`. The bundled WoWDBDefs layouts keep their own license (below).

## Third-party data

`data/wowdbdefs/` holds DBC table layouts adapted from [WoWDBDefs](https://github.com/wowdev/WoWDBDefs), licensed CC BY-SA 4.0 (see that folder's `LICENSE.md` and `ATTRIBUTION.md`). It contains no game data; the tool reads stock data from the player's own client at run time.

Imported mods live in `$MPM_STORE`, else `$XDG_DATA_HOME/mpm/store`, else `~/.local/share/mpm/store`. Import and remove never modify the source files or the client.

Stack: C# / .NET 11 + Avalonia 12.
