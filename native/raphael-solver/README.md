# raphael-solver (vendored, patched)

Copy of the `raphael-solver` crate from
[KonaeAkira/raphael-rs](https://github.com/KonaeAkira/raphael-rs) at commit
`def2860e5a2de4023fddc48e130f2d9b4b94710c`, licensed under Apache-2.0 (LICENSE).

`native/cielcraft-raphael/Cargo.toml` substitutes it for the git dependency via
`[patch]`, so `raphael-sim` and `raphael-data` still come from the pinned
upstream commit and share types with this crate.

## Modification

`src/macro_solver/solver.rs`: `MacroSolver::solve` now delegates to a new
public `MacroSolver::solve_from_state(SimulationState)`. Upstream only solves
from the synthesis-begin state; CielCraft's mid-craft re-solve passes the live
state (progress, quality, durability, CP and active effects) so the plan for
the remainder of a craft is exact rather than a "fresh miniature craft"
approximation. Nothing else is changed.

To bump the upstream pin: re-copy `src/`, re-apply the change above, and
update the `rev` in both `Cargo.toml` files.
