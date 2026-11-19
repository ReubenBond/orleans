# Operational observation

`Orleans.Durable.Diagnostics.DurableObservationSource` provides a live,
application-facing view of committed task, messaging, and job transitions.
`AddDurableTasks`, `AddMessaging`, and `AddDurableJobs` register the silo singleton.
Resolve it from dependency injection and subscribe with an
`IObserver<DurableObservation>`. Keep the returned subscription for the exporter's
lifetime and dispose it when the exporter stops.

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Durable.Diagnostics;

public sealed class OrderOperations(
    DurableObservationSource observations,
    ILogger<OrderOperations> logger) : IHostedService, IObserver<DurableObservation>
{
    private IDisposable? _subscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = observations.Subscribe(this, capacity: 512);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }

    public void OnNext(DurableObservation observation) =>
        logger.LogInformation(
            "{Owner} {Component} {Transition} committed={Committed} task={TaskId} message={MessageId} job={JobId} due={DueTime}",
            observation.Owner, observation.Component, observation.Transition,
            observation.IsCommitted, observation.TaskId, observation.MessageId,
            observation.JobId, observation.DueTime);

    public void OnError(Exception error) => logger.LogError(error, "Observation exporter failed.");
    public void OnCompleted() { }
}
```

Register the exporter using `services.AddHostedService<OrderOperations>()`.
Callbacks run on an independent worker for each subscription. An exporter can
perform synchronous I/O there; each subscription retains at most its configured
queue capacity plus its current callback. Subscriber exceptions are logged and
the worker continues with the next observation. Other subscribers keep their own
delivery queues and workers.

Keep `OnNext` synchronous so the worker owns each export through completion.
When an exporter offers an asynchronous API, wait for that operation to complete
inside this worker callback, or hand it to another explicitly bounded pipeline.
The worker then preserves the source's ordering and exception isolation.

## Commit and activity semantics

| Component | Committed transitions | Attempt activity |
| --- | --- | --- |
| `task` | `Created`, `Scheduled`, `Outcome`, `DelayScheduled`, `CancellationRequested`, `Deleted`, `InvocationSent`, `MessageApplied` | `AttemptStarted`, `AttemptStopped`, `WaitStarted`, `WaitEnded`, `AttemptStalled`, `Recovered` |
| `inbox` | `Accepted`, `AcceptedAndCompleted`, `Completed`, `RetryScheduled`, `Rejected`, `DeadLettered` | Existing Messaging diagnostics report execution activity. |
| `outbox` | `Enqueued`, `RetryScheduled`, `Retired`, `DeadLettered` | Existing Messaging diagnostics report delivery attempts. |
| `job` | `Scheduled`, `RetryScheduled`, `Retired`, `Completed` | `AttemptStarted`, `AttemptStopped` |

`IsCommitted` is true only after storage acknowledges the containing write.
Owners capture immutable transition metadata with the write, then publish its
captured prefix through `IStateMachine.OnWriteCompleted`. Changes staged while
that write waits for acknowledgement belong to the next write. A failed write
leaves its staged transitions unpublished, and the journal owner fences that
activation. Journal preparation retries retain their pending transitions.

`Outcome` carries the task's runtime-defined terminal status in `Detail`.
`Scheduled` includes the invocation's persisted incarnation and caller after
the runtime establishes them. A `Created` observation describes the record's
initial identity and kind. `Retired` means that the owning outbox or job shard
committed removal; a job's `Completed` follows acknowledgement of removal after
its handler reported completion. This proves the scheduler's committed outcome.
Applications establish external business effects through their own durable
protocols.

An inbox's `Rejected` records a committed processed rejection.
`DeadLettered` identifies a retained inbox dead letter. These transitions
distinguish the processed outcome from the inbox's dead-letter inventory.

Activity has `IsCommitted=false` and describes a particular attempt or recovery
pass. A `WaitStarted` identifies the waiting execution in `TaskId` and its awaited
task or signal record in `RelatedTaskId`. A signal wait is local execution
activity; the delivered signal's recorded outcome is a committed transition.
`AttemptStalled` carries the runtime-defined unsupported-version or divergence
reason in `Detail`. A later activation publishes `Recovered` identities and starts
a new attempt.

The synchronous [`DurableTaskEvents`](../DurableTasks/Diagnostics/DurableTaskEvents.cs)
and Messaging diagnostic listeners remain staging and fault-injection boundaries:
their callbacks run on the activation and their exceptions propagate to the
operation. Operational subscribers use the separate bounded source above.

## Identity, ordering, and metadata

An observation contains immutable identity strings, a subsystem timestamp,
runtime-defined detail, and an applicable due time. Payloads, exception details,
and custom headers remain redacted. Applications can explicitly join authorized
business data in their exporter.
Treat identifiers as potentially sensitive and apply the application's own
access control and redaction policy before external export.

- `Owner`, `Component`, and `Epoch` identify a publication sequence. `Sequence`
  increases within it, including gaps for dropped producer observations.
  Each subscriber preserves that order for the records it receives. Correlate
  different owners by protocol identity and causation.
- Tasks carry `TaskId`, their parent or awaited `RelatedTaskId`, and their
  applicable persisted `Incarnation`. `WireTaskId` connects a caller's child to
  the callee's scoped task using the persisted incarnation and caller identity.
  Recovery derives the same key from those protocol fields. `InvocationSent`
  and `MessageApplied` link the task and message identities at their owning
  boundaries. `DelayScheduled` includes the actual persisted resume message's
  identity and due time.
- Messaging carries its persisted `MessageId`. An ordinary outbox message
  includes its receiving grain; reference sends use the task observations for
  their task/target relationship.
- Jobs carry their persisted `JobId`, target grain, due time, and available
  persisted W3C `TraceParent`. Attempt activity also includes the runtime's run
  identity in `AttemptId`. A shard's committed sequence and each execution
  attempt have independent epochs.
- `WriteVersion` numbers acknowledged writes since that component's observation
  buffer was created or reset. It is local to the epoch; use durable inspection
  for journal positions and history.

Identity strings longer than 512 UTF-16 code units are represented as
`sha256:` followed by the uppercase SHA-256 digest of their UTF-8 encoding.
The same identity maps to the same bounded correlation key. Runtime-defined
transition/detail names and W3C trace parents have fixed bounds. Every
observation retains only these immutable diagnostic values.

## Buffering and reconciliation

The producer retains at most 256 staged observations per component owner,
including the prefix awaiting acknowledgement. Each subscriber defaults to a
256-record queue and can choose a capacity from 1 through 65,536. Full buffers
drop the incoming observation and preserve the queued prefix. Budget memory
for active observed owners, subscriber capacities, and one in-flight callback
per subscriber. Subscribe only the exporters the application needs.

`DroppedCount` reports aggregate producer loss and subscriber delivery loss on
the silo; each subscriber's lost delivery counts separately. Producer losses
become visible when their containing write acknowledges. Subscriber overflow
is logged when that worker resumes. The standard .NET meter
`Orleans.Durable.Observations` exposes the same count as
`orleans.durable.observations.dropped`. A disabled source constructs no transition
payloads.

The live feed has an explicit crash gap between storage acknowledgement and
publication, and another between publication and an exporter's delivery.
Durable inspection supplies the source of truth after either gap or overflow.
A separately designed journal consumer can supply complete historical delivery.

On startup, reconnect, an epoch change, or a changed drop count, read the
application's committed state and task status, then reconcile the live projection
against that state. `Recovered` is useful live activity and can itself be lost.
Retain domain history in a durable log when the application requires historical
reconstruction. The feed's epochs and sequences describe delivery, while the
durable protocol establishes business state.

## Example: follow an order to shipment

The [durable order playground](../../../playground/DurableOrderProcessing/README.md)
provides a concrete workflow: `place-order-{orderId:N}` delays through a grace
period, reserves stock through durable calls, authorizes payment, then waits for
the `shipped` signal.

| Observation | Operator interpretation and next responsible action |
| --- | --- |
| Order task `Scheduled`, followed by `AttemptStarted` | The invocation committed; this attempt executes the workflow. |
| `DelayScheduled` for `.../grace-period`, with resume `MessageId` and `DueTime` | The order waits until the Durable Jobs clock reaches the recorded due time. The inbox holds the scheduled resume. The wake-up job's target identifies the order grain. |
| `InvocationSent` for `.../reserve/0`, then outbox `Retired` and receiver `MessageApplied` sharing its message identity | The stock call crossed its durable delivery boundary. Join the caller and callee by `WireTaskId`; the catalog grain now owns the work. |
| `Outcome` for the reservation and payment children | Those child outcomes committed. The order can advance from its recorded results. |
| `WaitStarted`, with the order execution and related `.../$signal:shipped` record | A paid order waits normally for shipment. `ShipAsync` is the next responsible action. |
| `MessageApplied` for a delivered signal, or the signal record's `Outcome`, then order `Outcome` | The signal outcome and then the order's terminal outcome committed. Read the order's committed history for its business shipment state. |
| `AttemptStalled` with `UnsupportedVersion` or `Diverged` | Code cannot replay this execution. Deploy compatible code and let recovery retry it; the task remains pending. |

Normal waiting has a named awaited operation, a due time, or a responsible
external signal. Diagnose a stall from its explicit runtime reason and
authoritative task state, alongside the application's expected progress policy.

For a feed-loss exercise, block one exporter with capacity one and let an order
complete. Its queue preserves an earlier observation and its dropped count
increases while the order's durable task and history continue advancing. Compare
the exporter's last delivered transition with
`GET /api/orders/{orderId}/workflow` and the committed history at
`GET /api/orders/{orderId}/events`. After deactivation, recovery preserves the
task/incarnation/wire identities and creates a new local observation epoch.
Rebuild the operator projection from those authoritative reads, and resume
live delivery.
