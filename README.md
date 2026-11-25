# mod-patch-manager

Desktop tool for AzerothCore 3.3.5a WoW clients that merges many mod patches into one generated patch MPQ.

- Merges DBCs row by row (3-way against the stock client), with per-field Server/Local tiering.
- Reports every conflict; overrides are saved per profile.
- Never deletes existing patches: managed patches are renamed to a non-loading name and can be restored.

Status: Milestones 1-3 done (MPQ/DBC read/write, mod import store, stock baseline, row-level DBC merge, apply/unapply). Not yet tested in-game. GUI not started. See `docs/PLAN.md`.

## Build and test

```bash
dotnet test ModPatchManager.slnx
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

## Third-party data

`data/wowdbdefs/` holds DBC table layouts adapted from [WoWDBDefs](https://github.com/wowdev/WoWDBDefs), licensed CC BY-SA 4.0 (see that folder's `LICENSE.md` and `ATTRIBUTION.md`). It contains no game data; the tool reads stock data from the player's own client at run time.

Imported mods live in `$MPM_STORE`, else `$XDG_DATA_HOME/mpm/store`, else `~/.local/share/mpm/store`. Import and remove never modify the source files or the client.

Stack: C# / .NET 11 + Avalonia (GUI not started yet).
