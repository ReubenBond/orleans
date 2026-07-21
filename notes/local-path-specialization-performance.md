# Local-path specialization

Date: 2026-07-21

## Path matrix

The retained hosted-client response optimization already removes the largest
local-only envelope and routing sequence. The remaining paths have different
ownership constraints:

| Path | Request delivery | Response delivery | Required boundary |
| --- | --- | --- | --- |
| Hosted client to local grain | Cached activation receiver when warm | Direct callback completion | Request message persists through activation execution and timeout/cancellation races |
| Local grain to local grain | Cached activation receiver when warm | Calling activation callback/scheduler | Grain isolation and activation scheduling |
| Local system target | Exact local target | System-target scheduler | System-target ordering and lifecycle |
| Silo to silo | Connection and wire | Connection and wire | Serialization and wire format |
| External client | Gateway and wire | Gateway and wire | Serialization, gateway routing, reconnect/failover |

Local request delivery already bypasses `MessageCenter` once the
`GrainReference` receiver cache is warm. The request `Message` remains the
owner of invocation metadata, cancellation identity, timeout information,
request context, tracing, forwarding state, and response correlation while it
waits in an activation. Removing that envelope would require duplicating those
semantics in a second local invocation representation.

## Profile

A low-overhead hosted-client CPU trace completed 3.789M calls/s with zero lost
events. `InsideRuntimeClient.SendRequest` remained the largest directly mapped
runtime leaf at 163 ms of sampled CPU. The response path no longer constructed
or routed a response `Message`; callback lookup, destination update, and
completion were the remaining local response operations.

Raw traces and pvanalyze output are under
`Artifacts\Benchmarks\LocalSpecialization`.

## Rejected: attach callback directly to the local request

The candidate attached the already-created `CallbackData` to the local request
and used a reference-checked callback-registry removal during direct hosted
completion. This preserved registration for timeout, cancellation, status,
shutdown, and target-failure scans while avoiding key lookup on the successful
local response.

The first representation added a nonserialized field to `Message`. That
increased every message object by eight bytes and hosted allocation from
952 B/call to 960 B/call, so it was immediately replaced.

The second representation reused the existing nonserialized reply-receiver
slot. Forwarded and remote requests already clear or reconstruct that slot, so
remote behavior and wire bytes remained unchanged. The initial form performed
an extra receiver CAS and measured:

| Version | Hosted throughput |
| --- | ---: |
| Baseline | 3.800M/s |
| Direct callback pointer | 3.737M/s |
| Change | **-1.65%** |

A refined version installed only one reply object before publication and
removed the response-side clear. It restored 952 B/call but averaged
3.732M/s across four iterations, still below the paired baseline. The direct
callback registry is already a 1,024-slot CAS table; replacing its validated
lookup did not offset the extra ownership special case.

Reference-removal collision and stale-replacement tests passed while the
candidate was present. Remote silo and external-client controls remained
functional. All candidate runtime and test changes were removed.

## Conclusion

No runtime change is retained. Hosted responses already use the profitable
local specialization. Warm local requests already bypass general routing, and
their request envelope cannot be removed without replacing metadata,
forwarding, timeout, cancellation, and tracing ownership. The next
investigation should profile the remaining response construction/copy costs
without assuming that another local routing layer exists.
