# Contributing

Thanks for taking an interest. This is a small solo project, but PRs are welcome and I'll review them.

## Quick start

```bash
git clone https://github.com/XeldarAlz/FFXIV-DomanMahjongSolver.git
cd FFXIV-DomanMahjongSolver
dotnet restore Mahjong.Plugin.Dalamud.sln --locked-mode
dotnet build   Mahjong.Plugin.Dalamud.sln
dotnet test    Mahjong.Plugin.Dalamud.sln
```

Use SDK 10.0.400 from `global.json`, plus the .NET 8 runtime for portable projects. Run `./tools/install-dalamud.ps1` on Windows to install the exact Dalamud build and verify hashes in `build/dependencies.lock.json`. CI and releases use this same installer and NuGet lock files.

## Test suite

Seven .NET suites cover rules, engine logic, public-state reduction, strategies, strict replay, configuration, IPC and plugin action flows. The runner output is the current test count; do not maintain a duplicated count here.

Pure logic tests run on Linux. Dalamud plugin tests run on Windows. Python transport and arena tests use `python -m unittest discover -s tools/tests -v`. Missing replay fixtures or goldens fail tests. Only explicit `UPDATE_REPLAY_SNAPSHOTS=1` (Tenhou) or `MJ_REGEN_FIXTURES=1` (UI fixtures) updates reviewed baselines.

See [implementation status](docs/implementation-status.md) and [paired evaluation](docs/evaluation.md) for acceptance limits and commands.

## Project layout

```
FFXIV-DomanMahjongSolver/
├── Mahjong.Core/                value types: Tile, Meld, Hand, Decomposition, ...
├── Mahjong.Rules/               IRuleSet + 38 IYakuRule + scoring/dora/fu rules
├── Mahjong.Policy.Abstractions/ contracts: IPolicy + sub-policies, IRandomSource, weights
├── Mahjong.Plugin.Game/         plugin contracts + LayoutProfile + ActionStateMachine
├── Mahjong.Replay/              Tenhou parser + golden-file regression harness
├── Mahjong.Engine/              decomposition · shanten · ukeire · Scorer
├── Mahjong.Policy/              heuristic policy implementations · weight tuner
├── Mahjong.Tuner/               legacy logic smoke tuner; not a strength benchmark
├── Mahjong.Plugin.Dalamud/      the Dalamud plugin (thin shell)
│
├── data/
│   ├── layouts/                 per-variant addon offset profiles (JSON)
│   ├── replays/                 Tenhou logs + golden snapshots for regression
│   └── weights/                 tuner output: versioned weight bundles
│
├── docs/
│   ├── architecture.md          layered overview · extension points
│   ├── dispatch-protocol.md     popup-by-popup dispatch shapes (verified/broken)
│   ├── roadmap.md               shipped / in progress / planned
│   └── ruleset.md               Doman vs Riichi rules spec
│
├── server/                      legacy server source; no active plugin upload pipeline
├── tests/                       per-project test suites
├── tools/                       Node + Python + PowerShell scripts:
│                                  - legacy corpus analysis (not part of plugin operation)
│                                  - cross-install offset RE scanners (scan-*.mjs)
│                                  - per-variant capture helpers (scan_tiles.py)
├── repo/repo.json               Dalamud plugin manifest (CI-checked against Directory.Build.props)
└── .github/workflows/           CI (build · test · format · version sync) · auto-tag · release
```

Rule of thumb: if logic can live in `Mahjong.Engine`, `Mahjong.Rules`, or `Mahjong.Policy`, put it there and test it. Keep `Mahjong.Plugin.Dalamud/` focused on glue: reading addons, dispatching clicks, drawing windows.

See [`docs/architecture.md`](docs/architecture.md) for the layered overview and extension points, and [`docs/dispatch-protocol.md`](docs/dispatch-protocol.md) for the source-of-truth inventory of every Doman Mahjong popup/state and its dispatch shape.

## Before you open a PR

1. `dotnet build` cleanly.
2. `dotnet test` passes. If you changed engine or policy behavior, add or update tests.
3. Keep the diff focused. One concern per PR.
4. Match the existing style: terse and direct. No heavy abstractions "for later."
5. If your change affects what a user sees or types, update the README.

## Good first issues

Check the issue tracker for anything labeled `good first issue`. If nothing's there, [`docs/roadmap.md`](docs/roadmap.md) lists open work: especially JP / OC client verification and the remaining stuck-state captures.

## Releasing (maintainers)

Bump `<Version>` in [`Directory.Build.props`](Directory.Build.props) (single source of truth) **and** `AssemblyVersion` + `TestingAssemblyVersion` in `repo/repo.json`. CI's `guards` job fails fast if the three values don't match. Merge to main → `auto-tag` workflow creates the `vX.Y.Z` tag → `release` workflow builds and uploads `latest.zip`.

On first run per version, the release tag sometimes needs a one-time manual re-push (GitHub won't let workflow-pushed tags trigger other workflows); `gh workflow run release.yml --ref vX.Y.Z` is the standard recovery.

After the release publishes, set proper release notes via `gh release edit vX.Y.Z --notes "$(cat <<EOF ... EOF)"`. The boilerplate "Full Changelog: ..." is not enough for an alpha plugin where users rely on the release page to understand what changed.

## Reporting bugs

Use the bug report template. Include plugin version, mode, and repro steps. Each dispatch is annotated with `schedState`/`curState`/`path` so a single log paste from the chat log is usually enough to pin a regression.

## Security

Please don't file public issues for security problems; see [SECURITY.md](SECURITY.md).

## Code of conduct

See [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md). Be decent.

## License

By contributing, you agree your contributions are licensed under AGPL-3.0-or-later, the same as the project.
