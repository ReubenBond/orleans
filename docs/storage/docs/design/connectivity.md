# Connectivity: one connection, one RPC system, every service

Status: **Decided** by the user; rollout to the layer documents pending. This proposal changes decisions N2, N3, N12, N17, N18, N21, N22, N26, and S1 ([Messaging](messaging.md), L2 branch), the L4 and L6 assumptions about messaging, and the layer order in [Layers](layers.md). Nothing in it has shipped.

## Problem

Several services must talk across processes before, or regardless of, cluster membership:

- **Seed-quorum MetaStore** (Fast CASPaxos, `clog-caspaxos`): stores cluster metadata registers, including `{cluster:N}/membership` in disk-native deployments. Membership cannot exist until it runs.
- **Storage (Clog):** replicated ledger writes/reads/seals, journal forwarding, and control-plane RPCs (`96`–`100`).
- **Rapid:** probes, alerts, consensus, lease promises, and joins from processes that are not members yet (`16`–`17`).
- **Grain calls** across cluster nodes.
- *(Deferred past v1)* **External clients**, which will reuse the same connection type with a `ClientHello` peer variant when external gateways (`Gateway` 18) are added.

Until now each kind of peer had its own connection mode: member, candidate, client, and, earlier, bootstrap. The handshake and dispatch checked membership: they queried `PeerAuthority`, held unknown dialers, dropped messages from defunct senders, and every header carried the sender's membership version. So messaging depended on membership, while the services membership depends on needed messaging.

## Idea

Messaging is a membership-free, authenticated RPC system between **processes**. A process is identified by its `InstanceId` (random `Id128`, chosen at start; [M30](membership.md)) and authenticated by its TLS certificate. Messaging neither carries membership versions nor checks callers against membership. Membership semantics belong to the layers that need them, which already enforce them at the sender:

- **Effects:** a node releases a response or message for a grain only while it holds that range's open-ledger replica lease (`ST8`, checked in L4 Runtime). Under ledger-replica leases, no successor ledger commits in `LedgerMap` before the closing ledger's lease ends or is surrendered, so a deposed primary stops sending before its successor starts.
- **Durable writes:** fenced by Clog ledger seals, MetaStore CAS, and `JournalVersion` admission (L2 Storage), whatever the clock does.

A receiver-side check against a defunct sender only narrowed a race the sender-side lease already closes. A message sent under a valid lease may still arrive after the sender's death commits, with or without the check, exactly as a delayed message from a live sender would.

## Design

### Identities

| Identity | Exists | Authenticated by | Used by |
| --- | --- | --- | --- |
| `InstanceId` | From process start | A TLS certificate chaining to the cluster's roots (`rustls`, S1) | Messaging: connections, dialing, newest-wins |
| `MemberId` | From the view where its `Join` commits | `Join` records `instance` and `tls_key_sha256` | Membership and above: grain addresses, routing, ownership |

A service that needs to know which member is calling resolves the connection's `InstanceId` through its own view. Messaging exposes the peer's `InstanceId` and key hash in the request context.

### Connections

- **One connection type** *(decided)*. The dialer sends requests and one-way messages, and the acceptor sends only responses, on the same connection.
- **Count.** Each process dials each peer process once, so a node pair has two connections, one per direction, as N12 decided.
- **Shared.** Every service shares a peer's connection: MetaStore, Clog storage, Rapid, joins, and grain calls. Lanes (N3) keep Rapid traffic ahead, and Clog's replica control lane keeps `ReadTail` seals and idle `Heartbeat` renewals ahead of bulk storage traffic.
- **Dialing.** By address. Addresses come from configuration (seeds, MetaStore acceptors), from a membership view (members' `NodeEndpoints`), or from a service's own configuration. Connections are keyed by the peer's `InstanceId`; dialing a configured address learns it from `HelloResult`.
- **Newest wins** per peer instance (N18). An acceptor reads all of a peer's connections on one thread, `instance(peer) mod T`, so newest-wins and per-thread order stay local to that thread. A `Hello` that arrives on another thread gets `CODE_WRONG_THREAD` with that thread's address in `HelloResult.redirect`. A dialer that knows the peer's `NodeEndpoints` dials the right thread directly, so the redirect costs one extra round trip only on first contact through a configured address.
- **Closing.** A connection closes on I/O errors, protocol violations, shutdown, or when a layer above asks: `close(instance)` fails that connection's pending calls with an unknown outcome. The grain runtime calls it when membership reports a peer defunct, so pending calls fail promptly.

Because responses return on the request's connection, no process needs another's endpoints to answer it. A process that has never joined can call a MetaStore acceptor or a member's Join target and receive the response.

### Handshake

One round trip, with no reference to membership:

```proto
message Hello {
  bytes cluster_id = 1;                    // Id128
  oneof peer {
    roux.id.v1.Id128 instance = 2;         // a Roux process
    // tag 3 reserved for deferred external ClientHello (N21)
  }
  roux.id.v1.Id128 target = 4;             // the instance the dialer expects; absent when dialing a configured address
}

message HelloResult {
  Code code = 1;                           // ACCEPTED, WRONG_TARGET, WRONG_CLUSTER, INCOMPATIBLE, UNAUTHORIZED
  uint32 protocol_version = 2;
  roux.id.v1.Id128 acceptor = 3;
}
```

The acceptor checks the cluster, the protocol version, the target, and the TLS certificate, and answers immediately.

### Header

The header keeps routing and RPC fields and drops membership:

- **Kept:** `kind`, `message_id`, `dest_thread`, the target (grain or system target), `interface`, `method`, `deadline_us`, `status`, `location` (a moved grain's address, on `NOT_HERE`), and `context`.
- **Added:** `Status.view_version`, set on `NOT_HERE` only.
- **Removed:** `membership_version`, `origin`, and `sender_thread`. Responses travel back on the request's connection, to the thread that sent the request, which the connection's pending-call table records.
- **Misrouted requests** *(decided)*. A node that receives a request for a grain it does not host returns `NOT_HERE`, with `location` when it knows it and always with `view_version`, the version of the responder's latest applied view. The caller resends to `location`, or once it has applied `view_version`, within the call's deadline. No message names a sender other than the connection's peer. `hops` stays as the loop bound.
- **Membership versions on the wire** appear only there, and in membership's own messages.

### Targets

A target is a grain interface or a system target. Every authenticated cluster process may call any target. A system target registers a target ID from its layer's range, a lane, and a thread rule (for example, MetaStore acceptors on thread 0). Senders put that thread in `dest_thread`. Every service uses the same header, codec, deadlines, status codes, and `NotSent` semantics. Responses match a pending call by message ID.

Services fence themselves: the MetaStore by ballots, Clog ledgers by seals, Rapid by configuration IDs, and grains by leases and ownership (`NOT_HERE`).

### Layer order *(decided: Roux on Clog)*

Messaging (`roux-net`) depends only on L0 network and `bytes::Bytes`; Clog storage (`clog-caspaxos`, `LedgerReplica`, `JournalForward`, `ClusterControl`, `NodeControl`), Membership, and the Grain runtime all run on top of it:

```text
L0 Substrates        cloud object storage, local NVMe, OS network
L1 Messaging         roux-net: connections, handshake, RPC (over bytes::Bytes, TCP + rustls)
L2 Storage (Clog)    6 clog-* crates (clog-metastore, clog-caspaxos, clog-wal, clog-ledgers, clog-archive, clog-journals) + roux-storage
L3 Membership        roux-membership: Rapid views (persisted in clog-metastore), failure detection, endpoints
L4 Grain runtime     roux-runtime + roux-jobs: durable activations, turns, nil-externality, durable jobs, inbox/outbox
```

`PeerAuthority` and `PeerEvents` are removed between membership and messaging.

### Startup

| Step | Cloud MetaStore deployment (`ObjectStoreMetaStore`) | Disk-native MetaStore deployment (`clog-caspaxos`) |
| --- | --- | --- |
| 1 | The process chooses an `InstanceId`, starts listeners and the RPC system | Same |
| 2 | — | Seed nodes serve `MetaStoreCasPaxos` (96) at configured addresses |
| 3 | Process opens its local `LedgerStore` in `clog-wal` (reads or creates `NodeId` UUID in `wal/node`), registers `LedgerReplica` (97) and `NodeControl` (100), and connects to `clog-metastore` | Same, connecting to `clog-metastore` via `clog-caspaxos` over L1 Messaging |
| 4 | Membership loads the latest view from `clog-metastore` and calls `Join` (17) on a live member | Membership calls `Join` (17) on a configured seed |
| 5 | The view with its `Join` commits to `clog-metastore`; the process learns its `MemberId` and registers in Clog's `NodeRegistry` (`CLNR`) | Same |
| 6 | Clog `ClusterController` hands off or assigns dyadic `KeyRange` ledgers; grain traffic shares the existing connections | Same |

No step reconnects or changes connection mode.

## What this removes

- **Connection modes:** member, candidate, client, and bootstrap modes collapse into one.
- **Membership in messaging:** `PeerAuthority`, `PeerEvents`, the per-thread defunct table and dispatch check, handshake holding and its results, `observe_version`, the header's membership version and `origin`, and the specification's defunct-sender invariant.
- **Reconnects:** a joiner keeps its connections when it becomes a member.
- **Dial-back:** acceptors don't need a dialer's endpoints to respond.
- **The layering inversion** that review 2 flagged.

## What it changes

| Decision | Change |
| --- | --- |
| N2, S1 Handshake | `Hello` carries an `InstanceId` or client credentials; no membership check. A certificate chaining to the cluster's roots authorizes a process; `Join` still records its key hash for services that want to bind members to keys |
| N12 Topology | Responses return on the request's connection instead of the reverse connection |
| N17 Peer authority | Withdrawn |
| N18 Lifecycle | Connections keyed by peer `InstanceId`, dialed by address; `close(instance)` for layers above |
| N21, N26 | Clients use the one connection type, restricted to client-callable targets; the candidate mode is withdrawn |
| N22 Header | No `membership_version`, `origin`, or `sender_thread`; `NOT_HERE` carries `view_version` |
| L2 contract | Order per (sender process, destination thread, lane), at most once, `NotSent` has no effect, authenticated peer; no membership clauses |
| L4 assumptions | Drop "handshakes reject defunct identities" and "every header carries the sender's `MemberId` and membership version". Non-durable single activation rests on L2 ledger-replica leases (`ST8`), at the sender |
| L6 | Fails pending calls to a defunct member by closing its connections; resolves a caller's `MemberId` from the connection's `InstanceId` when it needs one |
| User requirement | "All messages should carry the current membership version of the sender" is withdrawn; versions travel in membership's own messages and on `NOT_HERE` |
| System targets | Logs, including the MetaStore, claim a range, for example 96 to 127 |
