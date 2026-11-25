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
  - `Import/` - MPQ/folder/zip -> store (`~/.local/share/mpm/store/<modId>/`), manifest.json (name, tier, source hash, file list).
  - `Baseline/` - resolve stock file per path using real client load order.
  - `Classify/` - per-field tier classification: field schema tags (gameplay / cosmetic / link) maintained as data file per DBC; manifest override; reasons recorded for UI.
  - `Merge/` - per-file-type strategies:
    - DBC: row-level 3-way merge on ID, field diffs vs baseline, tier rules, cosmetic allow-list, override application.
    - Lua/XML/TOC: 3-way text merge vs stock; on conflict fall back to priority + report.
    - Binary assets: priority only, report.
  - `Renumber/` - detect Local new-ID clashes, allocate free IDs in a reserved range, rewrite FKs within Local mods (per WoWDBDefs relations); refuse if ID is server-referenced.
  - `Build/` - write merged tree into output MPQ(s); conflict report (JSON + human-readable).
  - `Install/` - rename ledger (`<client>/Data/.mpm-ledger.json`), apply/unapply, verify hashes, detect manual changes.
  - `Profiles/` - profile JSON: client path, locale, mod order, tiers, overrides.
- `Mpm.App` - Avalonia UI: profile picker, mod list (drag reorder, tier toggle), conflict browser (row/field view, pick winner), build/apply/unapply buttons, report viewer.
- `Mpm.Cli` - thin headless wrapper (build/apply/unapply) for testing and scripting.
- `Mpm.Tests` - xUnit.

## Milestones
1. MPQ read/write + DBC read/write round-trip (byte-identical on stock files).
2. Import + store + baseline resolution.
3. DBC merge (priority only), single output patch, rename ledger apply/unapply. -> usable MVP.
4. 3-way field merge, per-field classification, manifest support, cosmetic allow-list, conflict report.
5. GUI conflict browser + saved overrides; profiles.
6. Lua/XML text merge.
7. Local-ID auto-renumber with FK rewriting.

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
