# Microsoft Orleans Durable Messaging

This intermediate project supplies durable command identities, subjects, envelopes, and handler contracts.

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
