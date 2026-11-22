# Linearizability checker

A C# port of [Porcupine](https://github.com/anishathalye/porcupine)'s linearizability checker and visualizer, at
commit `97cd067defd99df172053ba7f4ca5bbae57fe446`, maintained as Clog code
([verification §3.4](../../../docs/spec/verification.md#34-linearizability)). Porcupine is Copyright (c) Anish Athalye,
under the MIT License in [`PORCUPINE-LICENSE.md`](PORCUPINE-LICENSE.md).

A test project links the sources and embeds the visualizer's assets:

```xml
<Compile Include="..\shared\Linearizability\*.cs" LinkBase="Linearizability" />
<EmbeddedResource Include="..\shared\Linearizability\Visualization\*"
                  LinkBase="Linearizability\Visualization"
                  LogicalName="Orleans.Durable.Storage.Testing.Linearizability.%(Filename)%(Extension)" />
```

## Sources

| File | Porcupine source |
| --- | --- |
| `HistoryOperation.cs`, `HistoryEvent.cs` | `Operation`, `Event` (`model.go`) |
| `LinearizabilityModel.cs` | `Model` (`model.go`) |
| `LinearizabilityChecker.cs`, `LinearizabilityVerdict.cs` | `checker.go`, `porcupine.go`, `bitset.go`, `CheckResult` |
| `LinearizabilityAnalysis.cs` | `LinearizationInfo`, `Annotation` (`checker.go`, `visualization.go`) |
| `PorcupineVisualization.cs` | `computeVisualizationData`, `Visualize` (`visualization.go`) |
| `Visualization/index.html`, `index.css`, `index.js` | `visualization/`, unchanged |
| `HistoryRecorder.cs`, `JournalModel.cs`, `LinearizabilityAssert.cs` | Clog's own: recording, the journal model, and the test assertion |

## Differences from Porcupine

- **Scheduling.** Porcupine searches partitions in parallel goroutines. The port searches them on the calling thread
  in turns of `LinearizabilityChecker.Quantum` steps, round robin in partition order, so a check is deterministic.
  `Check` stops at the first illegal partition, as Porcupine's does; `CheckVerbose` searches every partition to its
  end, as Porcupine's does, unless `stopAtFirstIllegal` is set.
- **Cancellation.** A `CancellationToken` replaces the timeout duration; a cancelled search is `Unknown`.
  `StepContext` is not ported: models step synchronously, and the search observes cancellation between steps.
- **Partitions.** A model's `PartitionComparer` groups inputs into partitions, in the order of each partition's first
  operation, replacing `Partition` and `PartitionEvent`.
- **Events.** An event history becomes operations timed by each event's position in the whole history, rather than
  in its partition, so partitions share one timeline in the visualizer.
- **Order of equal times.** Entries at one time sort invocations before responses, as Porcupine's do, then by
  operation, where Porcupine's unstable sort leaves them in any order.
- **Cache.** The linearized set's hash is a Zobrist hash updated as operations are linearized and backtracked.
- **Not ported.** `NondeterministicModel` and its power-set construction.

## Validation

`Orleans.Durable.Storage.Testing.Tests` checks Porcupine's published key-value histories
([`tests/shared-vectors/linearizability`](../../shared-vectors/linearizability/README.md)), ports Porcupine's
hand-written checker and visualization tests (including the exact visualization data of
`TestVisualizationMultipleLengths`), and checks generated histories with injected violations.
