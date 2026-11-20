# Observe an order

The order grain owns a small immutable `OrderStatus` projection: its order ID, business stage, and lifecycle ID. The saga
assigns a new projection as it prepares, ships, compensates, or completes. A completed order retains its
projection for the order's lifetime, independently of durable-task result retention. Use one order ID for
one business order. Replay preserves its furthest recorded stage.

## Read, watch, and wait

```shell
curl "$service/workflows/orders/order-1/status"
curl -N "$service/workflows/orders/order-1/status/watch"
curl "$service/workflows/orders/order-1/status/terminal"
```

In another terminal, run the order:

```shell
curl -X POST "$service/workflows/orders/order-1?failShipping=false"
```

The one-shot status endpoint returns the current committed projection. An order which has yet to start
has stage `NotStarted`. The watch endpoint sends newline-delimited JSON, beginning with a current snapshot
and then subsequent committed projections. Stages are serialized as numeric `OrderStage` values:
`NotStarted` (0), `Preparing` (1), `Shipping` (2), `Compensating` (3), `Completed` (4), and `Compensated` (5).
The terminal endpoint waits for `Completed` or `Compensated`, returning the same projection shape as the
one-shot endpoint. Request cancellation ends the HTTP operation and its remote enumeration.

An Orleans client uses the same application grain interface:

```csharp
var order = client.GetGrain<IWorkflowGrain>("order-1");
var status = new OrderStatusClient(order, logger);
var terminal = await status.WaitAsync(value => value.IsTerminal, cancellationToken);
```

`WaitAsync` evaluates the predicate in the caller, including for the initial snapshot, and disposes its
enumeration on success, predicate failure, or cancellation. It returns the first **observed** satisfying
projection. Durable workflow calls and messaging own business reactions which require every transition.

## Commit and snapshot boundaries

`GetOrderStatusAsync` takes the first event of `WatchOrderStatusAsync`, so reads and watches share the
same committed view. The built-in durable-value watch coordinates initial snapshot capture with
subscription registration in the activation's scheduler. A concurrent change is either in that snapshot
or a later update. A snapshot of staged state waits for the journal write's acknowledgement.

Each projection is one immutable value in the grain's journal. Its fields become visible together at an
acknowledged grain-write boundary. A projection spanning several durable collections should likewise be
materialized into one value at the business boundary and persisted with those collections in the same
acknowledged journal write.

Durable-value watches retain the latest committed projection and the bounded values awaiting a write
acknowledgement. Slow readers coalesce obsolete snapshots. `OrderStatusClient` requests batches of one,
and the HTTP endpoint awaits each serialized write and flush. Each subscriber therefore has constant-size
application buffering; a slow reader observes current progress.

## Reconnect and lifecycle

`OrderStatusUpdate` includes `SubscriptionId`, `Version`, and `IsResync`. The first update of each subscription
is a resync baseline. Each order lifecycle has a persisted `LifecycleId`; `NotStarted` uses the empty GUID.
A journal reset publishes `NotStarted` with `IsResync` set. A recreated projection has a new lifecycle ID,
so a slow reader still receives an explicit resync when the reset itself coalesces away. A consumer replaces its
displayed snapshot on resync. `Version` is the owning journal manager's activation-local write version,
comparable only inside the same subscription and lifecycle ID. Recovery and deletion/
recreation establish a fresh baseline. Clients retain the grain identity to reconnect.

The client serially disposes a failed or completed subscription before reacquiring a watch through that
grain reference. Updates from an abandoned subscription therefore cannot replace the new baseline.
`SiloUnavailableException`, `EnumerationAbortedException`, `ConnectionFailedException`, and request `TimeoutException` trigger reconnect.
Retries use exponential backoff from 100 ms to 3.2 seconds, resetting after a received snapshot, until the
caller cancels. Normal owner shutdown also reconnects. Authorization, decoding, application-contract, and
journal-failure exceptions surface to the caller. A failed journal manager is fenced; a new observation
attempt after the grain has recovered establishes the durable state.

Orleans remote-enumeration requests and heartbeats refresh activation activity while the caller is
actively waiting. Between requests, an idle subscriber can lose its enumerator through idle cleanup or
activation collection; its next request reconnects and resnapshots. Explicit deactivation and silo loss
also replace the activation. Enumeration disposal/cancellation removes the owner-side watch; Orleans
expires abandoned enumerators, and manager shutdown completes its watches.

## Application boundary and diagnostics

The application interface selects the fields clients receive. In a deployed service, authorize the
order identity at the HTTP/application-grain boundary using the application's tenant and access policy.
Keep business details needed for privileged operations behind that policy. This local sample uses its
existing open HTTP host.

`OrderStatusClient` logs disconnected grain identities and retry causes, and logs owner completion before
reconnecting. Orleans logs failed remote-enumerator disposal and exposes async-enumeration activity,
including the `Orleans.Runtime.AsyncEnumerableGrainExtension` diagnostic listener and its cleanup events.
Correlate repeated reconnects and disposal warnings with activation turnover and storage failures. Track
active HTTP requests and cancellation alongside those diagnostics when investigating retained subscribers.
Once HTTP streaming has begun, a terminal error aborts the response; a caller starts a new request only
after handling that failure.
