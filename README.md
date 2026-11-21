# mod-patch-manager

Desktop tool for AzerothCore 3.3.5a WoW clients that merges many mod patches into one generated patch MPQ.

- Merges DBCs row by row (3-way against the stock client), with per-field Server/Local tiering.
- Reports every conflict; overrides are saved per profile.
- Never deletes existing patches: managed patches are renamed to a non-loading name and can be restored.

Status: planning. See `docs/PLAN.md`.

Stack: C# / .NET + Avalonia.
