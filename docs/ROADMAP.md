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

## Phase 7 — 2.0: beyond crafting (planned, after the v1.0 gate)

Requested 2026-09-14. Ordered by how much each item reuses what exists; the
first three are cheap, the last two are projects of their own. All of them
follow the same rules as everything else: observed transitions, verify
against inventory, pause with a reason.

- [ ] 7.1 Standalone gather target (S) — "Gather X ×N" from the main window,
  including collectables; ship with tests D1, D3 and D4 run through it (the
  debug-tab path was never validated in game; deferred 2026-09-15),
  the way a recipe is entered today: plan = the runner's gather phase alone
  (teleport, travel, timed windows, loop), no crafting. Fish targets join
  this once 7.5 exists.
- [ ] 7.2 Spiritbond / materia extraction (S–M) — detect any equipped piece
  at 100% spiritbond, open Materialize and extract (general action, addon
  callback — ids pending), between crafts/nodes like repair and food.
  Applies to crafter and gatherer gear alike: extraction runs between
  crafts and between nodes. Optional "spiritbond mode": loop a chosen cheap
  recipe (crafters) or a chosen node/item (gatherers) purely to bond the
  equipped set, stopping at a target materia count.
- [ ] 7.3 NPC interaction layer (M) — shared prerequisite for 7.4: locate an
  NPC (ENpcResident + Level sheet → territory/position), teleport to the
  nearest attuned aetheryte, navmesh to the NPC, interact, drive the
  dialog (SelectIconString/SelectString by option text, Talk advance),
  with the same timeouts and interference rules as travel today.
  - [ ] 7.3a NPC repair when no Dark Matter (S after 7.3) — find the nearest
    mender, repair all, return to the previous task. Falls back to this
    automatically when self-repair finds no Dark Matter in the bags.
  - [ ] 7.3b Vendor purchases (M after 7.3) — GilShopItem sheet maps item →
    shop → NPC; the planner treats such items as "buy N" steps instead of
    missing materials (gil-gated, with a configurable gil floor and a
    per-run spend cap). Special/currency shops excluded at first.
- [ ] 7.4 Fishing (L) — needed by several CUL recipes. FishingSpot /
  FishParameter / SpearfishingItem sheets for spot, bait and (where known)
  time/weather windows; cast → observe bite (tug type) → hook → verify the
  catch in inventory; mooch when the target needs it; bait purchases via
  7.3b. Gearset for FSH, travel via the existing runner phases. Fish as
  raw materials in production plans, and as 7.1 standalone targets.
  Candidate shortcut: drive the AutoHook plugin over IPC for the
  bite/hook timing if it exposes one (to confirm); own implementation
  otherwise.
- [ ] 7.5 Combat drops (XL) — skins, hides, etc. Three separate problems:
  (1) data: no game sheet maps items to monsters, so a bundled drop table
  built from an external dataset (Garland Tools / gamerescape) with a
  refresh script, like the gathering database today; (2) combat: rotation
  and targeting from a combat plugin over IPC — BossMod Reborn (AI mode +
  autorotation) and Rotation Solver Reborn are the candidates; confirm
  which exposes a stable IPC before designing around it; (3) the hunting
  loop: travel to the spawn area, pick a target of the right name/level,
  let the combat plugin fight, loot verification by inventory delta,
  retreat/heal rules, death handling, "someone else is here" etiquette.
  Combat-job gearsets and level gating decide feasibility per item.

- [ ] 7.6 Teleport to the estate / home point for crafting (S) — after the
  gather phase the runner already returns to the zone aetheryte (safe idle
  spot); 2.0 should let the user pick where crafting happens (estate hall,
  apartment, inn room) and teleport there before the first craft step.

- [ ] 7.7 Solution cache (S) — the solver runs on every craft even when the
  same recipe repeats with the same stats, buffs and initial quality. Key a
  cache on the full CraftSetup + CraftObjective (stats, CP, food, specialist
  flags, target/initial quality) and reuse the action list within a batch and
  across sessions (persist in the config folder); invalidate on any key
  change. Mid-craft recovery solves stay uncached (live state is unique).

- [x] 7.9 Pre-flight gearset check (S) — refuse to start a plan whose craft or
  gather jobs have no gearset, naming the job, instead of failing 15s into
  the step. Done 2026-09-14.

- [ ] 7.8 Rotation visibility and manual rotations (S–M) — show the solved
  rotation (action list, expected progress/quality per step) in the main
  window before and during a craft, not just in the log; and let the user
  enter or paste a manual rotation (action names / Teamcraft macro format)
  per recipe that replaces the solver when set, with the same adaptive
  recovery on a bad condition.

- [ ] 7.10 Trade-request blacklist (S) — a trade request during automation is
  the usual "is that a bot?" poke. On an incoming trade (Trade addon / the
  chat notice) decline it, add the sender to the game blacklist automatically,
  log who and when, and optionally pause the run for a configurable settle
  time so the character does not carry on the instant the window closes.
  Settings: auto-blacklist on/off, pause-on-trade seconds. Same treatment
  worth considering for party invites and /tells from strangers (log only by
  default).

- [ ] 7.11 Per-activity food and potion (S–M) — today one FoodItemId/FoodIsHq
  pair serves everything and there is no medicine support. Split into a
  crafting set and a gathering set, each with food + potion (item, HQ
  preference), chosen from the inventory with a searchable picker that shows
  the buff. Maintenance re-applies whichever set matches the phase about to
  start (before a craft step, before a gather task), tracks the two buffs
  separately (Well Fed / Medicated), and never eats or drinks mid-node or
  mid-craft. Pre-flight warns when a chosen consumable is not in the bag.

- [ ] 7.12 Production breakdown (M) — Preview today is a flat list. Show the
  resolved graph as a tree: target → sub-crafts → raw materials, each node
  with crafts × yield, the job, and per-ingredient need / owned / missing;
  roll-ups per gathering zone and job (how many nodes, which teleports), CP
  and time estimates from recent solves, and which HQ materials will be
  consumed where. Same view live during a run with progress ticks per node,
  and as text in the report and via a "/cielcraft plan" command.

### Borrowed from Lisbeth (reviewed 2026-09-15)

- [ ] 7.13 Orders model (M) — replace the single target + queue with orders:
  per-order amount mode (Absolute / **Restock** = top the bag up to N),
  production mode (Any / Force HQ / Collectable / Quick Synth), "materials
  only" (skip the final craft), side orders, and order *groups* that run in
  sequence while orders inside a group are planned together (shared
  sub-crafts, one gather trip per material). Import/export as JSON and
  import from a Teamcraft list. Perpetual mode: restart the orders when done.
- [ ] 7.14 Gathering rotation engine (M) — today buffs are hard-coded (one
  yield buff, Solid Reason when it pays). Replace with conditional rotation
  tables per node class (normal / unspoiled yield / crystal / collectable):
  GP thresholds, Gatherer's Boon, the unspoiled bonus conditions (bonus
  yield / attempts / boon), Eureka Moment → Wise to the World, Twelve's
  Bounty / Giving Land for crystals; user overrides in the same format.
  Cordial types (HQ, watered, hi-cordial) and cooldown-at-node awareness.
- [ ] 7.15 Timed-node scheduler (M) — a Schedule view: upcoming unspoiled /
  legendary windows for every planned item, slots computed from GP
  regeneration, cordials and rotation cost, travel started early enough to
  be at the node when it pops; between windows do untimed work or wait at
  the home spot (ties into 7.6). Aetherial reduction of ephemerals for
  crystal clusters, with per-element crystal spot preferences.
- [ ] 7.16 Character capability model (S–M) — flying unlocked per zone,
  master-book recipes, tribe reputation ranks, GP-regen traits, read from
  the game where possible; source availability and the planner honour them
  (no fly-only nodes without flight, no locked recipes). Refresh on login.
- [ ] 7.17 Sourcing beyond gather/craft (L) — vendor purchase with a max gil
  cap (extends 7.3b), scrip / tomestone / Grand Company exchanges, a
  collectables planner that works out which turn-ins earn the scrips an
  order needs (cheapest or fastest), retainer inventory + ventures +
  storage rules, desynthesis and trash cleanup of unused byproducts.
- [ ] 7.18 Assist mode, craft test, lock-step (S–M) — Assist: auto-run any
  synthesis the user starts by hand. Craft Test: solve for chosen stats and
  recipe and show the rotation with expected progress/quality/HQ%% and solve
  time (extends 7.8). Lock-step: pause before every action for expensive
  crafts (fits the pause command).
- [ ] 7.19 Equipment set builder (M) — "make a full gear set for job X at
  level Y" as generated orders, with tradeable-only / rarity / tomestone /
  scrip / GC-seal switches; in-game optimizer auto-equip; mender fallback
  when self-repair is not possible (extends 7.3a).
- [ ] 7.20 Finish-and-idle behaviours (S) — random landing points near nodes
  (avoid stacking with other gatherers), "stop gently" (finish the current
  step then stop), go home / to the aetheryte when done, sound or
  text-to-speech on completion or error, exit the game when done, a debug
  mode that stops on unreadable results, and a first-run setup wizard
  (gearsets, flying, books, reputations).
- [ ] 7.21 Window layout (S) — sidebar navigation like Lisbeth's: Orders /
  Mode, Status (Progress, Crafting Steps, Schedule), Tools (Equipment),
  Settings (Character, General, Crafting, Gathering, ...); the diagnostic
  report and log as a Status page.

- [ ] 7.22 HQ-aware intermediates (M) — today every non-final step is quick
  synthesized (NQ) and the final craft solves from zero quality. For recipes
  whose quality cannot be filled from zero: solve the final recipe once at
  initial quality 0; if it caps short of the target, compute the HQ
  material mix that seeds enough initial quality (per-ingredient share of
  max quality by material value), re-solve to confirm, and mark those
  intermediate steps "craft normally at 100%%" while the rest stay quick.
  The final craft's HQ fill consumes them. Validate the initial-quality
  formula against a real recipe in game before trusting it.

- [ ] 7.23 Collectable crafting as a target option (S) — today collectables
  are only craftable by opening the log on the recipe and running a Batch.
  Make it a production mode on the target / order (ties into 7.13): pick
  the collectable, a count, and the collectability tier to hit (low / mid /
  high threshold, from the recipe data); the solver targets that tier and
  the run reports items per tier. Optional turn-in at the appraiser
  afterwards belongs to the scrips planner (7.17).

Cross-cutting for 2.0: the planner grows a "source" per missing material
(gather / fish / buy / hunt / stored-in-retainer), chosen by preference and
availability; the runner gets one phase per source. That is the point where
5.1 (state machines in Core) pays off, so 5.1 comes first.

## 2.0 plan (drafted 2026-09-15)

Sizes: S ≈ 1–2 days, M ≈ 3–5 days, L ≈ 1–2 weeks, XL ≈ 3+ weeks of focused
work. Milestones ship in order; each is releasable on its own. Cut the 1.0
tag first (the validated build from 2026-09-15) so 2.0 work happens on a
known base.

### M0 — Foundations (≈ 2 weeks) — do first, everything else builds on it
1. 5.1 State machines into Core + the review follow-ups: one TravelDriver
   (runner + gathering share mount/fly/land), shared Throttled/Retry helper,
   common status/transition base. The orders model, scheduler and sourcing
   all add phases; adding them to five hand-rolled machines is where bugs
   would come from.
2. 7.16 Character capability model (S–M): flight per zone, books,
   reputations, GP traits. The planner must know these before orders and
   sourcing start trusting it.
3. 7.10 Trade-request blacklist + 7.20 finish-and-idle behaviours (S + S):
   safety and looking-human items; cheap, and they protect every later run.
4. 7.7 Solution cache (S): free speed on repeated recipes; touches only the
   solver service.

### M1 — Daily use (≈ 4 weeks) — the things asked for while testing
5. 7.13 Orders model (M): amount modes (Restock), production modes, groups,
   materials-only, JSON + Teamcraft import. Replaces target + queue.
6. 7.21 Window layout (S): sidebar; orders/status/settings pages. Do with 5
   so the UI is built once around orders.
7. 7.1 Standalone gather target (S) incl. collectables; run tests D1/D3/D4.
8. 7.23 Collectable crafting as a target option (S) — a production mode on 5.
9. 7.11 Per-activity food and potion (S–M).
10. 7.6 Estate / home teleport for crafting (S).
11. 7.12 Production breakdown tree (M).
12. 7.8 Rotation visibility + manual rotations, and 7.18 assist / craft test /
    lock-step (S–M + S–M): share the rotation view; build together.

### M2 — Smarter gathering and crafting (≈ 3 weeks)
13. 7.14 Gathering rotation engine (M): conditional tables per node class.
14. 7.15 Timed-node scheduler (M): needs 13 for GP costing and 10 for
    waiting at home.
15. 7.22 HQ-aware intermediates (M): needs the solver to answer "reachable
    from zero?"; validate the initial-quality formula in game.
16. 7.2 Spiritbond / materia extraction (S–M).

### M3 — Beyond gather and craft (≈ 6 weeks)
17. 7.3 NPC interaction layer (M) → 7.3a mender repair (S) → 7.3b vendor
    purchases (M).
18. 7.17 Sourcing (L): scrip / tomestone / GC exchanges, collectables-for-
    scrips planner, retainer ventures + storage rules, desynth + trash
    cleanup. Depends on 17 and the orders model.
19. 7.19 Equipment set builder (M): generated orders; depends on 5 and 18.
20. 7.4 Fishing (L): its own controller; depends on 17 for spots/NPCs.

### M4 — Stretch
21. 7.5 Combat drops (XL): only after a combat plugin with stable IPC is
    confirmed; nothing else waits on it.

Rule of thumb for the order: safety and foundations first, then whatever
shortens a daily run, then whatever widens what a run can source.

## Review follow-ups (structural, deferred)

From the full-code review: extract the duplicated mount/fly travel logic
(ProductionRunner + GatheringController) into one TravelDriver; consolidate the
five per-class Throttled/RetryInterval copies into a shared helper; a common
status/transition base for the five state machines; move interference detection
below ProductionRunner so standalone batches/gathers are covered too.

## Testing

In-game regression checklist: `docs/TESTPLAN.md`. Diagnostics: `/cielcraft report`
(state of every layer + last 200 log lines, copied to the clipboard and saved
in the config folder); debug window **Log** tab; tick exceptions are caught and
logged once per 10 s per source instead of spamming every frame.

## Known validation-pending values

Values that could not be verified offline and are confirmed at in-game gates:
crafting-log callbacks (8 = synthesize, 9 = quick synthesis — validated),
general action ids (9 = mount roulette, 23 = dismount, 6 = repair — all validated 2026-09-15),
repair-addon callbacks (0 = repair all, -1 = close; SelectYesno 0 = yes — validated 2026-09-15),
HQ-fill mechanism: writing the RecipeNote amount spans does *not* register a
selection; the crafting log's own NQ/HQ fill buttons do, and the assignment
is verified from the selected recipe entry — validated 2026-09-15 (Titanium
Gold Shield crafted HQ from HQ ingots, initial quality 5100),
crafting-status step counts: they live in the status *parameter* (stacks), not
`RemainingTime`, which reads 0 — validated 2026-09-15 via the mid-craft re-solve
effects line (Inner Quiet 7 / Waste Not 4 / Manipulation 6 matched the icons),
Synthesis-window numbers arriving without thousands separators (the parser now
merges "12,345"/"12.345"/"12 345" groups either way).
