# Design

- **Spec first.** New or changed behavior has a stated contract before code; stateful components model it as client-visible state and actions (a state machine) ([Lampson](https://github.com/ReubenBond/research-papers/blob/main/Hints_and_Principles_for_Computer_System_Design/Hints_and_Principles_for_Computer_System_Design.md)).
- **Interfaces are contracts.** An interface documents what callers rely on beyond its signature: behavior, failure modes, ordering, and resource or performance assumptions, as they apply.
- **Modularity.** Each module hides a design decision likely to change, so callers never depend on its representation ([Parnas](https://github.com/ReubenBond/research-papers/blob/main/On_the_Criteria_To_Be_Used_in_Decomposing_Systems_into_Modules/On_the_Criteria_To_Be_Used_in_Decomposing_Systems_into_Modules.md)).
- **Composable primitives.** Small primitives compose, and a composite preserves the contract it implements.
- **Layering.** Dependencies point down, toward the core and abstractions; each layer exposes lower-layer capabilities through its own contract or an explicit extension point, and code that bypasses that contract to reach a lower layer is a layering violation.
- **Weakest sufficient contract.** A layer guarantees only what callers require and it can enforce; callers needing more compose it (for example, effectively-once processing from retried at-most-once calls plus idempotent handlers).
- **End-to-end.** Reliability, security, deduplication, and ordering are verified by the endpoints that observe the complete outcome; lower-layer guarantees reduce cost and are correctness dependencies only where a contract states them ([Saltzer et al.](https://github.com/ReubenBond/research-papers/blob/main/End-to-End_Arguments_in_System_Design/End-to-End_Arguments_in_System_Design.md)).
- **Virtualization.** Application code addresses entities by logical identity; infrastructure code reaches physical instances only through placement, directory, lifecycle, and migration contracts ([Orleans](https://github.com/ReubenBond/research-papers/blob/main/Orleans_Distributed_Virtual_Actors_for_Programmability_and_Scalability/Orleans_Distributed_Virtual_Actors_for_Programmability_and_Scalability.md)).
- **Mechanism vs policy.** Lower layers supply mechanisms; policy that callers have a concrete need to choose is exposed through options, strategies, or providers.
- **Single source of truth.** Each piece of state has one authority at a time, with transfers governed by versions or epochs; caches and hints are derived and refreshable from that authority.
- **Reuse proven patterns.** Logs, state machines, epochs and ETags, leases, idempotency keys, sharding, and batching are applied where their guarantees and costs fit the problem.
- **Divide and conquer.** Where scale or contention requires concurrency, state is sharded by key and work pipelined into stages before shared locks are considered.
- **Reconfiguration is a protocol.** Ownership and topology changes seal the old configuration, define the cutover point, and switch via one authoritative versioned record.
- **Platform first.** Use the platform's standard library and first-party libraries before building our own: in .NET, the BCL and Microsoft.Extensions (hosting, dependency injection, options, logging, metrics, resilience, `TimeProvider`, object pools, hashing). A component of our own replaces one of them only for a requirement it cannot meet, recorded with the rationale. A third-party dependency is justified by a requirement the platform and existing dependencies cannot meet.

## Compatibility

- **Shipped surfaces are contracts.** Shipped public APIs, serialized contracts, and persisted state and storage schemas evolve additively; breaks and migrations follow an explicit, approved process.
- **Rolling upgrades.** Changes to messages and persisted data interoperate across the supported rolling-upgrade window, tested in both directions (old writer to new reader, and new writer to old reader).
- **Observable behavior.** Documented behavior, and observable behavior users plausibly depend on, changes deliberately with its impact stated ([Hyrum's law](https://github.com/ReubenBond/research-papers/blob/main/Hyrums_Law/Hyrums_Law.md)).
- **Unshipped surfaces.** APIs and formats introduced since the last stable release carry no shipped-compatibility obligation and stay consistent with in-repository callers and the approved design.
- **Uncertain status.** When shipped or persisted status is unclear, raise it as a question.

Worked guidance: [abstraction and virtualization](applied-principles.md#hide-decisions-behind-contracts) and [end-to-end guarantees](applied-principles.md#place-success-at-the-right-boundary).
