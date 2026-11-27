# Grain runtime specification

[`grainRuntime.qnt`](grainRuntime.qnt) is a [Quint](https://quint-lang.org) specification of the grain runtime's turn, staging, durability-gating, cancellation, and termination rules, with a minimal inbox and outbox, as designed in [Grain runtime](../../docs/design/runtime.md). It models Clog's journals and per-ledger replica leases only through their contracts.

## Model

- **Clog journal contract.** Each grain has one journal, a list of accepted entries, where each entry is either `Append(List[Cmd])` or a full-state snapshot `Replace(List[Cmd])`. Logical journal content (`cmdsOf`) folds `Append` by concatenation and `Replace` by replacing the accumulated command list. Clog tracks an opaque monotonic version `journalVer` per grain that advances on accepted writes and on internal Clog version rebases (`rebaseVersion`, `JS-I10`) without changing logical content. A write names the opaque version `atVer` it expects and the logical activation watermark `covers` it carries; it lands only when `journalVer == atVer`, otherwise it is `Rejected`. A recovery read returns the current logical content and `journalVer`.
- **Write outcomes (R14, R31).**
  - `ReplaceRequired`: Clog refuses an `Append` with no journal effect. The activation preserves in-memory state and `sent`, sets `replaceNeeded = true`, and answers with a `Replace(mem)` snapshot between segments (`sendReplace`) that folds the refused write and any newly staged commands into one entry.
  - `Fenced`: Clog refuses a write with no journal effect while this node still leads the range; the activation resends the exact same record verbatim (`fencedResend`).
  - `Rejected`: when `journalVer != atVer`, the activation re-reads the journal and compares logical content against its accepted content digest (`acceptedMem`, standing in for length + XXH3-128). If equal (a pure version rebase), it rebases `atVer` to `journalVer` and resends; if different (another writer moved the journal on), it terminates immediately.
- **Ambiguous writes.** When an activation terminates with a write in flight, the write lands, has no effect (`Moved` or decided against), or goes into limbo as `Ambiguous`. A write in limbo may land later, at most once, while its expected `atVer` holds, even after a new activation has read the journal without it.
- **Ledger-replica lease contract.** At most one activation of a grain exists at a time. Messages a terminated activation was holding become zombies, which lease checks keep from release.
- **Messaging contract.** Released messages may be delivered any number of times.
- **Turns.** A turn is a sequence of segments. A segment starts, writes its commands one at a time, and ends; turns interleave only between segments. Client turns write in two segments, read across two segments, or add an outbox message.
- **Staging, writes, and release (R2, R3, R30, R31).** Commands apply to memory and the staging buffer at once. Each activation has at most one write in flight; between segments, with none in flight, it sends everything staged as one `Append` (when `not(replaceNeeded)`) or writes a `Replace(mem)` snapshot. A message depends on the write (`covers`) that will carry the commands staged when it was created, or on the last write sent; it is released once `accepted >= dep`.
- **Cancellation (R11).** A turn not running a segment can be dropped; its completed segments stay staged.
- **Inbox and outbox.** Grain `A`'s outbox delivers to grain `B`'s inbox. `B` accepts each message ID once and acknowledges every delivery; a handler turn writes the message's effect and its completion in one segment. `A` removes acknowledged messages.
- **Shapes.** Turn identities, turn kinds, commands, journal entries, and message kinds are sum types; active activations are the keys of a map, and the write in flight is a list of at most one, so no field holds a sentinel.

## Properties

| Invariant | Meaning |
| --- | --- |
| `nilExternality` | Every released message depends only on accepted entries, and what it reports (a response's value, a delivery's outbox entry, an acknowledgement's acceptance) is in the sender's durable journal. |
| `turnExclusive` | At most one segment of an activation runs at a time. |
| `segmentAtomic` | Every journal entry (`Append` or `Replace`) holds whole segments. |
| `journalMonotone` | Logical journal content (`cmdsOf`) grows monotonically across every landing `Append` and `Replace` write. |
| `effectivelyOnce` | The receiver's journal applies each message's effect at most once, and only for messages the sender durably sent. |

`safety` is their conjunction.

## Mutants

Each mutant breaks one rule of the design and must violate the invariant named in its `// breaks:` comment, which CI checks with the shared [`check-mutants.sh`](../check-mutants.sh):

| Mutant | Broken rule | Violates | Caught by |
| --- | --- | --- | --- |
| `mutantReleaseEarly` | Release a message before its write is accepted (R3) | `nilExternality` | simulation |
| `mutantDepIgnoresStaged` | Make a response depend on the last write sent, ignoring commands still staged (R3) | `nilExternality` | simulation |
| `mutantSendMidSegment` | Send an `Append` write while a segment runs (R2) | `segmentAtomic` | simulation |
| `mutantReplaceMidSegment` | Send a `Replace` snapshot while a segment runs (R2, R31) | `segmentAtomic` | simulation |
| `mutantAppendAfterReplaceRequired` | Send `Append(staged)` after `ReplaceRequired`, dropping the refused write (R31) | `nilExternality` | simulation |
| `mutantReplaceDropsRefused` | Send `Replace(acceptedMem)` after `ReplaceRequired`, dropping the refused write while covering `sent` (R31) | `nilExternality` | simulation |
| `mutantFencedDropsWrite` | Advance `accepted` on `Fenced` instead of resending (R14) | `nilExternality` | simulation |
| `mutantConcurrentSegments` | Start a segment while another runs | `turnExclusive` | simulation |
| `mutantCancelDiscardsStaged` | Discard staged commands when a turn is cancelled (R11) | `nilExternality` | simulation |
| `mutantReleaseZombie` | Release a terminated activation's messages, as without lease checks | `nilExternality` | simulation, `zombieReleaseTest` |
| `mutantNoDedup` | Run the inbox handler again for a completed message | `effectivelyOnce` | simulation |
| `mutantSplitHandler` | Write a handler's effect and its completion in separate segments | `effectivelyOnce` | simulation, `splitHandlerTest` |

Two more rules are checked by directed scenarios rather than the mutant gate, because random simulation does not reliably reach the ~30-step ambiguous-handler path that shows them:

| Variant | Broken rule | Violates | Caught by |
| --- | --- | --- | --- |
| `landLateUnchecked` | An ambiguous write lands without Clog's version check | `effectivelyOnce` | `noVersionCheckTest` |
| `rejectRebaseUnchecked` | Rebase expected version on `Rejected` without checking the content digest (R14) | `effectivelyOnce` | `rebaseWithoutDigestCheckTest` |

## Findings

- **Handlers need not wait for durable acceptance.** `inboxRun` starts a handler as soon as the acceptance is in memory, and `safety` holds: the handler's effect follows the acceptance in the same journal, so any history that keeps the effect keeps the acceptance, and the acknowledgement is gated by nil-externality.
- **The effect and completion must share a segment.** `mutantSplitHandler` shows that a handler whose effect and completion can land in different entries applies its effect twice after a crash between them.
- **Clog's version check and R14 content digest make ambiguous writes and version rebases safe.** After an `Ambiguous` handler write, a new activation may run the handler again; whichever of the two writes lands first wins, and the version check rejects the other (`ambiguousLateLandingTest`). Without the version check (`noVersionCheckTest`) or without verifying the accepted content digest before rebasing on `Rejected` (`rebaseWithoutDigestCheckTest`), the effect applies twice.
- **`ReplaceRequired` must block further `Append`s until a full-state `Replace(mem)` lands.** Sending either an `Append` of newly staged commands (`mutantAppendAfterReplaceRequired`) or a `Replace` omitting the refused write (`mutantReplaceDropsRefused`) breaks `nilExternality`.

## Running

`grainRuntimeSim` configures random simulation and the directed scenarios; `grainRuntimeTlc` is a smaller configuration for exhaustive checking.

```sh
quint test grainRuntimeSim.qnt --main=grainRuntimeSim --match=Test
quint run grainRuntimeSim.qnt --main=grainRuntimeSim --backend=rust --invariant=safety --max-steps=50 --max-samples=100000
bash ../check-mutants.sh grainRuntimeSim.qnt grainRuntimeSim safety --backend=rust --max-steps=50 --max-samples=50000 --seed=7
quint verify grainRuntimeTlc.qnt --main=grainRuntimeTlc --backend=tlc --invariant=safety
```

CI runs the tests, simulation, and mutants on every pull request that touches this directory, and exhaustive TLC checking on `main` and weekly ([workflow](../../.github/workflows/specs-grain-runtime.yml)).

## Results

- Directed scenarios: all eleven pass.
- Random simulation: no violation in 100,000 traces of up to 50 steps (`grainRuntimeSim`).
- Mutants: all twelve violate their named invariants within 20,000 traces.
- TLC: run by the CI job on `main` and weekly.

## Not yet modeled

- Liveness: every committed outbox message is eventually processed, under fairness.
- Bounded deduplication windows, several senders, and inbox retries and dead-lettering.
- Lease timing and Clog range transitions: modeled separately in [`../range-handoff/`](../range-handoff/README.md); durable jobs are modeled in [`../jobs/`](../jobs/README.md).
