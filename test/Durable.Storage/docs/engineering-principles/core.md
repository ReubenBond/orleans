# Engineering principles

## Priorities

- Rank goals as correctness (including safety) > simplicity > performance > convenience; seek a design that serves all of them before trading one away.
- Safety (no corruption, data loss, broken invariants, or security violations) precedes liveness.
- Ask the user (in reviews, raise a question) before relying on an invariant that is not yet established, changing a surface that may have shipped or been persisted, or adding a dependency outside the platform's standard and first-party libraries.

## Approach

- **Essence over accident.** The hard part is the spec and the conceptual model; code is its representation. Spend effort there.
- **KISS.** Simplicity is the hardest revision, not the first draft. Iterate until the design is obviously right.
- **YAGNI.** Build for today's requirements. "If in doubt, leave it out."
- **Smallest complete change.** Reuse existing patterns and helpers; keep unrelated refactoring and formatting out of scope.
- **Concrete requirements only.** Abstractions, inheritance, virtual dispatch, options, fields, properties, caches, and states are added only for concrete requirements or boundaries; values are derived from existing state when correctness and efficiency permit.
- **Grow, don't build.** Keep a running end-to-end skeleton; add function incrementally.
- **Zero debt in what ships.** Fewer features, each solid. Fix showstoppers when found.
- **Always say why.** Every non-obvious decision carries its rationale (comment, commit message, PR).

## Headlines

- **Design:** spec first; layers with explicit contracts in both directions; end-to-end checks; logical separated from physical; one authority at a time per piece of state; the platform's libraries before our own.
- **Compatibility:** unshipped surfaces carry no shipped-compatibility obligation; shipped public APIs, serialized contracts, and persisted data are contracts.
- **Correctness:** invariants, not fallbacks; fix a broken guarantee in the layer that owns it; make implicit guarantees explicit; assert them precisely; make every answer durable before it leaves; stop the process on a local storage failure; handle every expected failure.
- **Testing:** derive oracles from the spec, write each once, and choose the simplest technique that establishes each property — example, property-based, model-based, formal model, deterministic simulation, linearizability, fuzzing, crash-state enumeration, snapshot, fault injection; mutation testing checks the tests.
- **Performance:** estimate cost for new and scale-dependent designs; batch; measure the bottleneck.
- **Code craft:** simple control flow, smallest scope, precise names.
