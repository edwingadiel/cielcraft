# CielCraft in-game test plan

Run through this after any change to the orchestration code, and before tagging
a release. Every test ends the same way: **if anything looks wrong, run
`/cielcraft report` immediately** (before pressing Stop or Resume, so the state
that went wrong is still in the report), then paste the report with the test
number. A report is also worth taking after a test that *passed* if something
looked slow or odd.

Before starting: open the debug window (`/cielcraft debug`), keep the **Log**
tab visible on a second monitor if you have one. Check the Overview tab shows
Raphael "Ready" and vnavmesh "Ready".

Legend: **Expect** = pass condition. **Watch** = what to look at in the debug
window. Tests marked ★ exercise code changed since the last validated build.

---

## A. Crafting basics

**A1. Single solve + run** — Open the crafting log on a mid-level recipe (one you
comfortably HQ), press Synthesize, Debug → Crafting → "Solve current craft", then
"Run rotation".
Expect: the craft completes HQ with no manual input. Watch: the solver status
line and the "Rotation:" log entry list the same actions that get executed.

**A2. Batch of 3, normal synthesis** — Crafting log on a recipe, main window →
Target → Batch ×3.
Expect: 3 crafts, count verified in inventory, batch state Completed. The
"Batch crafter" section of a report shows `Crafts 3/3`.

**A3. Batch with HQ materials (PreferHqMaterials on)** — Same recipe, own some
HQ ingredients.
Expect: the HQ counts are filled in before each synthesis; the solve log shows
`initial quality > 0`; the rotation is shorter than in A2.

**A4. Target quality 50%** — Settings → Crafting → target quality 50, batch ×2.
Expect: the plan stops adding quality around 50%; the craft still completes.
Set it back to 100 afterwards.

**A5 ★. Buff durations** — During any craft, use Innovation or Veneration
manually while the automator is paused (or before pressing Run rotation), then
look at Debug → Crafting → Buffs.
Expect: `2189 (4 steps)` counting down 4 → 3 → 2 → 1 in step with the number on
the buff icon. Inner Quiet shows `251x<stacks>` with no steps. **If the steps
number is missing or does not match the icon, report it: this is the one value
in this build that could not be verified offline.**

## B. Adaptive engine and recovery ★

**B1 ★. Quality-capped finisher** — Batch ×1 on an easy recipe with target
quality 100%. Once quality is capped mid-rotation, the engine should finish
with the cheapest sufficient synthesis instead of running the rest of the plan.
Expect: a `[Adaptive] quality target reached — finishing with …` log line and
the craft completes. If Veneration is up at that moment, the finisher chosen
may be cheaper than before (it now counts the buff).

**B2 ★. Mid-craft recovery (live-state solve)** — Batch ×1. After 3–4 actions,
Pause (main window). Manually use one *quality* action the plan did not expect
(e.g. Basic Touch), then Resume.
Expect: `[Craft] Paused` → the batch enters "Recovering: re-solving the
remaining craft…" → a `[Raphael] Mid-craft re-solve: … effects CraftLiveEffects {
InnerQuiet = N, … }` log line → a new rotation → the craft completes. Check the
logged `InnerQuiet` matches the buff stacks and `Manipulation`/`Innovation`
match the steps on the icons. Paste the report either way; the effects line is
what I need to confirm the mapping.

**B3 ★. Recovery with a one-shot spent** — Same as B2, but on a specialist job:
use Heart and Soul manually before resuming.
Expect: the re-solved rotation never contains Heart and Soul again
(`Live effects: … HeartAndSoulAvailable = False`).

**B4. Recovery limit** — Trigger B2, then deviate a second time after the
recovery rotation starts.
Expect: the batch pauses with a reason (one recovery per craft) instead of
looping. Resume should continue or pause safely, never spam actions.

**B5 ★. Final action in flight while paused** — Batch ×1; press Pause the
moment the last action of the rotation fires.
Expect: the craft completes and is counted; Resume (if still paused) does not
fire an extra synthesis.

## C. Pause / resume matrix

For each row: start the run, pause at the described moment, wait 5 s, resume.
Expect: the run continues from where it was; nothing is repeated or skipped;
the state in a report taken while paused shows the paused layer *and* the
layers below it consistent (e.g. Batch Paused + Automator Paused + Executor
Idle).

| # | Run | Pause moment |
|---|-----|--------------|
| C1 | Batch ×2 | mid-craft between two actions |
| C2 | Batch ×2 | while "Solving rotation…" is shown |
| C3 | Batch ×2 | between craft 1 and craft 2 (StartingCraft) |
| C4 | Batch ×2 quick synth (QuickSynthIntermediates on a production plan) | during the quick synthesis |
| C5 | Gather ×5 (debug Gathering tab) | while walking to a node |
| C6 | Gather ×5 | mid-swing (the moment after clicking the item) |
| C7 | Gather ×5 | between nodes (after a node is exhausted) |
| C8 | Run with a gathering step | while teleporting / on the loading screen |
| C9 | Run | during the job switch (PreparingStep) |

**C10 ★. Stop while queued** — Queue two targets, Run queue, then press Stop
everything during the first target. Expect: the queue holds, the first entry is
trimmed by what was produced (queue panel), and Run queue afterwards resumes
production of the remainder, not a fresh full quantity.

## D. Gathering

**D1. Gather ×10 of an untimed item** — Stand in the node area.
Expect: nodes are chained, yield buffs are used when GP allows, cordials are
drunk between nodes when GP is low, the loop completes with the inventory
count +10.

**D2. Node blacklist** — Start Gather ×10 on an item, then walk away from the
first node while the plugin approaches so it fails or despawns.
Expect: `blacklisting node, failure 1/5`, the loop moves to another node and
still completes.

**D3. Collectable gathering** — Gather ×2 of a collectable.
Expect: the collectable window is driven to the threshold and collected.

**D4. Timed node** — Plan an item whose node is timed and currently closed.
Expect: the runner shows "Waiting for window (…)" with a countdown and starts
moving when it opens.

## E. Production runner and queue

**E1. Full production run** — Main window → Target: an item with at least one
sub-craft and one gatherable raw material you do *not* have. Run (Preview shows the plan without starting).
Expect: gather → sub-craft (quick synth if enabled) → final craft → Completed
with the chat summary. Take a report at the end even on success; the runner
section shows the full plan and timings.

**E2. Resume from saved state** — During E1, `/xlplugins` → reload the plugin
mid-run (or log out and in).
Expect: the main window offers to resume; resuming continues at the right step.

**E3. Queue of two** — Queue two small targets, Run queue.
Expect: both produced in order, "Production queue complete." in chat, queue
empty. The in-flight row shows "● producing" and has no delete button.

**E4. Interference** — During a gather step, walk the character manually for a
few seconds.
Expect: the runner pauses with an interference reason instead of fighting you.

## F. Maintenance

**F1. Auto-repair** — Set the repair threshold above your current lowest gear
condition (e.g. 99%) and start a batch.
Expect: the repair window opens, repair-all fires, the window closes, the batch
proceeds. If the game instead shows the yes/no confirmation, it should be
answered. **Report if the repair window or the yes/no dialog stays open.**

**F2. Repair cancel mid-confirmation** — Same as F1, but press Stop everything
while the repair/yes-no window is up.
Expect: both windows close; nothing is left open.

**F3. Food** — Pick a food in Settings → Gathering/Crafting, start a batch with
no Well Fed buff.
Expect: the food is eaten first; with under 5 minutes left on the buff it is
re-eaten between crafts.

## G. Failure and stop paths

**G1. Emergency stop** — During any run, `/cielcraft stop`.
Expect: everything goes Idle within a second, navigation stops, no further
actions fire.

**G2. Out of materials** — Batch ×5 on a recipe you can only craft twice.
Expect: refused up front with the craftable count, not a mid-batch failure.

**G3. No gearset** — Plan an item whose sub-craft job has no gearset.
Expect: a clear pause/failure reason naming the job.

**G4 ★. Numbers with separators** — On a craft with progress or quality above
999, confirm Debug → Crafting shows e.g. `Progress: 1234 / 5000` (not `1`).

## H. Social guard ★ (roadmap 7.10; needs a second character or a friend)

**H1. Trade during a batch** — Start Batch ×3, have the other character send a
trade request.
Expect: within about a second the Trade window closes by itself, the batch
shows `Paused: trade request.`, and it resumes on its own 20 s later. Settings
→ Social lists the sender in the blacklist. Watch: the log has a `[Social]`
line naming the sender (`TradeRequest from Name@World: declined, blacklisted,
paused 20s`). If the name reads `unknown`, paste the report — the
`Trade window strings:` debug line shows what the window offered instead.

**H2. Trade from the same sender again** — Repeat H1 with the same character.
Expect: the window closes, the run does **not** pause (`declined, already
blacklisted`).

**H3. Tell and party invite** — While a batch runs, have the other character
/tell you and send a party invite.
Expect: nothing is answered; the run continues; both show up in the report's
"Social" section with sender and time. A tell from a party member must not be
listed.

## I. Finish-and-idle behaviours (roadmap 7.20)

**I1. Stop after step** — Run a two-step production (an intermediate plus the
final item, or a gather plus a craft). While step 1 runs, click **Stop after
step** (it reads `Finishing step…`).
Expect: step 1 completes, the run ends with `Stopped gently after step 1/2;
Resume continues from here.`, and the main window offers the saved
production's **Resume**. Resume must continue with step 2 only. Clicking the
button a second time before the step ends cancels the gentle stop.

**I2. Sound and speech** — Settings → Alerts: pick a sound effect, tick
*Read alerts aloud*, click **Test**.
Expect: the chosen `<se.N>` plays in game and Windows reads "CielCraft test
alert." Then run Batch ×1: the completion line in chat is accompanied by the
sound and the spoken summary; a paused/failed run triggers them too; plain
progress lines do not.

**I3. Setup checklist** — `/cielcraft setup`.
Expect: one line per crafting/gathering job with level and whether a gearset
is saved (red when a levelled job has none), vnavmesh status, flight zone
count, master book count and tribe count. **Refresh** re-reads; **Done —
don't show again** stops it opening on login.

**I4. Exit when done** ★ (ends the game session) — Settings → Alerts, tick
*Exit the game when the run and queue complete*; run Batch ×1 of something
quick.
Expect: about 5 s after the completion message the game sends `/shutdown`
and confirms the prompt; the game closes. With a failed or gently stopped
run nothing exits. Watch: the log has `[Finish] Exiting the game in 5s` then
`[Finish] Production and queue complete; exiting the game.`

**I5. Random landing** (with 7.1 / D-tests) — Fly to three nodes in a row.
Expect: each landing is a few yards to a different side of the node, never
on top of it, and the walk-up still opens the node.

## J. Orders (roadmap 7.13)

**J1. Migration** — With items still in the old queue, reload the plugin.
Expect: they appear as a group named `Queue` in the Orders panel (Absolute /
Any), the old queue is gone, and the report's saved-production line still
reads.

**J2. Group planned together** — One group: `Cobalt Tungsten Ingot ×2` and
another recipe that also uses a Tungsten-based intermediate (or any two items
sharing a sub-craft). Click **Preview**.
Expect: the shared intermediate appears once with the summed craft count;
raw materials are summed; each order shows its planned quantity.

**J3. Restock** — Order `Iron Ingot`, amount mode **Restock**, amount = (owned + 2).
Expect: Preview shows planned 2. Set amount ≤ owned: the order reads
`already stocked` and the group is skipped when run. Run orders: exactly 2
are crafted and the run completes.

**J4. Materials only** — Order an item with two craftable intermediates,
tick **Materials only**, Run orders.
Expect: the intermediates are crafted (and missing raws gathered), the final
item is never crafted, and the summary says so.

**J5. Force HQ** — Order ×2 of a recipe the character can HQ reliably, mode
**Force HQ**, with enough materials for at least four crafts.
Expect: every craft solves at 100 %; an NQ result is logged as not counting
and the batch keeps going until the HQ count has risen by 2; the run stops
with a clear message if materials run out first.

**J6. Quick synth mode** — Order ×3 of an intermediate the character has
crafted before, mode **Quick synth**.
Expect: the final step quick-synthesizes (no rotation), three items result.
Repeat with a never-crafted recipe: it falls back to a normal craft.

**J7. Groups in sequence, hold, perpetual** — Two groups (small orders). Run
orders; click **Hold** during group 1.
Expect: group 1 finishes, group 2 does not start, status says held; **Run
orders** again starts group 2. Tick **Perpetual** with a Restock order that
is already stocked: after the last group the book restarts, logs the cycle,
finds nothing to do and idles (no craft loop).

**J8. Import / export** — Export JSON to the clipboard, delete a group,
import JSON (append) → the group is back. Paste a Teamcraft "copy as text"
list (with Final items / Items / Crystals sections) and import.
Expect: one new group with the final items as orders; unresolved names
listed in red and skipped.

## K. Window layout (roadmap 7.21)

**K1. Pages** — `/cielcraft` opens the window on the last page; the Dalamud
cog lands on Settings › General (a second press closes); `/cielcraft debug`,
`setup`, `config` select Status › Debug, Tools › Character, Settings.
Expect: the sidebar unfolds only the active section; resize to 640×480 and
the Orders and Log pages still scroll inside the content area.

**K2. First run** — Clear `SetupCompleted` in the config and log in.
Expect: the window opens on Tools › Character; "Done — don't show again"
moves to Orders and the button disappears afterwards.

**K3. Live status** — Start a run. Expect: Orders shows one status line with
a "Progress ›" link; Status › Progress has pause / resume / stop / stop
after step and the materials table; Status › Report matches the clipboard
copy section by section.

## L. Production breakdown (roadmap 7.12)

**L1. Tree** — Orders › Preview a group, then Status › Breakdown.
Expect: targets expand to sub-crafts and raw leaves with counts; "Gathering
by zone" and "Crafts by job" match the preview's gather-first list and step
count; `/cielcraft plan` prints the same tree ("No plan." before any preview).

**L2. Live ticks** — Run orders. Expect: raw leaves show ▶ during gathering,
the current step ▶ n/m during a batch, completed steps ✓; the report's Plan
section carries the marks.

## M. Consumables (roadmap 7.11)

**M1. Migration** — With a pre-7.11 food configured, reload. Expect: the log
line about moving the food into both sets; Settings › Consumables shows it in
both Food slots; the legacy picker is empty.

**M2. Picker** — With HQ and NQ stacks of a meal and a crafting draught in
the bag, search in Crafting › Food and Potion. Expect: both stacks listed
with `(HQ ×n)` / `(NQ ×n)` and the buff summary; picking sets the item and
HQ; the row shows owned counts and "buff inactive".

**M3. Applied before a step** — Run a craft order with no buffs.
Expect: "Eating …" then "Drinking a potion…" in the status, Well Fed and
Medicated both up before the synthesis starts, remaining times counting down
in the panel; nothing is used while a synthesis or node window is open.

**M4. Gathering set and fallback** — Give the Gathering set a different food
and run a gather order: it is eaten before the first node. Choose HQ for a
food owned only as NQ: the log says the NQ one is used. Choose a food that
is not in the bag: the run pauses with "crafting food <name> is not in the
inventory"; clear the slot and resume.

## N. Gather orders, collectables, home (roadmap 7.1, 7.23, 7.6)

**N1 ★. Gather order** — Orders: search kind **Gather**, add an untimed ore
or log ×10, Preview.
Expect: `×10 · gather`, "Gather first:" lists it, no craft step. Run orders:
teleport / travel → node loop → `Completed: materials gathered.` and the
chat summary `gathered 10× <item>`. The report's target line reads
`Gather; mode Any`.

**N2. Gather restock and non-gatherable** — The same order in Restock with
amount = owned + 3 → planned 3; amount ≤ owned → `already stocked`. Switch a
craftable-only item's row to Kind Gather → Preview shows `not gatherable`.

**N3 ★. Collectable gather** — Gather order for a rarefied ore, Production
Collectable, Tier Mid, ×2. Run.
Expect: `[Gather] … as Mid collectables` in the log; at the node the
appraisal stops once the Mid threshold is reached and Collect fires; a
Low-only result on the last integrity point is not collected; the loop
completes at 2 with `collectables taken 2` in the report. Repeat with High.

**N4 ★. Collectable craft** — Craft order for a rarefied recipe ×2, mode
Collectable, tier Low; and a non-collectable recipe with mode Collectable.
Expect: the second reads `not a collectable recipe`; the first's Step line
says `Low collectable ≥ N collectability`, the report's Batch section shows
the target quality (N × 10), two collectables land in the bag, and the
chat summary names the tier. Check the turn-in window shows collectability
at or above the tier — the ×10 mapping is validated from data only.

**N5 ★. Home teleport** — Settings › Home: Estate Hall (with a house). Run a
craft-only order from a field zone.
Expect: `Heading to the estate hall before step 1/…`, `[Travel] Teleporting
home: estate hall, aetheryte …`, loading screen, `Home; preparing step 1`,
the craft runs there. Set Apartment without owning one: `No apartment
aetheryte to teleport to; crafting in place.` Set Inn Room: the run
teleports to the cheapest inn city and the log says the inn is not entered.

**N6. Home after gathering / refused cast** — With a home set, an order that
gathers first goes home after the last node instead of the zone aetheryte.
Start a craft-only order with the crafting log open: the log is closed
first; a refused cast retries every 2 s up to three times, then `… was
refused 3 times; crafting in place.`

## O. Rotations, assist, lock-step, craft test (roadmap 7.8, 7.18)

**O1 ★. Rotation view** — Batch ×1 on a mid-level recipe; Status › Crafting
Steps.
Expect: Source "solved" (or "cached solve"), an Expected line with progress,
quality and HQ %, ✓ on executed rows, ▶ on the one in flight, per-step
progress / quality / durability / CP; Normal-condition rows match the
Synthesis window exactly, Good / Excellent steps land higher.

**O2 ★. Manual rotation** — With the crafting log on a recipe you HQ easily,
paste Teamcraft macro lines (or "Use current" after O1) into Manual
rotation, Save, Batch ×1.
Expect: `[Production] Manual rotation for recipe N: k actions …; skipping
the solve.`, no `[Raphael] Solve requested`, source "manual rotation".
Clear → the next batch solves again. A line with an unknown action
disables Save and names the line; hand-edited garbage in the config falls
back to the solver with `… ignored (line …); solving instead.`

**O3. Manual rotation recovery** — O2, Pause after three actions, use one
quality action by hand, Resume: the B2 flow (`Mid-craft re-solve`), source
"mid-craft re-solve".

**O4 ★. Lock-step** — Enable Lock-step, Batch ×1.
Expect: `[Craft] Paused: lock-step, next Muscle Memory (1/n).`; Step (or
`/cielcraft step`, or Resume) fires exactly one action and holds again;
Continue turns the setting off and the rest runs; no "Recovering:
re-solving" while held.

**O5 ★. Assist mode** — Enable Assist, nothing running, press Synthesize by
hand.
Expect: `[Production] Assist: attaching to the synthesis started by hand.`
on step 1 and the craft completes; a second hand craft is assisted again; a
craft cancelled mid-way leaves the batch Idle; a craft on which you already
used an action (step 2) and a quick synthesis are not adopted; Run orders
with assist on shows no "Assist:" lines.

**O6 ★. Craft Test** — Tools › Craft Test: search a recipe, "Use my stats",
Solve.
Expect: solve time (a second Solve says cached), an Expected line with HQ %,
a per-step table; "Copy macro" pastes valid Teamcraft lines; "Save as manual
rotation" then Batch ×1 follows O2. A batch running at the same time keeps
its own `[Raphael]` lines.

---

## What to paste

1. The test number and one line of what you saw.
2. The report from `/cielcraft report` taken *at the moment it went wrong*.
3. If the game showed an error toast or a window stayed open, say which.

The report's **Log** section lists every transition of every layer with UTC
timestamps, so I can usually find the cause without further questions. If the
plugin itself threw, the log will contain an `Unhandled exception in tick`
entry with the stack trace.

## Validation log

**2026-09-15, late (orders model smoke run).** Added a Cobalt Tungsten Ingot order, Preview, Run orders: the group planned and the batch started, which surfaced and fixed three crafting bugs (ingredient assignment read the previous craft, first action of a craft resolving after the 6 s timeout, Groundwork wrongly halved under Waste Not). The craft finished HQ through the new attach-to-craft path; "Resume" on the saved production correctly reported it already complete. J1–J8 still to run properly.

**2026-09-15 (Windows, Dalamud 15.0.3.4, first in-game run).** Passed: A1, A2,
A3, B1, B2, B5, C1, C5, D2, E1, E2, E4, F1, F3, G1, G4, plus a four-material
plan across four zones, the quick-synthesis fallback for a never-crafted
recipe, and HQ-material assignment through the crafting log's fill buttons.
Deferred as unlikely in practice: A4, A5, B3, B4, C2–C4, C6–C10, D1, D3, D4,
E3, F2, G2, G3.
