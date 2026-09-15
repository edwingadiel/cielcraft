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

**2026-09-15 (Windows, Dalamud 15.0.3.4, first in-game run).** Passed: A1, A2,
A3, B1, B2, B5, C1, C5, D2, E1, E2, E4, F1, F3, G1, G4, plus a four-material
plan across four zones, the quick-synthesis fallback for a never-crafted
recipe, and HQ-material assignment through the crafting log's fill buttons.
Deferred as unlikely in practice: A4, A5, B3, B4, C2–C4, C6–C10, D1, D3, D4,
E3, F2, G2, G3.
