# Rapid Consensus and Cut Detection Model

`rapidConsensus.qnt` formally specifies Rapid's multi-process cut detector (`CutDetector`) and single-decree Fast + Classic Paxos (`ConsensusCoordinator`, `PaxosAcceptor`, `PaxosLearner`) as described in [`docs/design/rapid.md`](../../docs/design/rapid.md).

## Checked properties (`safety`)

| Invariant | Guarantee |
| --- | --- |
| `agreement` | At most one cut proposal is ever decided per configuration across Fast (`N - f`) and Classic (`floor(N/2) + 1`) rounds. |
| `validity` | Every decided value is a valid candidate cut proposal. |
| `classicUniqueness` | Each classic rank `(round >= 2, node)` proposes and accepts at most one value. |
| `fastDecisionPreserved` | Once any proposal reaches a fast quorum `N - f` in round 1, every classic round 2+ proposal must choose that value. |
| `cutStability` | Normal `CutDetector` emission never fires while any subject sits between `L_EFF` and `H_EFF`, and every emitted subject has at least `H_EFF` distinct ring reports (real or implicitly invalidated). |

## Mutants (`../check-mutants.sh`)

| Mutant | Disabled guard | Expected violation |
| --- | --- | --- |
| `mutantUndersizedFastQuorum` | Fast quorum reduced to `N - f - 1` | `agreement` |
| `mutantArbitraryFastRecovery` | `chooseValue` picks an arbitrary round-1 value instead of `count > N / 4` | `fastDecisionPreserved` |
| `mutantAcceptIgnoresPromise` | `classicAccept` omits `rank >= promised` check | `agreement` |
| `mutantFastVoteAfterClassicPromise` | `fastVote` allowed after promising a classic round `>= 2` | `fastDecisionPreserved` |
| `mutantUndersizedClassicQuorum` | Classic quorum reduced to `floor(N/2)` | `agreement` |
| `mutantEmitDuringUnstableCut` | `CutDetector` emits while `preProposal` is non-empty | `cutStability` |

## Reproduce

```bash
cd specs/rapid
quint typecheck rapidConsensus.qnt
quint test rapidConsensus.qnt --main=rapidConsensusSim --match=Test
quint run rapidConsensus.qnt --main=rapidConsensusSim --backend=rust --invariant=safety --max-steps=40 --max-samples=20000
../check-mutants.sh rapidConsensus.qnt rapidConsensusSim safety --backend=rust --max-steps=40 --max-samples=20000 --seed=7

# Exhaustive TLC verification: Consensus and CutDetector operate on disjoint state
# variables (`keepCut` / `keepConsensus`), so TLC exhausts each component independently.
quint verify rapidConsensus.qnt --main=rapidConsensusTlc --step=stepConsensus --invariant=consensusSafety --backend=tlc
quint verify rapidConsensus.qnt --main=rapidConsensusClassicTlc --step=stepConsensus --invariant=consensusSafety --backend=tlc
quint verify rapidConsensus.qnt --main=rapidCutDetectorTlc --step=stepCut --invariant=cutSafety --backend=tlc
```
