# Mod Patch Manager for AzerothCore 3.3.5a clients

## Context
The 3.3.5a client loads a fixed set of patch MPQ names (`patch-4..9`, `patch-A..Z`, plus locale `patch-<loc>-*`), with later names overriding earlier ones. Mods often ship whole DBC files, so stacking mods means the last patch silently wipes the others' rows -> errors, and the name space runs out. Goal: a desktop tool that merges every mod into ONE generated patch, resolves conflicts sensibly, and never destroys the user's files.

Project dir is empty (`/var/mnt/d-drive/dev/mod-patch-manager`); greenfield.

## Decisions (from discussion)
- Contents handled: DBCs, models/textures (BLP/M2/WMO/ADT), UI Lua/XML.
- DBC conflicts: **hybrid** - 3-way field merge vs stock baseline when changed fields don't overlap; priority on overlap; every conflict in a report; per-conflict overrides saved in profile.
- Stack: **C# / .NET 11 (installed SDK) + Avalonia** desktop GUI (Linux first, Bazzite).
- Scope: **client only**. No server writes.
- **Two tiers** (needed because players use local patches the server doesn't know about):
  - *Server* mods: authoritative, locked, never renumbered, win overlapping fields.
  - *Local* mods (HD, UI, personal): merged around server data.
- **Auto-renumber** applies only to Local new-record IDs that clash; references rewritten inside Local mods only. Never renumber IDs that server-referenced rows depend on (e.g. `ItemDisplayInfo` used by server items). Server-vs-server clash = hard error.
- Input: MPQ, loose folders, zip - all normalized on import into a managed store (extracted files + manifest).
- Baseline: read from the user's clean client (`common*.MPQ`, `expansion*`, `lichking*`, locale MPQs, stock `patch.MPQ`, `patch-2`, `patch-3`). Note: in 3.3.5a all DBCs live in the locale archives (`<loc>/locale-<loc>.MPQ`, `patch-<loc>*.MPQ`), not the root ones.
- Output: one generated patch (e.g. `patch-Z.MPQ`, plus `patch-<loc>-Z.MPQ` if locale files exist).
- **Old patches are renamed, never deleted.** Managed patches are renamed to a non-loading name (e.g. `patch-4.MPQ` -> `patch-4.MPQ.mpm-off`; must not match any `patch-?.MPQ` pattern). A ledger (original name, new name, SHA-256) allows exact restore / "Unapply".

- Primary user: **players**. Server owners only distribute patches.
- **Per-server profiles**: each profile = server's patches + chosen local mods + overrides; switching rebuilds output.
- **Tiering auto-detected per field** (no standard exists for servers marking their patches):
  - Gameplay fields in gameplay DBCs (Spell, Item, Talent, SkillLineAbility, CharTitles, Map, ...) -> Server. New IDs above stock max are a strong signal.
  - Textures/models/Interface, cosmetic DBC columns -> Local.
  - Mixed mods split automatically. UI shows each classification with reasons; user may override.
  - Optional `mpm-server.json` manifest (tool-defined) gives exact tagging when a server ships it.
- Local edits to server rows: **visual content only** (icons, models, textures, visual DBC rows). Purely client-side, no server work. Never relink IDs the server sends (e.g. `Item.displayid`).

## Architecture
Solution `ModPatchManager.sln`:
- `Mpm.Core` - pipeline, no UI.
  - `Mpq/` - own managed MPQ reader + writer (done, M1). War3Net.IO.Mpq was tried and rejected: it cannot read stock 3.3.5a archives (sector-CRC flag). Reader handles format 0/1 headers, encryption incl. fix-key, single-unit and sectored files, zlib + bzip2 (SharpZipLib, MIT), delete markers. Writer emits format 0, 4 KiB zlib sectors, (listfile), no (attributes) - same shape as the proven mod-tortoisewow writer.
  - `Dbc/` - WDBC reader/writer (done, M1: raw records + verbatim string block, byte-identical round-trip on all stock enUS DBCs). Field widths vary (e.g. CharBaseInfo uses 1-byte fields), and not every DBC has a 4-byte ID in field 0, so layouts from WoWDBDefs (build 3.3.5.12340) are required before merging; they also give the foreign-key metadata renumbering needs.
  - `Store/` (done, M2) - MPQ/folder/zip -> store (`$MPM_STORE` or `~/.local/share/mpm/store`). `mods/<id>/manifest.json` (name, source path/kind/sha256, files with chain + sha256 + origin, warnings); contents in zlib-compressed content-addressed `blobs/`. Folder import reads MPQs (client slot order) then loose files on top; loose files are mapped by the first known stock top-level folder, so `Data/`, `MyMod/Data/` etc. prefixes are stripped; junk (READMEs) is skipped with a warning. Zip import guards against path escape and >16 GiB expansion. Re-importing identical content returns the existing mod. Removing a mod only touches the store.
  - `Client/` (done, M2) - `ClientInstall` lists locales and every patch the client loads (stock vs custom); `BaselineChain` resolves stock files from stock root + locale archives only.
  - `Classify/` - per-field tier classification: field schema tags (gameplay / cosmetic / link) maintained as data file per DBC; manifest override; reasons recorded for UI.
  - `Dbc/` layouts (done, M3) - vendored 3.3.5.12340 extract of WoWDBDefs in `data/wowdbdefs/` (CC BY-SA 4.0, attribution there; layouts only, no game data), embedded in Mpm.Core. Every stock enUS DBC matches its layout (tested). Each file is checked against its layout at build time (field count + record size), so a client whose server changed a table's shape falls back to whole-file merge for that table instead of corrupting it. Planned: infer layouts for unmatched/custom tables per client, cached by DBC hash; detect build from Wow.exe.
  - `Build/` (done, M3: priority merge) - `PatchBuilder`: per path, drop mod copies identical to stock; DBCs with matching layouts merge by row on ID (a mod only claims rows it changed vs stock; same row changed by two mods = conflict, later mod wins; stock rows are never deleted); ID-less tables are a row union; everything else is whole-file priority. A mod shipping one path in both chains uses the copy in the stock chain. Output placed by the stock copy's chain, new `Interface\`/`DBFilesClient\`/`Fonts\` files -> locale, else root. `BuildOutput` writes `patch-Z.MPQ`, `<loc>/patch-<loc>-Z.MPQ`, `build-report.json/.txt`.
  - `Install/` (done, M3) - `Installer.Apply` renames patches to `<name>.mpm-off` (never deletes; also renames whatever already occupies the build's slot), installs the build archives, and writes `Data/.mpm-ledger.json` after every step. `Unapply` deletes only archives it installed whose hash still matches (else moves them to `.mpm-modified`), renames everything back, verifies hashes. CLI default: disable the client's custom patches byte-identical to a mod in the build.
  - M4 (done): `PatchBuilder` merges DBCs field by field against stock (3-way). Tiers per mod: answer > `mpm-server.json` (folder/zip root or inside an MPQ) > auto (Server if it changes any gameplay field or adds gameplay rows). Classification in `data/classification-3.3.5.12340.json` (our own data): cosmetic tables and cosmetic columns of mixed tables; all text and server-sent IDs are gameplay. Local mods' gameplay edits are dropped unless the user keeps them; Server changes beat Local on the same field by default.
  - Questions (done, M4): every judgment call is a `Question` with a stable id, options, default and chosen value: `tier:<mod>`, `rows:<path>:<mods>` (same field changed differently), `newrows:<path>:<mods>` (same new ID), `file:<path>:<mods>`, `removals:<path>:<mod>` (mod lacks stock rows; default keep), `localgameplay:<mod>`, `incomplete:<mod>` (unidentified files; default keep the source patch loading). Answers go back in via `BuildOptions` (CLI: `build --answers build-questions.json`). The GUI presents these.
  - `Merge/TextMerge` (done, M6) - line-based 3-way merge (Myers diff vs stock, common prefix/suffix trimmed, gives up above 4000 edits) for `.lua`, `.xml`, `.toc` that exist in stock. Separate edits combine; identical edits are taken once; overlapping or adjacent edits are a conflict (git's rule). CRLF and BOM preserved; non-UTF-8 files are not merged. In the build: clean merge -> `text:<path>:<mods>` question, default "Combined"; conflict -> the `file:` question with "edits overlap near line N" in its detail.
  - Remaining per-file-type strategies (original plan, for reference):
    - DBC: row-level 3-way merge on ID, field diffs vs baseline, tier rules, cosmetic allow-list, override application.
    - Lua/XML/TOC: 3-way text merge vs stock; on conflict fall back to priority + report.
    - Binary assets: priority only, report.
  - `Renumber/` - detect Local new-ID clashes, allocate free IDs in a reserved range, rewrite FKs within Local mods (per WoWDBDefs relations); refuse if ID is server-referenced.
  - `Profiles/` - profile JSON: client path, locale, mod order, tiers, overrides.
- `Mpm.App` - Avalonia UI: profile picker, mod list (drag reorder, tier toggle), conflict browser (row/field view, pick winner), build/apply/unapply buttons, report viewer.
- `Mpm.Cli` - thin headless wrapper (build/apply/unapply) for testing and scripting.
- `Mpm.Tests` - xUnit.

## Milestones
1. MPQ read/write + DBC read/write round-trip (byte-identical on stock files).
2. Import + store + baseline resolution.
3. DBC merge (priority only), single output patch, rename ledger apply/unapply. -> usable MVP. (done; verified on the tortoise patches: 54 DBCs merged with row sets identical to the mod's, apply/unapply restores the Data dir exactly. In-game: verified 2026-11-25 on a reflink clone of a ChromieCraft client: 7 custom patches (A, B, d, J, Q, U, Z) merged into one build, character logged in world, unapply restored the Data dir byte-identically. That client showed the original problem: patch-B and patch-d both ship Spell.dbc, so 8 of patch-B's spell edits never load today; the merge keeps them.)
4. 3-way field merge, per-field classification, manifest support, cosmetic allow-list, conflict report. (done; on the ChromieCraft set it removed 8 false Spell.dbc conflicts where two patches made identical edits, and detected A/B/d/Q as server, J/U/Z as local.)
5. Minimal GUI (Avalonia), per user request 2026-11-25:
   - Inputs: (a) a WoW client (merge everything in its Data and locale patch folders), (b) a folder and its subfolders, (c) drag and drop of patches.
   - Output: one merged patch (with the locale companion when needed); for a client, optionally apply/unapply.
   - Every Question is shown as yes/no or multiple choice with the default preselected; changing answers rebuilds.
   - Packages: Linux AppImage (tested), Windows and macOS (built but NOT tested by the user; say so in release notes).
   - Profiles / saved answers per server.
   (done: `Mpm.App` (code-only Avalonia UI) over `Mpm.Core.Session.MergeSession`; per-client profiles in `<app data>/profiles`; install builds kept in `<app data>/builds`; headless UI tests in `tests/Mpm.App.Tests` (xunit v3, required by Avalonia.Headless.XUnit 12); `packaging/` scripts build the AppImage (smoke-tested: launches) and cross-build Windows/macOS zips (untested, unsigned).)
6. Lua/XML text merge. (done; on the Tortoise patch's 5 stock UI overrides: 4 merged and matched exactly, 1 correctly refused (edit at end of file touching the test edit); about 25 ms for a 17 KB file.)
7. Local-ID auto-renumber with FK rewriting.

## Findings (M1-M2)
- Wow.exe (12340) embeds `patch-?.MPQ` (Data\) and `patch-%s-?.MPQ` (Data\<locale>\): exactly one slot character. Renamed copies like `patch-4.MPQ.prev` are ignored by the client.
- Stock layout: `Interface\`, `DBFilesClient\`, `Fonts\` (and some `Sound\`) live only in the locale chain; models/textures/world only in the root chain. No stock path is in both chains (tested), so root-vs-locale priority never matters for the baseline.
- Real mods do not follow that split (the tortoise patch-4 ships `Interface\` and `DBFilesClient\` in the root chain, and its DBCs again in patch-enUS-4). So the build must place each merged file by path, into the chain where its stock copy lives (new files: root, except `Interface\`/`DBFilesClient\`/`Fonts\` -> locale), and treat a mod's copies of one path in both chains as one contribution.
- Still unverified: the client's order among custom slot characters (assumed ASCII: digits < letters) and root-vs-locale priority for custom patches. Only matters for describing what a user's current, unmanaged setup does; the tool's own output avoids it by using one slot.

- Many mod patches ship without a (listfile). Import recovers names by testing candidates against the hash table (all layout DBC names, plus every stock path with `import --client`). Files that still cannot be named mark the mod incomplete (`UnnamedFiles`), and apply leaves that source patch loading.
- `.mpm-off` suffix: Windows wildcard matching also checks 8.3 short names; "patch-4.MPQ.mpm-off" gets a short extension "MPM", not "MPQ", so `patch-?.MPQ` cannot match it.

## Open risks
- MPQ write: resolved (own writer, verified by an independent reader). Still needs an in-game load test.
- WoWDBDefs FK coverage for 3.3.5 may be incomplete -> renumber must refuse when relations unknown.
- Locale MPQ load order details - confirm against client behavior.
- Some mods ship patches that depend on another mod's patch; may need explicit dependencies in manifest.

## Verification
- Unit: DBC/MPQ round-trip on stock files; merge cases (disjoint rows, disjoint fields, overlapping fields, server-vs-local, local ID clash).
- Integration: fixture mods (two that both ship `Spell.dbc`, one HD texture pack, one UI mod) -> build -> assert merged rows present, report lists conflicts.
- Apply/unapply on a copy of a client `Data/` dir: originals renamed, hashes match after unapply, no file deleted.
- Manual: launch 3.3.5a client against a local AzerothCore with merged patch; check items/spells from each mod appear, no DBC load errors.
