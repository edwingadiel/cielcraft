# Merge helpers for parallel packages

Used when several agent branches append to the same files (the bridge, the
interface, the test fakes, the test project). Run from the repo root after
`git merge` reports conflicts:

- `perl tools/merge/resolve_both.pl <file>...` keeps both sides of every
  conflict hunk (ours first) — right for append-only files.
- `perl tools/merge/tailmerge.pl CielCraft/Game/DalamudGameBridge.cs <branch> "// ---- <banner> ----"`
  rebuilds a file as HEAD's version plus the branch's banner block (git splits
  method bodies across hunks in the bridge; "both" leaves it unbalanced).
- `dotnet build 2>&1 | grep " error " | sort -u > errs.txt; perl tools/merge/addstubs.pl errs.txt`
  appends throwing stubs to every test FakeBridge for the interface members
  it does not implement yet.
