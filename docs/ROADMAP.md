# CielCraft Roadmap

Spec milestones M0–M16 (versions 0.1–0.6) are implemented and validated in-game.
This tracks the remaining work toward 1.0. Sizes: S / M / L.

## Phase 1 — Reliability & daily use

- [x] **1.1 Mount + flying during travel** (M) — mount up for long legs, fly where
  unlocked, dismount to gather. Travel is on foot today.
- [x] **1.2 Item search → "Make N"** (M) — search box in the main window (spec §43/§77);
  plan and run from an item name without opening the crafting log.
- [x] **1.3 HQ ingredients** (M) — respect the configured HQ material fill: solve with
  the craft's real starting quality instead of assuming 0.
- [x] **1.4 Quick synthesis for trivial intermediates** (S–M) — bulk low-level
  intermediates via quick synth (much faster); config-gated, intermediates only.
- [x] **1.5 Light dynamic replanning** (S) — spec §26: re-verify ingredients before each
  production step; re-resolve the remaining plan on shortfall without redoing done work.
- [x] **1.6 Inventory-space guard** (S) — check free bag slots before/while batches and
  gathering; pause cleanly instead of failing verification confusingly.
- [x] **1.7 Emergency stop** (S) — `/cielcraft stop` halts production, batch, gathering,
  and navigation at once (spec §48).
- [x] **1.8 Release pipeline** (S) — tagged releases + `repo.json` custom Dalamud repo so
  the game machine updates from the plugin installer.

## Phase 2 — Smarter gathering (spec §37)

- [x] 2.1 GP yield/boon/+attempt actions (King's Yield, Blessed Harvest, Solid Reason,
  Ageless Words) (M)
- [x] 2.2 Cordials between nodes (S)
- [x] 2.3 Quick-gathering checkbox handling (S)
- [x] 2.4 Node routing: nearest-neighbor chains, multiple areas per item (M)
- [ ] 2.5 City aethernet shards (S, low priority)

## Phase 3 — Smarter crafting

- [x] 3.1 Buff tracking in CraftSnapshot (spec §10) — status, stacks and remaining
  steps; the adaptive engine counts Veneration/Muscle Memory in its progress
  estimates (M)
- [x] 3.2 Mid-craft re-solve (spec §17) — the vendored solver
  (`native/raphael-solver`, one added entry point) searches from the live
  state: progress, quality, durability, CP, Inner Quiet stacks, buff durations,
  one-shot availability. Proc reactions (Good/Excellent) stay rule-based in the
  adaptive engine; the solver assumes Normal conditions, as Raphael does. The
  touch-combo state is assumed lost on a recovery (only forgoes a CP discount).
  One recovery re-solve per craft; anything after that pauses safely (L)
- [x] 3.3 Food/potion in solves; re-solve on expiry (S–M)
- [x] 3.4 Target-quality objectives (e.g. stop at a chosen HQ chance) (S)
- [x] 3.5 Specialist actions (Heart and Soul, Quick Innovation — no step advance) (M)
- [x] 3.6 Combo-aware CP accounting — engine estimates stay worst-case-safe; true combo pricing is handled by the mid-craft re-solve (3.2)

## Phase 4 — Advanced content

- [x] 4.1 Collectable crafting (M)
- [x] 4.2 Expert recipes (M, after 3.2)
- [x] 4.3 Collectable gathering (M)
- [x] 4.4 Retainer/saddlebag inventory awareness, read-only first (spec §22) (M)
- [x] 4.5 Multi-recipe/job choice per item (S)
- [ ] 4.6 Zones without aetherytes (travel graph) (L, low priority)

## Phase 5 — Engineering health

- [ ] 5.1 Extract batch/production/gathering state machines into Core for offline
  tests (spec §50–51) (L) — still deferred, now the top post-1.0 item: the
  orchestrators (BatchCrafter, ProductionRunner, GatheringController/Loop) are
  the riskiest code and the least testable. Plan: give each a pure core driven
  by synthetic events (CraftStarted, ActionResolved, CraftEnded,
  InventoryChanged, NavigationCompleted, NodeOpened, Timeout, Pause, Resume)
  with time/log/framework as seams, and keep the Dalamud classes as thin
  adapters. Do it after the v1.0 in-game gate, since a pure refactor of
  validated code needs its own regression pass. All pure decision logic
  (resolution, adaptive engine, planner, inventory, ET, live-effect mapping,
  digit parsing) is already in Core with 60 tests
- [x] 5.2 Weekly scheduled CI against the latest Dalamud distrib to catch API drift (S)
- [x] 5.3 UI polish to spec §44–46 (plan preview, live production panel, debug errors) (M)

## Phase 6 — Unattended reliability & polish

- [x] 6.1 Auto-repair with Dark Matter below a configurable gear-condition threshold
- [x] 6.2 Auto-food: keep a configured food buff active during automation
- [x] 6.3 Resume after reload: interrupted production persisted and offered on load
- [x] 6.4 User-interference detection: manual movement in stationary phases pauses politely
- [x] 6.5 Chat notifications + end-of-run summary (produced, HQ count, elapsed)
- [x] 6.6 Item icons in search, materials, plan preview, and queue
- [x] 6.7 HQ-material auto-fill before each synthesis (config-gated)
- [x] 6.8 Multi-target production queue (persisted, sequential, holds on failure)

## Review follow-ups (structural, deferred)

From the full-code review: extract the duplicated mount/fly travel logic
(ProductionRunner + GatheringController) into one TravelDriver; consolidate the
five per-class Throttled/RetryInterval copies into a shared helper; a common
status/transition base for the five state machines; move interference detection
below ProductionRunner so standalone batches/gathers are covered too.

## Known validation-pending values

Values that could not be verified offline and are confirmed at in-game gates:
crafting-log callbacks (8 = synthesize, 9 = quick synthesis — validated),
general action ids (9 = mount roulette, 23 = dismount — validated; 6 = repair — pending),
repair-addon callbacks (0 = repair all, -1 = close; SelectYesno 0 = yes — pending),
RecipeNote NQ/HQ ingredient amount spans as the HQ-fill mechanism (pending),
crafting-status `RemainingTime` holding the remaining step count (the number on
the buff icon; shown in the debug window's craft buffs as "(N steps)" — pending;
a wrong read degrades to "buff applies to the next action only", never unsound),
Synthesis-window numbers arriving without thousands separators (the parser now
merges "12,345"/"12.345"/"12 345" groups either way).
