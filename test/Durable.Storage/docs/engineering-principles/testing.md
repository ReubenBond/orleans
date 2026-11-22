# Testing

## Oracles and levels

- **Oracles come from the spec.** Each contract has one executable oracle, written once and reused at every level:
  a reference model for each stateful contract, invariant checkers for global properties, the slow path for every
  optimization, and linearizability for concurrent clients.
- **The sequence is separate from the verdict.** Generators (model exploration, property-based generators,
  simulation workloads, hand-written scenarios, production traces) choose what to run; the oracle decides whether
  the observed results are allowed.
- **The slow path is the oracle for the fast path.** An optimization (a cache, an index, a single-replica read) is
  correct exactly when it returns what the reference path returns; tests compare the two.
- **Right level.** A testing pyramid: unit tests cover pure logic, integration tests cover runtime boundaries,
  shared contract kits cover pluggable implementations, and end-to-end tests cover the composition. New tests follow
  the containing suite's conventions.
- **Cheap first.** Fast suites gate every change; exploration, full mutation runs, and fuzzing campaigns gate
  milestones; soak runs gate only a finished system.

## Techniques

- **Example tests** pin specific regressions and scenarios. Every bug gets one.
- **Formal models (Quint, TLA+)** check protocol safety in every reachable state of a finite model; each invariant
  has a mutant which disables its guard and must produce a counterexample. **Trace validation** replays model traces
  against the implementation, and checks implementation traces against the model's actions, so the code is held to
  the model.
- **Property-based tests ([CsCheck](https://github.com/AnthonyLloyd/CsCheck))** cover invariants, round-trips,
  canonical encodings, and algebraic laws with reproducible seeds and effective shrinking. **Metamorphic
  properties** check that transformations which must not change an answer do not (an order of merges, a cache on or
  off, a different replica).
- **Model-based tests ([Accordant](https://www.nuget.org/packages/Microsoft.Accordant))** check stateful
  components against a simpler reference model across operation sequences. `Expect.OneOf` models ambiguous outcomes,
  step functions model background work, and concurrent windows are checked for linearizability. Paired with CsCheck,
  the Accordant spec is the oracle (`spec.Allows`, `AllowsConcurrent`) for sequences CsCheck generates and shrinks.
- **Deterministic simulation** runs the composed system in one process under controlled scheduling, virtual time,
  seeded randomness, and a simulated network, with faults injected at every I/O boundary
  ([Clockwork](https://github.com/ReubenBond/Clockwork),
  [FoundationDB](https://github.com/ReubenBond/research-papers/blob/main/FoundationDB_Simulation_and_Testing/FoundationDB_Simulation_and_Testing.md)).
  Every invariant is checked after every step; every failure replays from its seed and minimizes. Production code
  routes every source of nondeterminism through a seam the simulation replaces.
- **Linearizability checking** of long client histories uses
  [Porcupine](https://github.com/anishathalye/porcupine)'s algorithm, partitioned by key where keys are independent.
- **Fault injection** exercises changed failure boundaries: storage, transactions, networks, ambiguous outcomes,
  and node stops and kills.
- **Safety and progress phases.** Failure tests check safety while faults are active, then restore the protocol's
  progress assumptions and verify completion under a stated bound
  ([Paxos Made Live](https://github.com/ReubenBond/research-papers/blob/main/Paxos_Made_Live_An_Engineering_Perspective/Paxos_Made_Live_An_Engineering_Perspective.md)).
- **Crash-state enumeration** recovers from every crash state the file system's persistence model allows (unsynced
  writes lost or reordered, renames, directory fsyncs) and checks that no acknowledged data is lost and that every
  answer given before the crash point holds after recovery (after ALICE and CrashMonkey).
- **Coverage-guided fuzzing** of every decoder of durable or wire data (SharpFuzz with libFuzzer), with corpora
  seeded from golden vectors and replayed in ordinary tests: any bytes are rejected or canonically accepted, without
  crashes or hangs.
- **Golden vectors** pin every durable and wire format byte for byte.
- **Snapshots ([Verify](https://github.com/VerifyTests/Verify), pinned to a release before its maintenance fee)** pin
  human-readable renderings and generated output; baseline changes are reviewed as semantic changes.
- **Mutation testing ([Stryker.NET](https://stryker-mutator.io/docs/stryker-net/introduction/))** checks that the
  suites detect behavioral change, with a break threshold on core projects; surviving mutants are killed by new tests
  or recorded as equivalent.
- **Leak checks** at teardown, where they matter most: pooled buffers returned, handles closed, and no background
  work outliving its owner.
- **Soak runs** confirm steady throughput and latency and the absence of leaks over long runs, as final validation.
- **Compatibility tests** cover changes to shipped serialization or persisted data in both directions the
  rolling-upgrade window requires.

## Assertions

- **Contract assertions.** Assertions check the observable contract exactly: precise where the contract is precise
  (values, ordering under controlled time), and silent on incidental details.
- **Reasoning first.** Randomized testing finds bugs but cannot prove their absence; a clear model of the invariants
  and assertions in code establish correctness, and tests confirm it.

Worked guidance: [history-based oracles and failure cut points](applied-principles.md#test-histories-and-failure-cut-points).
Clog's methodology, layers, and gates: [verification](../spec/verification.md).
