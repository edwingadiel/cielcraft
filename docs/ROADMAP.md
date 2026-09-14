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

- [x] 3.1 Buff tracking in CraftSnapshot (spec §10) — prerequisite for deeper adaptive
  rules (M)
- [x] 3.2 Mid-craft re-solve / GetNextAction (spec §17) — FFI v2 solving from live state;
  optimal proc reactions, recovery from any deviation (L)
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

- [ ] 5.1 Extract batch/production state machines into Core for offline tests
  (spec §50–51) (M) — deliberately deferred: requires threading time/log/framework
  seams through validated orchestration code; all pure decision logic (resolution,
  adaptive engine, planner, inventory, ET) is already in Core with 32 tests
- [x] 5.2 Weekly scheduled CI against the latest Dalamud distrib to catch API drift (S)
- [x] 5.3 UI polish to spec §44–46 (plan preview, live production panel, debug errors) (M)

## Known validation-pending values

Values that could not be verified offline and are confirmed at in-game gates:
crafting-log callbacks (8 = synthesize — validated; 9 = quick synthesis — pending),
general action ids (9 = mount roulette, 23 = dismount — pending).
