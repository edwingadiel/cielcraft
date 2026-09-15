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

- [x] 5.1 State machines on Core seams (L) — done 2026-09-15 in two halves.
  Seams in Core: `ILog`, `IClock`, `IUserNotifier` (with notification
  kinds), `IActionResolver`, `Throttle`, `AutomationMachine<TState>` (state,
  status, logged transitions, tick-safe, `OnTransitioned` hook, `Describe`)
  and `TravelDriver` (mount/fly/land/walk-up, random landing spot). Every
  machine (CraftStateMonitor, ActionExecutor, CraftAutomator, BatchCrafter,
  GatheringController, GatheringLoop, ProductionRunner, ProductionQueue,
  MaintenanceService, SocialGuard core) now derives from the base or reads the
  seams, and one `FrameworkDriver` ticks them in the original order. The
  runner and the controller hand their legs to the shared TravelDriver, so the
  duplicated mount/fly/land logic is gone. `Configuration` derives from
  `AutomationSettings`, the Dalamud-free settings the machines read.
- [ ] 5.4 Machines into Core with a fake game bridge (M) — the remaining half
  of 5.1: move `IGameBridge` and the machines out of the plugin assembly and
  write offline scenario tests (a batch of three with a verification lag, a
  gather run across two nodes, a production with a replan) against a scripted
  bridge. Blocked only on the Dalamud types the bridge interface still
  mentions (addon names, condition flags) and `Configuration.Save`.
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

- [x] 7.1 Standalone gather target (S) — done 2026-09-15 as an order kind:
  an order with Kind = Gather plans as a raw material (teleport, travel,
  timed windows, node loop; no craft), Restock applies, the search finds
  gatherable items; Mode = Collectable gathers collectables at the order's
  tier (the appraisal goal is the tier's collectability, taken from the
  gathering window). First in-game gather order ran on 2026-09-15 (Iron
  Ore ×3); D1/D3/D4 still to be run as written.
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

- [x] 7.6 Teleport to the estate / home point for crafting (S) — done 2026-09-15:
  Settings › Home picks Stay / Estate Hall / Apartment / Inn Room; the runner
  teleports there before the first craft step (after gathering instead of
  the zone-aetheryte return), waits for the loading screen, retries a
  refused cast three times, and crafts in place with a log line when no
  such aetheryte exists. Inn Room teleports to the nearest inn city (the
  inn itself is not entered).
- [x] 7.7 Solution cache (S) — done 2026-09-15 (M0); — the solver runs on every craft even when the
  same recipe repeats with the same stats, buffs and initial quality. Key a
  cache on the full CraftSetup + CraftObjective (stats, CP, food, specialist
  flags, target/initial quality) and reuse the action list within a batch and
  across sessions (persist in the config folder); invalidate on any key
  change. Mid-craft recovery solves stay uncached (live state is unique).

- [x] 7.9 Pre-flight gearset check (S) — refuse to start a plan whose craft or
  gather jobs have no gearset, naming the job, instead of failing 15s into
  the step. Done 2026-09-14.

- [x] 7.8 Rotation visibility and manual rotations (S–M) — done 2026-09-15:
  Status › Crafting Steps shows the active rotation (solved / cached /
  manual / mid-craft re-solve) with executed / current / pending marks and
  the expected progress, quality and HQ chance per step from a
  Normal-condition replay (`Core/Rotations/RotationSimulator`, the HQ table
  in `HqChance`); manual rotations per recipe (action names or Teamcraft
  macro text, `RotationText`) replace the solve and keep the adaptive
  recovery.
- [x] 7.10 Trade-request blacklist (S) — a trade request during automation is
  the usual "is that a bot?" poke. On an incoming trade (Trade addon / the
  chat notice) decline it, add the sender to the game blacklist automatically,
  log who and when, and optionally pause the run for a configurable settle
  time so the character does not carry on the instant the window closes.
  Settings: auto-blacklist on/off, pause-on-trade seconds. Same treatment
  worth considering for party invites and /tells from strangers (log only by
  default).
  Done as `SocialGuard` + `SocialGuardCore`: detection is the Trade window
  becoming visible, the name comes from LogMessage row 34 (fallback: the
  window's own strings), decline is the addon's cancel callback, and the
  settle pause (default 20 s) auto-resumes only a guard-made pause. The
  game blacklist is read-only through ClientStructs (InfoProxyBlacklist has
  no add; AgentBlacklist events are undocumented), so the blacklist is
  plugin-side in the config; repeat senders are declined without a second
  pause. Party/FC invites and tells from strangers are logged only. Report
  gets a "Social" section. In-game checks: test plan H1–H3.

- [x] 7.11 Per-activity food and potion (S–M) — done 2026-09-15: crafting and
  gathering consumable sets (food + potion, HQ preference) in
  `AutomationSettings`; `MaintenanceService` keeps up the set of the activity
  the runner announces (`PrepareFor`) before each craft step or gather task,
  tracking Well Fed and Medicated separately, never mid-node or mid-craft,
  NQ/HQ fallback when only the other quality is owned, and a `BlockedReason`
  naming a chosen item that is not in the bag. Settings › Consumables picks
  from the inventory (buff summary from ItemFood). The single pre-7.11 food
  migrates into both sets on load.
- [x] 7.12 Production breakdown (M) — done 2026-09-15: `Core/Planning/PlanTree`
  builds target → sub-craft → raw-material nodes (crafts × yield, job, need /
  owned / missing) with roll-ups per gathering zone, per job and HQ
  materials consumed; Status › Breakdown draws it with live ticks during a
  run (✓ / ▶ n/m / ·), `/cielcraft plan` and the report's Plan section render
  the same text. Not done: CP and time estimates from recent solves (no
  solve history exists yet; folded into 7.8's rotation view later).
- [x] 7.13 Orders model (M) — done 2026-09-15 (design: docs/design/orders.md).
  Order book of groups run in sequence; orders in a group are planned as one
  graph (`DependencyResolver.Resolve(targets)`: shared sub-crafts merged,
  stock consumed once, owned stock of an ordered item reserved, materials-only
  expands ingredients without the final craft). Amount modes Absolute /
  Restock (amount − owned, "already stocked" skips), production modes Any /
  Force HQ (100 % solve, HQ fill, counts HQ gain only) / Quick synth (falls
  back when refused) / Collectable (refused until 7.23). `OrderRunner`
  replaces the queue: Run orders / Hold / Stop, held on failure or pause,
  perpetual restart with a cycle count; `/cielcraft run` and `hold`. Saved
  production keeps one entry per target. JSON export/import and Teamcraft
  "copy as text" import via the clipboard. The old queue migrates into a
  "Queue" group on load. Side orders were folded into groups (an extra order
  in the same group is planned together with the rest).
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
- [x] 7.16 Character capability model (S–M) — flying unlocked per zone,
  master-book recipes, tribe reputation ranks, GP-regen traits, read from
  the game where possible; source availability and the planner honour them
  (no fly-only nodes without flight, no locked recipes). Refresh on login.
  Done 2026-09-15: CharacterCapabilities snapshot (Core) + CapabilityReader
  (PlayerState aether-current sets, secret recipe books, tribe ranks, Trait
  sheet + quest completion, ClassJobLevels); locked-book recipes are planned
  around and refused at start with the book's name; fly decisions and
  FindLocation prefer zones with flight; Capabilities section in the report
  and the debug Overview. Tribe-rank index semantics still to confirm in game.
- [ ] 7.17 Sourcing beyond gather/craft (L) — vendor purchase with a max gil
  cap (extends 7.3b), scrip / tomestone / Grand Company exchanges, a
  collectables planner that works out which turn-ins earn the scrips an
  order needs (cheapest or fastest), retainer inventory + ventures +
  storage rules, desynthesis and trash cleanup of unused byproducts.
- [x] 7.18 Assist mode, craft test, lock-step (S–M) — done 2026-09-15: Assist
  attaches a batch to a synthesis started by hand on step 1; Craft Test
  (Tools) solves for chosen stats and a recipe on a detached solver and
  shows the rotation, expected outcome, HQ % and solve time, with "copy
  macro" and "save as manual rotation"; Lock-step pauses before every action
  (Step / Continue buttons, `/cielcraft step`).

- [ ] 7.22 HQ-aware intermediates (M) — today every non-final step is quick
  synthesized (NQ) and the final craft solves from zero quality. For recipes
  whose quality cannot be filled from zero: solve the final recipe once at
  initial quality 0; if it caps short of the target, compute the HQ
  material mix that seeds enough initial quality (per-ingredient share of
  max quality by material value), re-solve to confirm, and mark those
  intermediate steps "craft normally at 100%%" while the rest stay quick.
  The final craft's HQ fill consumes them. Validate the initial-quality
  formula against a real recipe in game before trusting it.

- [x] 7.23 Collectable crafting as a target option (S) — done 2026-09-15: an
  order with Mode = Collectable and a tier (Low / Mid / High); thresholds
  from CollectablesShopRefine / SatisfactionSupply (quality = collectability
  × 10), the solve targets the tier, the summary reports the count and the
  targeted quality. Not covered: Ishgard Restoration items (no threshold
  data → solves for the configured quality with a warning). Turn-in stays
  with the scrips planner (7.17).

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
1. (done, remainder tracked as 5.4) 5.1 State machines into Core + the review follow-ups: one TravelDriver
   (runner + gathering share mount/fly/land), shared Throttled/Retry helper,
   common status/transition base. The orders model, scheduler and sourcing
   all add phases; adding them to five hand-rolled machines is where bugs
   would come from.
2. (done) 7.16 Character capability model (S–M): flight per zone, books,
   reputations, GP traits. The planner must know these before orders and
   sourcing start trusting it.
3. (done) 7.10 Trade-request blacklist + 7.20 finish-and-idle behaviours (S + S):
   safety and looking-human items; cheap, and they protect every later run.
4. (done) 7.7 Solution cache (S): free speed on repeated recipes; touches only the
   solver service.

### M1 — Daily use (≈ 4 weeks) — the things asked for while testing
5. (done) 7.13 Orders model (M): amount modes (Restock), production modes, groups,
   materials-only, JSON + Teamcraft import. Replaces target + queue.
6. (done) 7.21 Window layout (S): sidebar; orders/status/settings pages. Do with 5
   so the UI is built once around orders.
7. (done) 7.1 Standalone gather target (S) incl. collectables; run tests D1/D3/D4.
8. (done) 7.23 Collectable crafting as a target option (S) — a production mode on 5.
9. (done) 7.11 Per-activity food and potion (S–M).
10. (done) 7.6 Estate / home teleport for crafting (S).
11. (done) 7.12 Production breakdown tree (M).
12. (done) 7.8 Rotation visibility + manual rotations, and 7.18 assist / craft test /
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

From the full-code review. Done under 5.1 (2026-09-15): one TravelDriver for
the runner and the controller, the shared Throttle, and the AutomationMachine
base. Still open: move interference detection (manual movement = user took
over) below ProductionRunner so standalone batches/gathers are covered too.

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
