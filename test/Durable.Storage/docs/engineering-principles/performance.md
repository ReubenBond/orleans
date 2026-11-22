# Performance

- **Back-of-envelope at design time.** The largest wins come from design, so new designs and scale-dependent changes start from an estimate of network, disk, memory, and CPU cost.
- **Slowest resource first.** Effort goes to the few bottleneck operations, identified by measurement and weighted by frequency.
- **Tail latency.** Fan-out and background contention are evaluated through end-to-end latency percentiles under representative load ([The Tail at Scale](https://github.com/ReubenBond/research-papers/blob/main/The_Tail_at_Scale/The_Tail_at_Scale.md)).
- **Bounded speculation.** Retries and hedges share explicit attempt, concurrency, and time budgets with the original request; duplicate execution and work continuing after cancellation preserve the operation's contract.
- **Batch.** Per-operation overhead (storage writes, messages, directory operations) is amortized across batches where latency, ordering, and partial-failure semantics permit.
- **Separate planes.** Control-plane work (membership, directory, placement) stays out of per-message and per-turn paths, which use cached decisions with defined refresh.
- **Run at your own pace.** A component that receives external events processes them in batches at its own rate, rather than doing full work per event as it arrives, where latency permits.
- **Simple algorithms for small N.** Where N is small in practice (per-entity collections, node counts), the simplest algorithm wins; scale-dependent N (entities, messages, subscriptions) justifies better complexity.
- **GC-aware hot paths.** On hot paths (messaging, serialization, scheduling), avoidable steady-state allocations are removed with pooled buffers, pre-sized collections, `Span<T>`, and structs where measurement shows net benefit.
- **Retained memory.** Copies, object sizes (including alignment), and retained object graphs are minimized, especially for per-entity and per-message state; optimizations account for count and lifetime and are justified by net benefit, ownership safety, and complexity.

Worked guidance: [fan-out, batching, and speculative work](applied-principles.md#budget-fan-out-and-speculative-work).
