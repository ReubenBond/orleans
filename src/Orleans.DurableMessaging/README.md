# Microsoft Orleans Durable Messaging

This intermediate project supplies durable command identities, subjects, envelopes, handler contracts,
and journaled inbox and outbox processing.

`DurableEnvelope` carries an application-supplied `HierarchicalKey MessageId`, `SenderId`, `ReceiverId`,
ordinal `Subject`, and a non-null, garbage-collected `byte[] Payload`. Struct assignment shares the array;
ordinary Orleans deep copying and deserialization produce independent arrays. Applications keep command
contents stable after admission and use a separate array when independent mutation is needed.

The exact identity supplies receiver-local deduplication across senders and subjects. Applications namespace
commands and preserve their destination, subject, and body across resubmissions. Admission permits 1,024
UTF-8 bytes and 32 segments per canonical key, and 256 UTF-8 bytes per nonempty subject. Parents and children
are independent identities. Completion records supply deduplication for their configured retained lifetime.

`HierarchicalKey` is a readonly ordinal value with one immutable canonical backing path and cached process-local
hash. `Create` and `CreateChildKey` accept literal segments; `Parse` reads an escaped canonical path. `Append`
composes built hierarchies. Its default value is unset. Serialization stores canonical paths and reconstructs
hash and navigation state.

One nongeneric `IInboxHandler.HandleAsync` handles each inbox's commands. Its context exposes the envelope
and synchronous `Complete()`. Decode and validate local results, perform asynchronous work, construct outgoing
messages, and check cancellation before the first shared mutation. From that mutation through method return,
execute synchronously: apply safe-to-commit changes, send through an injected `IDurableOutbox`, call `Complete()`,
and return. The runtime owns actual persistence, acknowledgement, and attempt retirement.

`IDurableOutbox.Send` publishes the existing GC-owned payload alongside business mutations. Applications
keep its bytes unchanged after publication. RPC copying and deserialization isolate arrays. The
journal hook establishes its self-wakeup before capture; dispatch follows persistence acknowledgement.
Pending outbox restaging compares identity, sender, destination, ordinal subject, and bytes. Pending inbox
comparison permits a changed immediate sender for the same command. Completed duplicates acknowledge the
existing outcome; application results remain in business state or the original reply intent.

This project remains non-packable while runtime and hosting layers assemble the eventual
`Microsoft.Orleans.DurableMessaging` package.

The inbox accepts a command after DurableJobs confirms its wakeup and the journal commits the
envelope alongside its logical generation and exact returned physical job handle. Pending duplicates
compare ordinal subject and payload bytes while permitting a changed immediate sender. Conflicts
preserve the original pending command and fail before scheduling or mutation. Retained completed
commands acknowledge their existing outcome across valid sender, subject, and payload changes.

Admission and handler operations keep ordinary references to the published payload through their
actual outcomes, independently of caller-wait cancellation. `Complete()` stages inbox removal and
deduplication beside safe business changes; the handler continues to access its envelope after removal.
The runtime owns the subsequent journal write and acknowledgement. Preparation failures follow
bounded retry and dead-letter policy. Errors after Complete preserve the logical outcome through
persistence and are reported after acknowledgement.

Reusable non-interleaving timer turns carry immutable owner and operation snapshots. Stop closes
admission and drains actual operations before full deletion or scope disposal. Actual storage failures
retain their first cause; a fresh activation restores the persisted outcome.

The outbox uses six owner-bound standard durable collections and the existing sequence state.
The final journal capture hook confirms the provider-returned physical wakeup before capture;
the sequence state associates each message cohort with that exact generation and job handle.
Storage acknowledgement releases only the captured cohort for delivery. Messages staged during
storage await remain pending for a later write. Healthy ownership is reused across bursts and
retires at the configured idle deadline.

Outgoing delivery candidates keep ordinary envelope references through RPC or loopback delivery
and the journaled accounting write. Payload lifetime follows GC reachability; ordinary RPC
serialization and deep copying isolate the receiver's bytes. Remote batches keep their durable-attempt
token across timer turns; shutdown drains actual delivery and write outcomes before disposing that token source.

Specialized test-only composition exercises this outbox with actual Journaling and DurableJobs.
Ordinary receiver fixtures retain their isolated journaled collaborator. Public hosting, provider
cutover examples, package publishing, documentation-site integration and samples belong to the
final consumer layer. This intermediate project remains non-packable.

Outgoing state uses the exact application-supplied `HierarchicalKey` as its message identity,
attempt key, and dead-letter key. A retry reconstructs the same command key. New workflow steps
and fan-out recipients use distinct deterministic child keys, with fixed-depth identities built
from stable application facts.

Each pending key denotes one immutable intent: sender, destination, ordinal subject, and opaque
body bytes remain stable after publication. Equivalent repeated staging compares the durable
intent and preserves its original payload array. A conflicting destination, subject, or body fails
before replacement, capture, or scheduling.
After removal, the same command can be staged again; receiver completion supplies deduplication
within its configured retention horizon.
