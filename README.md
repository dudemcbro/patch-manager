# mod-patch-manager

Desktop tool for AzerothCore 3.3.5a WoW clients that merges many mod patches into one generated patch MPQ.

- Merges DBCs row by row (3-way against the stock client), with per-field Server/Local tiering.
- Reports every conflict; overrides are saved per profile.
- Never deletes existing patches: managed patches are renamed to a non-loading name and can be restored.

Status: Milestone 1 done (MPQ read/write, WDBC read/write). See `docs/PLAN.md`.

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
```

Stack: C# / .NET 11 + Avalonia (GUI not started yet).
