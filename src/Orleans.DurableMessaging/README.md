# Microsoft Orleans Durable Messaging

This intermediate project supplies durable command identities, subjects, envelopes, handler contracts,
and journaled inbox processing.

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
