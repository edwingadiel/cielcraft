# Orders model (roadmap 7.13) — design and work split

Replaces the single target + production queue with an **order book**: groups
of orders that run in sequence; orders inside a group are planned together
(shared sub-crafts, one gather trip per material). Borrowed from Lisbeth.

## Contract (already on main)

- `CielCraft.Core/Orders/OrderModel.cs` — `AmountMode` (Absolute, Restock),
  `ProductionMode` (Any, ForceHq, QuickSynth, Collectable), `Order`,
  `OrderGroup`, `OrderBook` (plain mutable classes, serialized by the plugin
  configuration as-is), `PlanTarget`, `OrderOutcome`, `GroupPlan`.
- `CielCraft.Core/DependencyResolver.cs` — `Resolve(IReadOnlyList<PlanTarget>, …)`
  plans several targets as one graph; `PlannedCraft.Mode` carries the order's
  production mode on target steps; `ProductionPlan.Targets` (the old
  `TargetItemId`/`TargetQuantity` are the first target, kept for callers not
  yet converted). Materials-only targets expand their ingredients without
  their own craft.
- `CielCraft.Core/Orders/OrderPlanner.cs` — `PlanGroup` (restock = amount −
  owned; disabled / satisfied / collectable / uncraftable orders are skipped
  with a reason), `Evaluate`, `RunnableGroups`.
- `CielCraft.Core/Orders/TeamcraftListParser.cs`, `OrderBookJson.cs` — stubs.
- `CielCraft/Crafting/OrderRunner.cs` — API skeleton (State, StatusText,
  Running, CurrentGroup, CurrentPlan, Cycle, Start, Hold, Stop, Preview,
  Tick, Describe); constructed and ticked by `Plugin` as `plugin.OrderRunner`.
- `Configuration.Orders` (`OrderBook`) exists next to the legacy
  `QueueItems`.

## Semantics

- **Amount modes.** Absolute produces exactly N. Restock produces
  `N − owned` (NQ+HQ count in the bag) and is skipped as "already stocked"
  when nothing is missing; in perpetual mode this is what makes the book
  idle-safe.
- **Production modes** (target step only; intermediates keep the existing
  "quick synth intermediates" setting): Any = current behaviour; ForceHq =
  solve at 100 % quality with HQ materials and keep crafting until the HQ
  count of the item has risen by the ordered amount (NQ results do not count
  and the batch continues while materials last); QuickSynth = quick
  synthesis for the final craft too when the game offers it (falls back to a
  normal craft when refused); Collectable = refused by the planner until
  roadmap 7.23.
- **Materials only** = gather and craft everything the item needs, skip its
  own final craft (Lisbeth's "materials only").
- **Groups** run in order; the first enabled group with an enabled order that
  plans to something non-empty runs; when its production completes the next
  group is planned (fresh inventory read); when the last completes the book
  is `Completed`, or in **perpetual** mode restarts from the first group
  (`Cycle++`). Failure or pause of the production runner puts the book on
  `Held` with the runner's status; `Start` while held resumes the runner if
  it is paused, else re-plans from the current group.
- **Hold** stops advancing but leaves the current production running.
  **Stop** stops the production runner too. "Stop everything" calls Stop.
- **Exit when done** (RunFinisher) fires only when the book is `Completed`
  (or not running) and the production runner completed.
- **Migration.** On load, if `QueueItems` is non-empty and `Orders.Groups`
  is empty, move them into one group named "Queue" as Absolute/Any orders
  and clear `QueueItems`. The saved-production resume (roadmap 6.3) keeps
  working for the plan in flight.
- **Import / export.** JSON: `{"version":1,"perpetual":false,"groups":[{"name":…,"enabled":true,"orders":[{"itemId":…,"name":…,"amount":…,"amountMode":"Absolute","mode":"Any","materialsOnly":false,"enabled":true,"note":""}]}]}`;
  ids are regenerated on import. Teamcraft: the text of "copy list as text"
  (section headings such as `Final items :` / `Items :` / `Crystals :` /
  `Gathering :`, lines `3x Iron Ingot` or `Iron Ingot x3`); the final items
  become orders in a new group named after the paste time; other sections
  are ignored unless the user picks "import everything".

## Work packages (three parallel agents, worktrees, merge order A → B → C)

### A — Core (owns `CielCraft.Core/Orders/*`, `CielCraft.Tests/Order*Tests.cs`, `DependencyResolverTests.cs`)
1. Implement `TeamcraftListParser.Parse` / `FinalItems` (tolerant: `3x Name`,
   `Name x3`, `3 Name`, `Name ×3`, HQ markers such as `(HQ)` or `HQ` suffix
   set nothing but are stripped; headings are lines ending with `:`; blank
   lines ignored).
2. Implement `OrderBookJson.Export` / `Import` with `System.Text.Json`
   (Core has no Newtonsoft); enums as strings; unknown fields ignored;
   version check.
3. Tests: multi-target resolve (shared intermediate merged, stock consumed
   once, surplus of target 1 feeds target 2, materials-only expands
   ingredients only, mode lands on the target step and survives being an
   intermediate too); `OrderPlanner` (restock math, skip reasons,
   `RunnableGroups`); parser fixtures (a real Teamcraft paste with three
   sections); JSON round trip incl. a hand-edited minimal document.
4. Review the resolver change for planning bugs and fix in place.

### B — Runner (owns `ProductionRunner.cs`, `BatchCrafter.cs`, `OrderRunner.cs`, `ProductionQueue.cs` (delete), `RunFinisher.cs`, `Plugin.cs`, `Configuration.cs`, `DiagnosticReport.cs`, `DebugWindow.cs`)
1. `ProductionRunner`: multi-target plans — summary, replan and saved
   progress per target (`SavedProductionState` becomes a list of
   `(ItemId, Quantity, InitialCount, Mode, MaterialsOnly)` plus the target
   list; `TryResumeSaved` re-plans the remaining targets); step
   `Mode` → `BatchCrafter.Start(crafts, quickSynth: …, requireHq: …)`;
   QuickSynth mode falls back to a normal craft when the game refuses.
2. `BatchCrafter.Start(int quantity, bool quickSynth = false, bool requireHq = false)`:
   with `requireHq` verify against `GetHqItemCount`, solve at 100 % target
   quality, keep going until the HQ gain reaches the quantity; fail when a
   craft cannot start for lack of materials.
3. `OrderRunner`: implement per the semantics above; log with the
   `[Orders]` prefix; `Describe` lists groups with outcomes.
4. Delete `ProductionQueue`; `RunFinisher` and `Plugin.StopEverything` use
   `OrderRunner`; `DiagnosticReport` prints the order runner; configuration
   migration of `QueueItems`; `/cielcraft run` (start the book) and
   `/cielcraft hold` commands.
5. Do **not** edit `MainWindow.cs`; C removes the queue UI there. Leave a
   compile-compatible `plugin.ProductionQueue` removed only after C's merge
   is not required — instead, remove it and expect the merge of C to drop
   the last references (the coordinator resolves).

### C — UI (owns `MainWindow.cs`, new `Windows/OrdersPanel.cs`, `UiTheme.cs`)
1. Replace the target search + Queue section with an **Orders** panel:
   groups as collapsible headers (rename inline, enable toggle, move
   up/down, delete, "Preview" showing the group plan with per-order outcome
   lines and skip reasons), orders as rows (icon, name, amount input,
   amount mode combo, production mode combo, materials-only and enabled
   checkboxes, delete). "Add order" uses the existing craftable search;
   the crafting-log selection can be added with one click.
2. Run controls: **Run orders** / **Hold** / **Stop**, perpetual toggle,
   status line from `plugin.OrderRunner.StatusText`, the running group
   highlighted with its production progress (reuse `DrawRunnerActive`).
   Keep the quick **Batch ×N** button for the crafting-log selection.
3. Import / export: buttons that read/write the clipboard
   (`ImGui.GetClipboardText` / `SetClipboardText`): "Export JSON", "Import
   JSON", "Import Teamcraft list" (names resolved through
   `plugin.RecipeProvider.SearchCraftable(name, 1)` exact-name match first;
   unresolved names reported in a red line, not imported).
4. Keep the codebase's short "why" comments and the UiTheme look; window
   must stay usable at its default size (scroll inside the orders list).

## Rules for every agent
- Zero compiler warnings; `dotnet build` then `dotnet test --no-build` green
  (`export PATH="/c/Program Files/dotnet:$PATH"`).
- Commit on your worktree branch with messages ending in
  `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`; do not merge
  to main; report what changed and anything you could not do as specified.
- Touch only the files your package owns; if you must change a shared type,
  say so in the report instead of editing it.
