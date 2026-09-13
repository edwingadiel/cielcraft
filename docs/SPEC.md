# CielCraft
## FFXIV Intelligent Crafting & Gathering Automation
### Master Product & Technical Specification — Version 2

---

# 1. Product Vision

CielCraft is a specialized Final Fantasy XIV automation system focused on one primary goal:

> **The user specifies an item and quantity. CielCraft figures out how to produce it.**

Example:

```text
Make 50 × Item X
```

CielCraft should determine:

- what the player already owns
- which ingredients are required
- which intermediate/sub-recipes must be crafted
- how many of each sub-recipe are required
- which raw materials are missing
- which missing materials can be gathered
- where those materials can be gathered
- the correct production order
- appropriate crafting rotations
- when a live crafting condition justifies deviating from the planned rotation
- when gathering is necessary
- when crafting can resume

CielCraft then executes the production plan.

The desired long-term experience is:

```text
User
 ↓
"Make 50 Item X"
 ↓
CielCraft
 ↓
Plan
 ↓
Gather
 ↓
Craft subcomponents
 ↓
Craft final items
 ↓
Verify completion
```

The user should not have to manually construct the production chain.

---

# 2. Revised Scope

CielCraft is now explicitly a:

> **Dalamud plugin for intelligent crafting production and supporting gathering.**

The previous requirement that CielCraft operate independently of Dalamud has been REMOVED.

Do NOT build:

- a custom injector
- a custom FFXIV runtime
- a custom plugin framework
- custom process bootstrap infrastructure
- IPC between an external application and injected DLL
- a replacement for Dalamud
- a replacement for vnavmesh

Use existing infrastructure wherever appropriate so development effort remains focused on CielCraft's actual product.

---

# 3. Core Dependencies

CielCraft should use three major existing technologies.

## Dalamud

Purpose:

```text
FFXIV integration
Plugin lifecycle
Game services
Game data
Native/game interop where required
UI
IPC with other plugins
```

CielCraft itself runs as a Dalamud plugin.

## Raphael

Repository:

```text
KonaeAkira/raphael-rs
```

Purpose:

```text
Craft simulation
Baseline crafting solver
Rotation generation
```

Relevant components:

```text
raphael-sim
raphael-solver
```

Raphael provides the initial deterministic crafting intelligence.

CielCraft should architect Raphael as a solver provider rather than permanently coupling the entire application to it.

## vnavmesh / ffxiv_navmesh

Repository:

```text
awgil/ffxiv_navmesh
```

Purpose:

```text
Navigation mesh
Pathfinding
Path execution
Movement
Ground navigation
Flying navigation where supported
```

CielCraft should communicate with vnavmesh through its Dalamud IPC API.

CielCraft should NOT initially implement its own navigation mesh or pathfinding engine.

---

# 4. Product Boundary

CielCraft is NOT intended to become a general-purpose FFXIV bot.

Primary scope:

```text
Crafting
Production planning
Sub-recipe crafting
Inventory awareness
Gathering materials required for production
Navigation required for gathering
Travel required for production
```

Explicitly out of scope:

```text
Combat automation
Dungeon automation
PvP
Quest automation
MSQ
Leveling automation
FATE farming
Cosmic Exploration
General gathering unrelated to production goals
Market-board botting
General-purpose bot profiles
Anti-detection functionality
Detection evasion
Anti-cheat circumvention
```

If functionality does not materially help:

```text
Acquire materials
or
Craft items
```

it probably does not belong in CielCraft.

---

# 5. High-Level Architecture

```text
                  CielCraft
                      │
     ┌────────────────┼─────────────────┐
     │                │                 │
     ▼                ▼                 ▼
 Production        Crafting          Gathering
   Engine           Engine            Engine
     │                │                 │
     ▼                ▼                 ▼
 Dependency        Adaptive         Gathering
 Resolver          Controller       Controller
     │                │                 │
     ▼                ▼                 ▼
 Inventory          Raphael        Navigation
 Resolver            │              Provider
     │                │                 │
     └────────────┬───┴────────────┬────┘
                  │                │
                  ▼                ▼
               Dalamud        vnavmesh IPC
                  │                │
                  └───────┬────────┘
                          ▼
                         FFXIV
```

---

# 6. Architectural Rule: Providers

External functionality should be hidden behind interfaces.

CielCraft's business logic should not directly depend everywhere on:

```text
Dalamud
Raphael
vnavmesh
```

Instead use provider abstractions.

Examples:

```text
IGameBridge
ICraftSolver
INavigationProvider
IRecipeProvider
IInventoryProvider
IGatheringProvider
```

Implementations:

```text
IGameBridge
 └── DalamudGameBridge

ICraftSolver
 ├── RaphaelSolver
 └── CielCraftAdaptiveSolver

INavigationProvider
 └── VNavmeshProvider
```

This allows components to be replaced later without rewriting CielCraft.

---

# 7. Suggested Solution Structure

```text
CielCraft/
│
├── CielCraft.Plugin/
│   ├── Plugin.cs
│   ├── Commands/
│   └── Dalamud/
│
├── CielCraft.Core/
│   ├── Models/
│   ├── State/
│   └── Common/
│
├── CielCraft.Game/
│   ├── IGameBridge.cs
│   └── DalamudGameBridge.cs
│
├── CielCraft.Crafting/
│   ├── CraftState.cs
│   ├── CraftController.cs
│   ├── CraftStateMachine.cs
│   ├── ActionExecutor.cs
│   └── Adaptive/
│
├── CielCraft.Raphael/
│   ├── ICraftSolver.cs
│   ├── RaphaelSolver.cs
│   └── RaphaelInterop/
│
├── CielCraft.Recipes/
│   ├── Recipe.cs
│   ├── RecipeProvider.cs
│   └── DependencyResolver.cs
│
├── CielCraft.Inventory/
│   ├── InventoryProvider.cs
│   └── InventoryResolver.cs
│
├── CielCraft.Production/
│   ├── ProductionPlanner.cs
│   ├── ProductionJob.cs
│   ├── JobScheduler.cs
│   └── Jobs/
│
├── CielCraft.Gathering/
│   ├── GatheringController.cs
│   ├── GatheringPlanner.cs
│   ├── GatheringNode.cs
│   └── GatheringDatabase.cs
│
├── CielCraft.Navigation/
│   ├── INavigationProvider.cs
│   └── VNavmeshProvider.cs
│
├── CielCraft.UI/
│   ├── MainWindow.cs
│   ├── ProductionWindow.cs
│   └── DebugWindow.cs
│
└── CielCraft.Tests/
```

A single project may be used initially if that speeds development.

Logical separation matters more than physical project count during the POC.

---

# 8. Game Bridge

Create:

```csharp
public interface IGameBridge
{
    PlayerState GetPlayerState();

    CraftState? GetCraftState();

    InventoryState GetInventory();

    GatheringState? GetGatheringState();

    bool ExecuteCraftAction(uint actionId);

    bool Interact(ulong objectId);
}
```

`DalamudGameBridge` implements this interface.

Use supported Dalamud services and game APIs where possible.

Avoid unnecessary low-level native integration when Dalamud already exposes the required information.

---

# 9. Initial Game State

CielCraft should identify:

```text
Character logged in
Character name
Current territory
Current position
Current crafting/gathering job
Current job level
Craftsmanship
Control
Current CP
Maximum CP
Current target
Whether crafting is active
Whether gathering is active
```

---

# 10. Craft State

Required state:

```text
Recipe
Progress
Maximum Progress
Quality
Maximum Quality
Durability
Maximum Durability
CP
Maximum CP
Step
Condition
Active crafting buffs
Remaining buff durations
```

Condition support should account for applicable FFXIV crafting conditions.

Examples:

```text
Normal
Good
Excellent
Poor
Centered
Sturdy
Pliant
Malleable
Primed
Good Omen
```

---

# 11. Craft State Machine

Never implement crafting as:

```text
Press action
Sleep
Press next action
Sleep
```

Use observable state transitions.

Suggested states:

```text
Idle
RecipeSelected
Preparing
Starting
Crafting
ActionRequested
ActionResolving
Completed
Failed
Paused
Recovery
```

Workflow:

```text
Crafting
 ↓
Choose action
 ↓
Request action
 ↓
ActionRequested
 ↓
Observe game-state transition
 ↓
ActionResolving
 ↓
Update CraftState
 ↓
Crafting
```

The next action should only execute when CielCraft knows the previous action resolved.

---

# 12. Action Execution

Provide a normalized method:

```text
ExecuteCraftAction(actionId)
```

Before execution verify:

```text
Craft active
Correct state
Action available
Enough CP
Craft not complete
No previous action pending
```

After execution verify:

```text
Step changed
or
Craft completed
or
Action failed
or
Timeout/error occurred
```

---

# 13. Raphael Integration

Raphael supplies the initial static solution.

Input:

```text
Recipe
Craftsmanship
Control
CP
Specialist status
Starting quality
Available actions
Relevant buffs
Target quality
```

Output:

```text
Ordered CraftAction[]
```

Example:

```text
Muscle Memory
Veneration
Manipulation
Groundwork
Innovation
Preparatory Touch
Preparatory Touch
Great Strides
Byregot's Blessing
Groundwork
```

---

# 14. Solver Abstraction

Create:

```csharp
public interface ICraftSolver
{
    CraftSolution Solve(
        CraftInitialState state,
        Recipe recipe,
        CraftObjective objective);
}
```

Initial implementation:

```text
RaphaelSolver
```

Future:

```text
CielCraftAdaptiveSolver
```

Do not design CielCraft under the assumption that a Raphael rotation must always be followed blindly.

---

# 15. Adaptive Craft Controller

Raphael should eventually represent:

> **The baseline plan.**

CielCraft owns the live execution strategy.

At every resolved craft step evaluate:

```text
Current state
Current condition
Remaining Raphael plan
Current quality
Target quality
Current progress
Remaining progress
Current durability
Current CP
Active buffs
Remaining buff durations
```

Then choose whether to:

```text
Continue Raphael
Modify next action
Skip actions
Re-solve remaining craft
Switch to completion strategy
```

---

# 16. Initial Adaptive Rules

## Quality Target Reached

If:

```text
quality >= desired quality
```

stop executing unnecessary quality actions.

Find fastest safe synthesis completion.

## Excellent

When:

```text
Condition = Excellent
```

evaluate whether an immediate quality opportunity is superior to following the static rotation.

## Good

When:

```text
Condition = Good
```

evaluate condition-dependent opportunities.

## Unexpected State

If:

```text
CP differs
Durability differs
Progress differs
Quality differs
```

from expected simulation:

```text
re-evaluate remaining plan
```

---

# 17. Long-Term Adaptive Solver

Eventually CielCraft should support:

```text
GetNextAction(
    currentCraftState,
    recipe,
    objective)
```

instead of requiring an entire predetermined rotation.

Long-term goal:

```text
Observe
 ↓
Evaluate
 ↓
Choose best action
 ↓
Execute
 ↓
Observe
```

This is one of the primary ways CielCraft can eventually go beyond a simple Raphael executor.

---

# 18. Batch Crafting

User selects:

```text
Item X
Quantity: 50
```

CielCraft should:

```text
verify materials
generate/obtain solution
start craft
execute craft
verify completion
repeat
stop when target quantity reached
```

Never assume every craft succeeded.

Verify inventory/output state.

---

# 19. Recipe System

Represent recipes with:

```text
Recipe ID
Result Item ID
Result Quantity
Crafting job
Required level
Difficulty
Quality
Durability
Ingredients
Ingredient quantities
Crystals
Recipe metadata
```

Use FFXIV/Dalamud game data where practical rather than manually maintaining redundant static data.

---

# 20. Dependency Resolver

Given:

```text
Final Item ×50
```

recursively expand craftable ingredients.

Example:

```text
Final Item ×50
│
├── Component A ×100
│    ├── Ingot ×200
│    │    └── Ore ×800
│    └── Resin ×100
│
└── Component B ×50
     └── Lumber ×150
          └── Logs ×450
```

Account for recipe output quantity.

Example:

```text
Need 10 Ingots
Recipe produces 3
Required crafts:
ceil(10 / 3) = 4
```

---

# 21. Dependency Graph

Represent production dependencies as a directed acyclic graph where possible.

Example:

```text
Ore
 ↓
Ingot
 ↓
Component
 ↓
Final Item
```

This enables:

```text
correct production order
inventory subtraction
job scheduling
replanning
progress tracking
```

---

# 22. Inventory Resolver

Before creating production jobs:

```text
Required resources
-
Resources already owned
=
Resources still required
```

Example:

```text
Required:
800 Ore

Owned:
613 Ore

Missing:
187 Ore
```

Initially support player inventory.

Expand later to other accessible storage if feasible.

---

# 23. Production Planner

User request:

```text
50 × Final Item
```

may become:

```text
Gather:
187 Ore
92 Logs

Craft:
67 Ingots
31 Lumber
100 Components
50 Final Items
```

The production planner determines:

```text
what
how much
in what order
```

---

# 24. Production Jobs

Use explicit job types.

```text
ProductionJob
│
├── CraftJob
├── GatherJob
├── TravelJob
├── WaitJob
├── InteractionJob
└── RecoveryJob
```

States:

```text
Queued
Preparing
Running
Paused
Blocked
Completed
Failed
Cancelled
```

---

# 25. Job Scheduler

The scheduler is the orchestration brain.

Example:

```text
1 Gather Ore ×187
2 Gather Logs ×92
3 Craft Ingots ×67
4 Craft Lumber ×31
5 Craft Components ×100
6 Craft Final Item ×50
```

The scheduler should execute only jobs whose dependencies are satisfied.

---

# 26. Dynamic Replanning

Recalculate when:

```text
Inventory changes
Craft fails
Gathering yield differs
User manually changes inventory
Materials become unavailable
Required quantity changes
Unexpected output occurs
```

Completed work should not be discarded.

---

# 27. Navigation — Revised Scope

CielCraft should NOT initially implement:

```text
NavMesh generation
Pathfinding algorithms
Path following
Collision avoidance
Ground movement engine
Flight movement engine
```

Use:

```text
vnavmesh
```

through Dalamud IPC.

---

# 28. Navigation Provider

Create:

```csharp
public interface INavigationProvider
{
    bool IsReady { get; }

    bool IsMoving { get; }

    Task<bool> MoveTo(
        Vector3 destination,
        bool fly);

    Task<bool> MoveCloseTo(
        Vector3 destination,
        float tolerance,
        bool fly);

    void Stop();
}
```

Initial implementation:

```text
VNavmeshProvider
```

---

# 29. vnavmesh Integration

Relevant vnavmesh IPC functionality includes capabilities such as:

```text
Nav.IsReady
Nav.Pathfind
Nav.PathfindWithTolerance
Path.MoveTo
Path.Stop
Path.IsRunning
Path.NumWaypoints
SimpleMove.PathfindAndMoveTo
SimpleMove.PathfindAndMoveCloseTo
```

Prefer high-level movement calls initially.

Conceptually:

```text
destination
 ↓
SimpleMove.PathfindAndMoveTo
 ↓
vnavmesh calculates route
 ↓
vnavmesh executes movement
 ↓
CielCraft monitors arrival
```

---

# 30. Navigation Responsibility Boundary

vnavmesh owns:

```text
Navigation mesh
Pathfinding
Waypoint traversal
Movement execution
Low-level navigation behavior
```

CielCraft owns:

```text
Where to go
Why to go there
Which gathering node is desired
When navigation begins
When navigation should stop
What happens upon arrival
What to do if destination cannot be reached
```

This distinction is important.

---

# 31. Navigation Dependency Handling

At startup:

```text
Check vnavmesh IPC availability
```

If unavailable:

```text
Crafting functionality remains available.
Automatic gathering/navigation functionality becomes unavailable.
Display clear status.
```

Example:

```text
Crafting: Ready
Raphael: Ready
Navigation: vnavmesh unavailable
Gathering automation: Disabled
```

Do NOT make CielCraft entirely unusable because vnavmesh is missing.

---

# 32. Gathering System

Gathering exists to support production.

Example:

```text
Need 187 Ore
 ↓
Inventory insufficient
 ↓
Ore is gatherable
 ↓
Create GatherJob
 ↓
Navigate to node
 ↓
Gather Ore
 ↓
Update inventory
 ↓
Continue production
```

Initial support:

```text
Miner
Botanist
```

Fishing may be deferred.

---

# 33. Gathering Data

For gatherable materials determine:

```text
Item
Gathering job
Required level
Territory
Node type
Node locations
Timed status
Eorzea Time window
Folklore requirements
Other gathering requirements
```

Use available FFXIV game data where possible.

Avoid maintaining data manually when reliable structured game data already exists.

---

# 34. Gathering Controller

Workflow:

```text
Select GatherJob
 ↓
Determine appropriate node
 ↓
Navigate to node area using vnavmesh
 ↓
Detect gathering objects
 ↓
Choose appropriate node
 ↓
Move close enough
 ↓
Target node
 ↓
Interact
 ↓
Read gathering UI
 ↓
Locate desired item
 ↓
Gather
 ↓
Verify inventory increase
 ↓
Repeat
```

---

# 35. Node Selection

The gathering controller should distinguish between:

```text
Navigation destination
Actual gathering object
```

The production database may know:

```text
"Item X is gathered around location Y."
```

Upon arrival CielCraft should identify currently spawned valid gathering nodes and select one.

Do not require perfectly fixed coordinates for every individual spawned object if runtime discovery is possible.

---

# 36. Gathering Loop

Example:

```text
Need:
50 Ore

Current:
0

Gather node
+5

Remaining:
45

Find next valid node

Navigate

Gather

...

Remaining:
0

GatherJob completed.
```

---

# 37. Gathering Actions

Initial goal:

> Gather the requested item reliably.

Optimization can come later.

Future gathering intelligence may optimize:

```text
GP usage
Yield actions
Gathering attempts
Boon probability
Collectability
Timed nodes
Node rotation
Travel distance
Yield per minute
```

---

# 38. Timed Nodes

Eventually support materials available only during specific windows.

Example:

```text
Material X

Available:
14:00–16:00 Eorzea Time
```

Scheduler behavior:

```text
If available:
    gather now

If unavailable:
    perform another useful job

When window approaches:
    schedule travel/gather job
```

Avoid waiting idle when another production dependency can be completed.

---

# 39. Travel Between Territories

Navigation within a territory and travel between territories are separate problems.

CielCraft should eventually support:

```text
Determine target territory
Teleport to appropriate aetheryte
Wait for territory change
Confirm arrival
Invoke vnavmesh
Navigate to gathering area
```

Implement travel as a provider or job abstraction rather than mixing teleport logic directly into gathering code.

---

# 40. Travel Provider

Suggested:

```text
ITravelProvider
```

Responsibilities:

```text
Determine whether destination is in current territory
Select appropriate teleport destination
Initiate teleport
Observe loading/territory transition
Confirm arrival
Hand destination to navigation provider
```

---

# 41. Production Orchestration Example

User requests:

```text
50 × Final Item
```

CielCraft calculates:

```text
Need:
200 Ingots
100 Lumber

Inventory:
120 Ingots
30 Lumber

Missing:
80 Ingots
70 Lumber

To craft those:

320 Ore
210 Logs

Inventory:
100 Ore
60 Logs

Missing raw materials:
220 Ore
150 Logs
```

Queue:

```text
1 Travel to Ore territory
2 Gather Ore ×220
3 Travel to Log territory
4 Gather Logs ×150
5 Craft Ingots ×80
6 Craft Lumber ×70
7 Craft Final Item ×50
```

Then execute automatically.

---

# 42. Production Orchestrator

This is the core CielCraft system.

Conceptually:

```text
Production Goal
      │
      ▼
Dependency Resolver
      │
      ▼
Inventory Resolver
      │
      ▼
Production Planner
      │
      ▼
Job Scheduler
      │
      ├── Travel
      ├── Gather
      ├── Craft
      └── Wait
      │
      ▼
Inventory Updated
      │
      ▼
Re-evaluate Goal
```

---

# 43. UI

Main window concept:

```text
┌─────────────────────────────────────────────┐
│                  CielCraft                  │
├─────────────────────────────────────────────┤
│ Dalamud:      ✓                             │
│ Raphael:      ✓                             │
│ vnavmesh:     ✓                             │
│                                             │
│ Character: Weaver Lv. 100                  │
│ Craftsmanship: 5402                        │
│ Control:       5214                        │
│ CP:             702                        │
│                                             │
│ Item: [ Search item...                   ] │
│                                             │
│ Quantity: [ 50 ]                           │
│                                             │
│ ☑ Use existing inventory                   │
│ ☑ Craft required sub-recipes               │
│ ☑ Gather missing materials                 │
│ ☑ Adaptive crafting                        │
│                                             │
│       [ PLAN ]          [ START ]           │
└─────────────────────────────────────────────┘
```

---

# 44. Plan Preview

Before START:

```text
TARGET

50 × Final Item


AVAILABLE

120 Ingots
30 Lumber


MISSING

80 Ingots
70 Lumber


RAW MATERIAL DEFICIT

220 Ore
150 Logs


PLAN

Gather 220 Ore
Gather 150 Logs

Craft 80 Ingots
Craft 70 Lumber
Craft 50 Final Items


[ START PRODUCTION ]
```

The user should always be able to inspect the proposed plan.

---

# 45. Active Production UI

Example:

```text
PRODUCTION

Final Item ×50

Overall:
██████████████░░░░░░ 71%


CURRENT JOB

Crafting Ingots

61 / 80


CURRENT CRAFT

Progress     3210 / 5200
Quality      9340 / 12600
Durability   35 / 70
CP           281 / 702
Condition    Good


Solver:
Raphael + Adaptive


Next action:
Precise Touch


[ PAUSE ]

[ STOP ]
```

---

# 46. Debug UI

A dedicated developer/debug window should exist from the beginning.

Display:

```text
Current territory
Player position
Current target
Current craft state
Current gathering state
Current production job
Current navigation destination
vnavmesh status
Raphael status
Last executed action
Last state transition
Errors
```

This will be extremely useful during user testing.

---

# 47. Logging

Logging is mandatory.

Categories:

```text
Plugin
Game
Craft
Raphael
Adaptive
Recipe
Inventory
Production
Gather
Navigation
Travel
Scheduler
Error
```

Example:

```text
[Production]
Goal: Item 12345 ×50

[Inventory]
Owned Ore=100 Required=320 Missing=220

[Scheduler]
Created GatherJob Ore ×220

[Navigation]
Moving to node area X,Y,Z

[Gather]
Node found ObjectId=...

[Gather]
Ore +5

[Craft]
Started recipe 54321

[Raphael]
Generated 17-action solution

[Craft]
Step=7 Condition=Excellent

[Adaptive]
Overriding Raphael action.

[Craft]
Executing action Precise Touch
```

---

# 48. Emergency Controls

Required:

```text
Pause
Stop
```

STOP must:

```text
stop issuing craft actions
stop gathering actions
request vnavmesh stop movement
cancel pending automation commands
```

Eventually support an emergency hotkey.

---

# 49. Failure Handling

Automation must fail safely.

Examples:

```text
vnavmesh unavailable
pathfinding failure
craft action rejected
unexpected crafting state
inventory mismatch
node unavailable
target lost
territory mismatch
Raphael failure
player manually interferes
```

Do not blindly continue when assumptions are invalid.

Prefer:

```text
Pause
 ↓
Log
 ↓
Explain
 ↓
Allow retry/replan
```

---

# 50. Offline Craft Testing

Crafting logic should remain testable without actively automating FFXIV.

Support:

```text
Recipe
Crafter stats
Starting quality
Raphael solve
Craft simulation
Adaptive simulation
```

This allows testing of:

```text
Raphael wrapper
adaptive rules
quality cutoff logic
completion optimization
dependency planner
```

without live crafting.

---

# 51. Testing Architecture

Abstract game-specific behavior.

Examples:

```text
IGameBridge

Real:
DalamudGameBridge

Test:
MockGameBridge
```

Navigation:

```text
INavigationProvider

Real:
VNavmeshProvider

Test:
MockNavigationProvider
```

Solver:

```text
ICraftSolver

Real:
RaphaelSolver

Test:
DeterministicTestSolver
```

This enables automated tests of the production system.

---

# 52. Development Strategy

DO NOT build horizontally.

Do not simultaneously implement:

```text
crafting
gathering
navigation
travel
adaptive solving
dependency planning
```

Build vertically.

Each milestone must produce something that works.

---

# 53. Milestone 0 — Plugin Foundation

Build:

```text
Dalamud plugin
basic UI
logging
configuration
service injection
project structure
```

Success:

```text
CielCraft loads successfully inside Dalamud.
Main window opens.
```

---

# 54. Milestone 1 — Craft State

Display live:

```text
Recipe
Progress
Quality
Durability
CP
Step
Condition
```

Success:

> CielCraft's values exactly match FFXIV.

No automation yet.

---

# 55. Milestone 2 — One Craft Action

Provide debug button:

```text
Execute Basic Synthesis
```

Success:

```text
CielCraft requests action.
FFXIV executes it.
CielCraft observes resulting state transition.
```

---

# 56. Milestone 3 — Raphael

Integrate Raphael.

Given:

```text
current stats
+
recipe
```

produce:

```text
valid crafting solution
```

Display solution before executing.

---

# 57. Milestone 4 — One Automated Craft

Workflow:

```text
Start craft
Generate Raphael solution
Execute action
Confirm transition
Execute next
...
Confirm craft completed
```

Success:

> One recipe is crafted automatically.

---

# 58. Milestone 5 — Batch Crafting

Input:

```text
Item X ×20
```

Success:

> CielCraft produces 20 successfully while verifying each craft.

At this milestone CielCraft is already useful.

---

# 59. Milestone 6 — Adaptive Crafting

Initial adaptations:

```text
Stop quality actions when target reached
Optimize remaining synthesis
React to Good
React to Excellent
Replan unexpected state
```

Success:

> CielCraft can intentionally deviate from Raphael based on live state.

---

# 60. Milestone 7 — Inventory

Read inventory.

Calculate:

```text
Required
Owned
Missing
```

Success:

> CielCraft accurately predicts how many crafts are currently possible.

---

# 61. Milestone 8 — Dependency Resolution

Input:

```text
Final Item ×20
```

Automatically calculate required sub-recipes.

Success:

> Complete recursive production graph generated.

---

# 62. Milestone 9 — Automatic Subcrafting

Execute:

```text
Subrecipe A
Subrecipe B
Final recipe
```

in correct order.

Success:

> Given enough raw materials, CielCraft can produce a final item from raw ingredients without manually crafting intermediates.

This is the first major production-system milestone.

---

# 63. Milestone 10 — vnavmesh Integration

Connect to vnavmesh IPC.

Implement:

```text
IsReady
MoveTo
MoveCloseTo
IsMoving
Stop
```

Success:

```text
Debug UI:

Destination X,Y,Z

[GO]

Character navigates there.
```

No gathering yet.

---

# 64. Milestone 11 — Gathering State

Read:

```text
Gathering node
Gathering UI
Available items
Gather attempts
GP
Gathering result
```

Success:

> CielCraft understands a manually opened gathering node.

---

# 65. Milestone 12 — One Automated Node

Given nearby node:

```text
Navigate
Target
Interact
Select required material
Gather
Verify inventory increase
```

Success:

> CielCraft automatically gathers one requested material from one node.

---

# 66. Milestone 13 — Gathering Loop

Input:

```text
Gather Ore ×50
```

CielCraft:

```text
find node
navigate
gather
find next node
navigate
gather
repeat
```

Success:

> Requested quantity acquired.

---

# 67. Milestone 14 — Production + Gathering

Input:

```text
Craft Item X ×20
```

Raw materials are insufficient.

CielCraft:

```text
resolves missing material
creates GatherJob
navigates
gathers
returns to crafting
crafts sub-recipes
crafts final items
```

Success:

> End-to-end production from missing raw materials.

---

# 68. Milestone 15 — Territory Travel

Add:

```text
teleport selection
territory transition detection
arrival confirmation
vnavmesh handoff
```

Success:

> CielCraft can gather a required material located in another territory.

---

# 69. Milestone 16 — Timed Nodes

Add scheduling for:

```text
timed gathering nodes
Eorzea Time
alternative work while waiting
```

Success:

> Production planner schedules timed materials intelligently.

---

# 70. Version 0.1 Definition

CielCraft 0.1:

```text
Dalamud plugin
reads live crafting state
integrates Raphael
executes one complete craft
```

---

# 71. Version 0.2 Definition

CielCraft 0.2:

```text
batch crafting
stable craft state machine
error recovery
production UI
```

---

# 72. Version 0.3 Definition

CielCraft 0.3:

```text
adaptive crafting
inventory awareness
quality optimization
```

---

# 73. Version 0.4 Definition

CielCraft 0.4:

```text
dependency resolution
sub-recipe planning
automatic subcrafting
```

At this stage:

```text
"Make 50 Item X"
```

works whenever all raw materials already exist.

---

# 74. Version 0.5 Definition

CielCraft 0.5:

```text
vnavmesh
Miner gathering
Botanist gathering
basic gathering loops
```

---

# 75. Version 0.6 Definition

CielCraft 0.6:

```text
production planner
automatic gathering of missing raw materials
automatic resumption of crafting
```

At this stage the core product vision is achieved.

---

# 76. Long-Term Version 1.0

CielCraft 1.0 should reliably support:

```text
batch crafting
Raphael solving
adaptive live crafting
inventory awareness
recursive sub-recipes
automatic subcrafting
Miner gathering
Botanist gathering
vnavmesh navigation
cross-territory travel
timed nodes
dynamic production scheduling
failure recovery
production replanning
```

---

# 77. Version 1.0 User Experience

User opens CielCraft.

Searches:

```text
Item X
```

Sets:

```text
Quantity: 50
```

CielCraft reports:

```text
You currently have enough materials for 17.

To produce 50:

Need to gather:

82 Ore
31 Logs

Need to craft:

22 Ingots
11 Lumber
50 Item X


Estimated production plan:

1. Gather Ore
2. Gather Logs
3. Craft Ingots
4. Craft Lumber
5. Craft Item X


[ START ]
```

User presses START.

CielCraft handles the rest.

---

# 78. Final Responsibility Matrix

## Dalamud

Responsible for:

```text
Plugin hosting
FFXIV integration
Game services
Interop infrastructure
Plugin IPC
```

## Raphael

Responsible for:

```text
Craft simulation
Baseline optimal/static crafting solutions
```

## vnavmesh

Responsible for:

```text
Navigation mesh
Pathfinding
Movement execution
```

## CielCraft

Responsible for:

```text
Production intelligence
Recipe dependencies
Inventory reasoning
Craft orchestration
Adaptive crafting
Gathering decisions
Gathering execution
Travel decisions
Navigation destinations
Job scheduling
Replanning
User experience
```

---

# 79. Critical Architectural Principle

CielCraft should orchestrate existing specialized components rather than reimplementing them.

Do not spend development time rebuilding:

```text
Dalamud
Raphael
vnavmesh
```

unless a future product requirement genuinely demands it.

Instead build the intelligence connecting them.

The unique value of CielCraft is:

```text
"I want 50 of this."
        ↓
CielCraft figures out everything required.
```

---

# 80. Instruction to Astra

Treat this document as the authoritative CielCraft master specification.

It supersedes the previous standalone/injected-runtime architecture.

Do not create:

```text
CielCraft.Runtime.dll
custom injector
external CielCraft.exe
custom navigation mesh
custom pathfinding system
```

unless explicitly requested later.

CielCraft should be implemented as a Dalamud plugin.

Use Raphael for baseline crafting solving.

Use vnavmesh through Dalamud IPC for navigation.

Keep both behind provider interfaces.

Build the project milestone-by-milestone.

Do not skip ahead to gathering before reliable crafting is established.

The immediate implementation target is:

```text
Milestone 0
Dalamud plugin foundation

↓

Milestone 1
Accurate live craft-state reader

↓

Milestone 2
Execute exactly one crafting action and verify its state transition

↓

Milestone 3
Raphael integration

↓

Milestone 4
Automatically complete exactly one craft
```

After Milestone 4 is validated by the user in FFXIV, proceed to batch crafting.

---

# 81. North Star

Every major architectural decision should be evaluated against one question:

> **Does this move CielCraft closer to allowing the user to simply specify an item and quantity and have the complete production chain handled automatically?**

The ultimate flow remains:

```text
ITEM + QUANTITY
       │
       ▼
      PLAN
       │
       ▼
     ACQUIRE
       │
       ▼
    SUBCRAFT
       │
       ▼
      CRAFT
       │
       ▼
      ADAPT
       │
       ▼
     VERIFY
       │
       ▼
    COMPLETE
```

That is CielCraft.
