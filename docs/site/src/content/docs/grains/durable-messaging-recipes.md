---
title: Durable messaging practical recipes
description: Implement stock reservation, idempotent payment, ordered projections, and durable notification fan-out.
ms.date: 10/10/2026
ms.topic: how-to
---

# Durable messaging practical recipes

These compiled examples use ordinary grains, named journaled state, and the
<xref:Orleans.DurableMessaging.IDurableMessagingGrain> capability. Configure
Journaling and Durable Jobs and call
<xref:Orleans.Hosting.DurableMessagingExtensions.AddDurableMessaging*> as shown in
[Durable messaging](durable-messaging.md#deployment-requirements).
Use persistent shared providers for deployments; the in-memory configuration is
suited to local execution.

All handler examples finish local validation and asynchronous preparation before
the first shared mutation. Typed sends encode before staging, followed by the
prepared business update. Their outgoing staging, shared update,
`Complete()`, and method return run synchronously. The inbox owns their journal
write. Ordinary methods explicitly await their application's journal write. Register one
non-generic handler per inbox and inject the outbox directly. Exact subjects identify
protocol operations; keyed <xref:Orleans.DurableMessaging.DurableMessageType`1>
bindings select their ordinary serializers. Typed outbox `Send` and `SendReply`
encode each body into an independently allocated GC-owned `byte[]` and stage an
ordinary envelope. Handler contexts and asynchronous operations keep the payload
reachable for as long as they use it. Published payload bytes remain immutable.

## Run the stock-reservation sample

The [Durable Messaging sample](https://github.com/dotnet/orleans/tree/main/samples/DurableMessaging)
runs an order grain and a stock grain in one localhost silo. Three independent
hierarchical command IDs exercise acceptance, insufficient stock, and invalid
quantity. For each command, the host arms an explicit journal-acknowledgement
observer before submission, awaits the typed reply's acknowledgement, and resubmits
the same immutable command. It verifies `Duplicate` admission for successful and
rejected commands, one handler execution per ID, unchanged stock for rejections,
and one stock decrement, without sleeps or polling.

From the repository root:

```powershell
pwsh .\samples\Build-Samples.ps1 -SkipExternalAssets
dotnet run --project .\samples\DurableMessaging\DurableMessaging.csproj --configuration Release --no-build
```

The sample uses locally packed Orleans packages while these APIs await publication.
Its README also describes the self-contained copy-out workflow. Volatile journals
and in-memory jobs support local execution; configure persistent shared providers
for restart recovery.

## Reserve inventory once per order line

Use one inventory grain per tenant/SKU with `available-stock`. The sender derives
the envelope's command ID using
[OrderOperationKeys](durable-messaging-idempotency.md#hierarchical-business-operation-keys),
and places the quantity and response destination in `ReserveStock`. The
[typed send helper](durable-messaging.md#encode-ordinary-application-values) encodes the
record under `inventory.reserve.v1` into an independently allocated payload array.

The inventory registers typed methods through
<xref:Orleans.DurableMessaging.DurableInboxExtensions.RegisterHandlers*>.
`inventory.reserve.v1` selects `ReserveStock` and its reservation method;
`inventory.restock.v1` selects `Restock` and its stock-increment method. One
dispatcher performs exact subject lookup and typed decoding, so each method
receives its application record directly. Registrations use method groups such as
`Register(reserve, HandleReserveStock)`. Synchronous dispatch checks cancellation at the
boundary before entering these methods.

These primary-constructor grains register routes in
<xref:Orleans.Grain.OnActivateAsync*>. Journal recovery restores durable state
before this method runs. Incoming requests and queued inbox pump turns begin
after `OnActivateAsync` completes, so recovered messages use the registered handlers.

:::code source="../snippets/compiled/Grains/DurableMessagingRecipes.cs" id="messaging_inventory" language="csharp":::

The reservation method computes its next stock and result locally. `SendReply`
encodes and stages the deterministic `result` reply, then the method applies stock
and completes synchronously. The first command commits that stock decrement, reply,
and inbox completion together. A repeat with the same command ID recognizes the
retained completion fact and preserves the original handler effects.
The serialized abstract `ReservationResult` derives from `OrderOutcome`;
sealed `ReservationAccepted` and `ReservationRejected` records distinguish the
valid business result types and carry the original quantity and remaining stock.
A nonpositive quantity sends `ReservationRejected` with `InvalidQuantity`;
a shortage sends it with `InsufficientStock`. Both complete with unchanged stock.
Invalid immutable reservation commands are normal durable business rejections,
not exceptions to retry. The original outbox intent delivers the reply
within its configured delivery policy.

`Restock` has no business reply protocol. A nonpositive increment is permanently
unusable, so the handler calls
<xref:Orleans.DurableMessaging.IInboxHandlerContext.Fail*> with a clear reason
and returns before mutation. A positive increment computes the checked new stock
value before mutation, then commits that update with inbox completion. Checked
overflow still throws and follows the ordinary bounded processing retry policy.
Give each distinct
restocking operation its own stable command ID. The example's `SetAvailableAsync`
is an administrative absolute-stock update. In
an order workflow, add explicit confirmation, expiry, and release policies for held
reservations and keep a record indexed by the reservation command ID when those
operations need lookup. Give release its own leaf, for example
`{order}/inventory/{sku}/release`, and commit its outcome with stock restoration.
Authorize stock administration and derive the tenant/SKU grain identity at the
application's trusted entry point.

## Charge an external provider with a stable key

Register an <xref:Microsoft.Extensions.DependencyInjection.IServiceCollection>
implementation of the shown gateway contract. Its adapter maps the key to the
provider's idempotency header or request field and implements the provider's outcome
reconciliation protocol.

:::code source="../snippets/compiled/Grains/DurableMessagingRecipes.cs" id="messaging_payment" language="csharp":::

The provider call precedes shared journaled changes. The asynchronous handler
passes its token to the gateway, and the method checks cancellation after that
await before `SendReply` and the final result update. If the provider succeeds and activation
loss interrupts the local commit, a replacement attempt uses the same provider key.
The canonical envelope ID is also the provider idempotency key. After local
completion, the inbox recognizes a resubmission during retention. The result-query
state, deterministic reply, and inbox completion share one write.

Normalize currency and scope the command ID to the provider account and tenant.
Treat declined payments as completed outcomes; treat ambiguous provider responses
using the provider's same-key retry or query contract. Expose a status query, as this
grain does, so a caller can reconcile an uncertain response by the same command ID.
Retain provider idempotency facts for the full retry horizon; adapt key length with
a deterministic canonical-key hash when the provider requires it.
See [External side effects](durable-messaging-idempotency.md#external-side-effects)
for retention and reconciliation responsibilities.

## Converge an out-of-order stock projection

A dashboard or search projection can accept complete stock snapshots independently
of transport ordering. The authoritative inventory producer assigns a monotonically
increasing version for the same tenant/SKU.

:::code source="../snippets/compiled/Grains/DurableMessagingRecipes.cs" id="messaging_projection" language="csharp":::

If version 12 arrives before version 11, the projection retains version 12 and
completes version 11 as superseded. A repeated version binds to its original content.
The version and complete stock value are one durable record. A stable message ID
recognizes the completed snapshot command; the domain version orders different
snapshot commands.

This recipe applies to **complete replacement snapshots**. Incremental debit, credit,
or stock-delta events use a contiguous-sequence policy with a journaled gap buffer
and a domain-defined recovery path for missing events.

## Publish a durable notification campaign

An ordinary method can stage several destinations with one business update. Give the
campaign a stable ID so a repeated client submission finds its recorded content and
recipient set.

:::code source="../snippets/compiled/Grains/DurableMessagingRecipes.cs" id="messaging_fanout" language="csharp":::

The campaign record and every outgoing intent are captured in the same sender
journal write. The typed batch
<xref:Orleans.DurableMessaging.DurableOutboxExtensions.Send*> helper enumerates the
recipient commands and sends each through the ordinary typed send helper.
Each message is encoded into its own array and staged independently.
The campaign record is added after all sends succeed. A later send failure leaves
earlier intents staged, and a repeated submission uses the same recipient command
identities and content.
The outbox keeps the envelopes and their arrays reachable through acknowledgement and delivery.
Each destination commits independently through the
[notification handler](durable-messaging.md#deployment-requirements). Each envelope's
ID is a stable recipient child under the campaign root, with literal grain identity
escaping handled by the key factory. The recipient's retained inbox completion
record recognizes that notification command. The campaign record also supports
`GetCampaignAsync` queries. A successful publish response
means the campaign and intents are durable; recipient completion is a later event.

Bound the recipient count and payload size at your ingress according to journal
capacity and latency budgets. For larger campaigns, persist a campaign cursor and
emit bounded recipient chunks, committing the cursor and each chunk together.
If delivery confirmation is needed, have recipients reply with a keyed outcome and
let the campaign grain aggregate those results. Retain campaign state for its
submission/query horizon and receiver completion facts for the supported
resubmission horizon.

## Combine the recipes into an order workflow

Use <xref:Orleans.DurableMessaging.DurableInboxDispatcher> when the coordinator
receives several subjects. This example registers typed inventory and payment
result delegates once and records outcomes by their deterministic reply IDs for
progress and result queries:

:::code source="../snippets/compiled/Grains/DurableMessagingRecipes.cs" id="messaging_dispatcher" language="csharp":::

The dispatcher matches subjects ordinally and invokes typed handler objects
containing each binding and method-group delegate. Configuration
freezes the routes before installation. Asynchronous routes return their actual
handler `ValueTask`.
Each delegate explicitly completes in the same synchronous block as its shared
mutation and actual method return. Unknown subjects and decode failures enter
processing retry/dead-letter policy before mutation.

A per-order coordinator persists its expected step keys and phase, sends
inventory and payment requests, then completes the initiating command. Each
response records its outcome and completes in one local commit. Once all required
steps have succeeded, one transition emits `shipment/create`.

| Phase | Durable transition |
| --- | --- |
| Submitted | Save the expected leaf keys and emit stock and payment requests. |
| Waiting | Save each correlated participant result under its reply ID; the inbox recognizes repeated reply commands. |
| Ready | Record the ready phase and emit the separately keyed shipment request. |
| Rejected | Record the business rejection and emit release/refund commands for completed participant steps. |
| Finished | Record shipment or compensation outcomes and retain workflow state for result queries and audit. |

Each transition uses the same prepare-then-synchronous-final-block pattern. Keep
business rejection in the workflow state and reserve exception retry policy for
preparation failures. Use recorded step outcomes to answer status queries during outages.

## Choose an application migration pattern

Move existing application responsibilities into the handler and its typed payload
records, using the durable inbox/outbox commit boundary for outgoing intent:

| Existing application pattern | Durable messaging pattern |
| --- | --- |
| Request/reply grain method | Put the stable command ID and subject in the envelope and the response destination in its body; the handler stages a deterministic typed reply with completion. |
| Business update followed by a remote call | Stage the outgoing envelope with the business update; acknowledged outbox state drives delivery and retry. |
| External provider call | Prepare the provider outcome using the canonical command ID, then commit its local query state, reply, and inbox completion together. |
| Notification loop | Call typed `Send` for each stable recipient identity, record the campaign after successful staging, and await the journal write. |
| Related records and attachments | Put application records and `byte[]` attachments in one serializable message and use the typed send and handler helpers. |

Preserve command identity and recorded outcomes when moving application workflows.
Typed subject bindings and outbox send/reply helpers carry ordinary records in
GC-owned payload arrays.
Applications own subject contracts, reply routing, authorization, and ID construction.
Preserve payload contents through retries and use ordinary managed lifetimes. See
[Application payload evolution](durable-messaging-operations.md#evolve-application-payload-records)
for rolling application-record changes.

## Verify your handlers

Exercise same-ID resubmissions across senders, conflicting pending request reuse,
distinct child commands, out-of-order arrival, and cancellation during local
preparation. Assert business state, query outcomes,
outgoing envelopes, and completion together. Test activation recovery and ambiguous
storage/provider outcomes against the actual providers used in deployment.

The executable documentation examples exercise hierarchical key isolation,
typed reservation acceptance, zero/negative-quantity rejections, shortages,
polymorphic result round trips, reply-stage failure without stock mutation,
permanent invalid-restock dead-lettering, checked restock overflow,
provider-success/local-cancellation retry,
out-of-order projections, typed multi-subject dispatch, and ordinary composite-message
round trips. Verify the original reply's actual journal acknowledgement and
the exact completion-retention boundary when testing end-to-end resubmission.
For runtime guarantees and operating controls, see
[Durable messaging operations](durable-messaging-operations.md).
