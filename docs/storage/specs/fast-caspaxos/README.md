# Fast CASPaxos in Quint

`FastCASPaxos.qnt` is an executable, finite-state model of the Fast CASPaxos
register protocol with **Vertical Paxos I voter reconfiguration**. It checks
fast and classic agreement, recovery, prepared-ballot reuse, acceptor-floor
fencing, cross-configuration hand-off, dual per-key completion, pre-join refusal,
and preservation of committed update histories under asynchronous delivery.

## Protocol and abstraction

Ballots are ordered lexicographically by `(config, round, owner)`:

- `(0, 0, 0)` is the zero ballot (unaccepted state or absent piggyback).
- `(c, 0, 0)` (`c >= 1`) is a floor ballot fencing all configurations `< c`.
- `(c, r, 0)` (`c >= 1, r >= 1`) is a shared fast ballot in configuration `c`.
- `(c, r, o)` (`c >= 1, r >= 1, o >= 1`) is an exclusive classic ballot in `c`.

Each configuration `c` in `Configs: int -> Set[int]` defines a voter set `V(c)`,
classic quorum `|V(c)| / 2 + 1`, and fast quorum `FastQuorums.get(c)`. Acceptors
retain `(floor, promised, accepted, value)` durably with effective promise
`effPromised(acc) = max(acc.promised, acc.floor)`. Bootstrap voters start at
`floor = (1, 0, 0), promised = (1, 1, 0)`; newly introduced voters start at
`floor = (firstConfig(a), 0, 0), promised = (firstConfig(a), 0, 0)`. Only
bootstrap `(1, 1, 0)` is implicitly prepared; for `c > 1`, a key's first ballot
is classic and may piggyback `(c, r + 1, 0)` to enable 1-RTT fast writes after
configuration `c` completes.

`raiseFloor(a, c)` advances `a`'s durable floor to `(c, 0, 0)` once `c - 1` is
complete. `prepare(a, b)` is gated on `b.config == 1 or complete.contains(b.config - 1)`
and intentionally left ungated on `votersOf(b.config)` so misrouted prepares
exercise pre-join floor refusal (`noPreJoinAcceptance`). `issueRecovered` selects
either a classic quorum of `V(b.config)` (when `b.config` is complete) or a
Vertical Paxos I hand-off quorum `q ⊆ V(c - 1) ∪ V(c)` containing both a classic
quorum of `V(c - 1)` and a classic quorum of `V(c)` (while `c` is incomplete).
`recoveredBases` handles stale unchosen remnants (`highest.config < minRelevantCfg`)
and scopes fast-ballot tallies strictly to `V(highest.config)`.

`issuePrepared` requires `complete.contains(b.config)` and either bootstrap
`(1, 1, 0)` or a piggybacked promise certificate in `V(b.config)` from a
committed ballot in the same configuration. `accept(a, r)` is gated on
`votersOf(r.ballot.config).contains(a)` (`onlyVotersAccept`) and compares new
accepts against `max(effPromised(old), old.accepted)`. Exact duplicate replays
leave durable state unchanged and echo `effPromised(old)`. A configuration `c`
completes for the modeled key via either `completeTransferred(c)` (a ballot of
`c` is chosen in `V(c)`) or `completeVacuous(c)` (a classic quorum of `V(c - 1)`
holds `floor >= (c, 0, 0)` with `accepted == zero`).

Values form a bounded binary tree: `0 -> {1, 2}`, `1 -> {3, 4}`, and so on.
Each proposal either takes one child or preserves its base (identity updates
model reads, inapplicable CAS operations, and hand-off transfers).

## Checked properties

`safety` combines 11 invariants:

| Property | Guarantee |
| --- | --- |
| `quorumIntersection` | For every configuration `c`, classic quorums intersect, fast quorums intersect, and `2 * fast + classic > 2 * N(c)`. |
| `stateWellFormed` | Ballots, floors, values, completion flags, and participants stay in their finite domains; prepare snapshots precede their recovery ballot. |
| `firstWriteWins` | An acceptor acknowledges at most one value per ballot. |
| `classicOwnership` | Each classic ballot carries at most one immutable request. |
| `oneDecisionPerBallot` | Each ballot chooses at most one value, counted strictly over `V(b.config)`. |
| `chosenChain` | Values chosen at increasing ballots across any configurations extend earlier chosen histories. |
| `acceptedExtendsChosen` | Every higher-ballot acceptance extends every lower-ballot chosen value. |
| `durableHistory` | Current accepted ballots and effective promises preserve all prior acknowledgment and prepare evidence. |
| `acknowledgmentProvenance` | Each acknowledgment corresponds to an issued request. |
| `floorMonotoneAndEffective` | Acceptor floors are monotonically non-decreasing; all prepares and non-duplicate accepts satisfy `floor <= ballot`, and all acks echo `promised >= floor`. |
| `noPreJoinAcceptance` | Newly introduced voters never promise or accept any ballot from a configuration before `firstConfig(a)`. |

## Guard-disabling mutants

All 7 reconfiguration mutants are checked via `../check-mutants.sh`:

| Mutant | Module | Target invariant | Bug injected |
| --- | --- | --- | --- |
| `mutantFloorOnlyHeldKeys` | `FastCASPaxosReconfigReplace` | `chosenChain` | `prepare`/`accept` ignore `floor` when `old.accepted == zero` while `completeVacuous` still fires. |
| `mutantPhase1InNewConfigBeforeComplete` | `FastCASPaxosReconfigReplace` | `chosenChain` | Hand-off Phase 1 reads only `V(c)`, skipping `V(c - 1)`. |
| `mutantHandoffSkipsNewConfigQuorum` | `FastCASPaxosReconfigReplace` | `chosenChain` | Hand-off Phase 1 reads only `V(c - 1)`, skipping `V(c)`. |
| `mutantImplicitFastInNewConfig` | `FastCASPaxosReconfigReplace` | `acceptedExtendsChosen` | Treats `(c, 1, 0)` as implicitly prepared for `c > 1`. |
| `mutantNewVoterBeforeFloor` | `FastCASPaxosReconfigReplace` | `noPreJoinAcceptance` | New voter initializes with `floor = (1, 0, 0)` instead of `(firstConfig(a), 0, 0)`. |
| `mutantSkipSingleChoice` | `FastCASPaxosReconfigReplace` | `chosenChain` | Allows hand-off at config `2` against a conflicting voter set `altV != V(2)`. |
| `mutantUnscopedFastTally` | `FastCASPaxosReconfig4Voter` | `chosenChain` | Disables both the `accept` `votersOf` gate and the `recoveredBases` `V(hCfg)` filter. |

## Cross-repository sharing

`FastCASPaxos.qnt`, `FastCASPaxosMC.qnt`, `FastCASPaxosReconfigMC.qnt`,
`FastCASPaxosTests.qnt`, and `tlc.json` contain no Roux-only paths or
conventions and are shared **byte-identically** with
`ReubenBond/fast-caspaxos` (`quint/`). Reconfiguration simulation and mutant
modules live in `FastCASPaxosReconfigMC.qnt` so `FastCASPaxosMC.qnt` remains
comfortably under Apalache's 20 MB JSON RPC string limit during TLC compilation.
Repository-specific transport, SQLite, and Rapid integration details live in
[`docs/design/fast-caspaxos.md`](../../docs/design/fast-caspaxos.md).

## Reproduce the checks

Use Node.js 22, Quint **0.33.0**, and Java 21 for model checking.
Install the CLI if needed:

```powershell
npm install --global @informalsystems/quint@0.33.0
```

From this directory:

```powershell
quint typecheck .\FastCASPaxosMC.qnt
quint typecheck .\FastCASPaxosReconfigMC.qnt
quint typecheck .\FastCASPaxosTests.qnt
quint test .\FastCASPaxosTests.qnt --main FastCASPaxosTests --max-samples 1 --seed 1
quint test .\FastCASPaxosTests.qnt --main FastCASPaxosReconfigTests --max-samples 1 --seed 1
quint test .\FastCASPaxosTests.qnt --main FastCASPaxosNegativeTests --max-samples 1 --seed 1
quint test .\FastCASPaxosTests.qnt --main FastCASPaxosBoundaryTests --max-samples 1 --seed 1
quint run .\FastCASPaxosMC.qnt --main FastCASPaxosMC --backend rust --invariant safety --max-samples 2500 --max-steps 60 --seed 17
quint run .\FastCASPaxosReconfigMC.qnt --main FastCASPaxosReconfigReplace --backend rust --invariant safety --max-samples 2500 --max-steps 60 --seed 17
quint run .\FastCASPaxosReconfigMC.qnt --main FastCASPaxosReconfig4Voter --backend rust --invariant safety --max-samples 2500 --max-steps 60 --seed 17
quint run .\FastCASPaxosReconfigMC.qnt --main FastCASPaxosReconfigDoubleReplace --backend rust --invariant safety --max-samples 2500 --max-steps 60 --seed 17
quint run .\FastCASPaxosReconfigMC.qnt --main FastCASPaxosReconfigAddRemove --backend rust --invariant safety --max-samples 2500 --max-steps 60 --seed 17
quint run .\FastCASPaxosReconfigMC.qnt --main FastCASPaxosReconfigDisjoint --backend rust --invariant safety --max-samples 2500 --max-steps 60 --seed 17
bash ../check-mutants.sh FastCASPaxosReconfigMC.qnt FastCASPaxosReconfigReplace safety --backend=rust --max-samples=2500 --max-steps=60 --seed=17
bash ../check-mutants.sh FastCASPaxosReconfigMC.qnt FastCASPaxosReconfig4Voter safety --backend=rust --max-samples=2500 --max-steps=60 --seed=17
quint verify .\FastCASPaxosMC.qnt --main FastCASPaxosExhaustive --backend tlc --tlc-config .\tlc.json --invariant safety
quint verify .\FastCASPaxosMC.qnt --main FastCASPaxosPipeline --backend tlc --tlc-config .\tlc.json --invariant safety
quint verify .\FastCASPaxosMC.qnt --main FastCASPaxosReconfigExhaustive --backend tlc --tlc-config .\tlc.json --invariant safety
```

Automatic Quint/TLC checks run in [`.github/workflows/specs-storage.yml`](../../.github/workflows/specs-storage.yml) for changes under `specs/fast-caspaxos/**`, on pushes to `main` and on pull requests.

| Module | File | `Configs` | `FastQuorums` | Owners / Rounds / Depth | CI check |
| --- | --- | --- | --- | --- | --- |
| `FastCASPaxosSmall` | `FastCASPaxosMC.qnt` | `1 -> {1,2,3}` | `1 -> 3` | 1 / 2 / 1 | Simulation |
| `FastCASPaxosEven` | `FastCASPaxosMC.qnt` | `1 -> {1,2,3,4}` | `1 -> 3` | 2 / 3 / 2 | Simulation |
| `FastCASPaxosMC` | `FastCASPaxosMC.qnt` | `1 -> {1,2,3,4,5}` | `1 -> 4` | 2 / 4 / 3 | Simulation |
| `FastCASPaxosReconfigReplace` | `FastCASPaxosReconfigMC.qnt` | `1 -> {1,2,3}, 2 -> {1,2,4}` | `1 -> 3, 2 -> 3` | 2 / 2 / 2 | Simulation + 6 mutants |
| `FastCASPaxosReconfig4Voter` | `FastCASPaxosReconfigMC.qnt` | `1 -> {1,2,3,5}, 2 -> {1,2,3,4}` | `1 -> 3, 2 -> 3` | 2 / 3 / 2 | Simulation + `mutantUnscopedFastTally` |
| `FastCASPaxosReconfigDoubleReplace` | `FastCASPaxosReconfigMC.qnt` | `1 -> {1,2,3}, 2 -> {1,2,4}, 3 -> {1,4,5}` | `1 -> 3, 2 -> 3, 3 -> 3` | 2 / 2 / 2 | Simulation (3-config chain) |
| `FastCASPaxosReconfigAddRemove` | `FastCASPaxosReconfigMC.qnt` | `1 -> {1,2,3}, 2 -> {1,2,3,4}, 3 -> {2,3,4}` | `1 -> 3, 2 -> 3, 3 -> 3` | 2 / 2 / 2 | Simulation (add then remove) |
| `FastCASPaxosReconfigDisjoint` | `FastCASPaxosReconfigMC.qnt` | `1 -> {1,2,3}, 2 -> {4,5,6}` | `1 -> 3, 2 -> 3` | 2 / 2 / 2 | Simulation (full migration) |
| `FastCASPaxosExhaustive` | `FastCASPaxosMC.qnt` | `1 -> {1,2,3}` | `1 -> 3` | 1 / 1 / 1 | Exhaustive TLC |
| `FastCASPaxosPipeline` | `FastCASPaxosMC.qnt` | `1 -> {1,2}` | `1 -> 2` | 1 / 2 / 1 | Exhaustive TLC |
| `FastCASPaxosReconfigExhaustive` | `FastCASPaxosMC.qnt` | `1 -> {1,2}, 2 -> {1,3}` | `1 -> 2, 2 -> 2` | 1 / 1 / 1 | Exhaustive TLC |

The model follows the protocol in [`docs/design/fast-caspaxos.md`](../../docs/design/fast-caspaxos.md). Quint language and tool documentation:
<https://quint-lang.org/docs>.
