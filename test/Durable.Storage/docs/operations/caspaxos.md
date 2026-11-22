# Fast CASPaxos MetaStore operations

This guide covers running the Fast CASPaxos MetaStore: building and configuring its hosts, sizing
membership and quorums, keeping its SQLite state durable, maintaining and backing it up, and reading its
failures. The register contract (conditional writes, versions, ambiguous outcomes, key layout) is specified
in [MetaStore](../spec/components/metastore.md); the decision to use Fast CASPaxos is recorded in
[Fast CASPaxos MetaStore](../spec/components/metastore.md#8-mechanism-and-policy).

## Topology

A Fast CASPaxos cluster is a fixed set of host processes. Each member owns:

| Part | Identity | Durable state |
| --- | --- | --- |
| Acceptor | `AcceptorId`, unique in `Peers` | Acceptor SQLite database and its WAL |
| Proposer | `ProposerId`, a cluster-unique positive integer | Proposer allocator SQLite database and its WAL |
| Register frontend | One gRPC origin, listed in `Peers` and in clients' `endpoints` | — |
| Admin listener | One HTTP origin (`HttpEndpoint` in `Peers`) | — |

Clients (embedding applications, through the `caspaxos` MetaStore provider in `Orleans.Durable.Storage.CasPaxos`) reach
any frontend; each frontend's proposer runs the protocol against every acceptor. Every member configures the same full
membership. The endpoint list a client holds selects frontends only; the hosts' `Peers` define the voting membership.

## Membership and quorums

Run hosts in `fast` mode. Fast CASPaxos commits on the fast path when a fast quorum acknowledges, and
recovers through a classic ballot when a fast quorum is unavailable.

| Members | Fast quorum | Classic recovery quorum | Unavailable members tolerated |
| --- | --- | --- | --- |
| 5 | 4 | 3 | 1 on the fast path; 2 through classic recovery |
| 3 | 3 | 2 | 0 on the fast path; 1 through classic recovery |

Size the cluster for the number of simultaneous member losses it must ride through: five members keep
writes available with two members down. Membership is fixed: adding or removing a member, and replacing a
member's identity after its disk is lost, are [deferred work](../spec/deferred.md).

The data plane sizes its quorums independently: a ledger's replicas acknowledge a write at their own quorum
(`Q` of `N`, 2 of 3 by default) regardless of the metadata quorum state.

## Build

The host, its gRPC register frontend and client, and its administrative UI form Fast CASPaxos' standalone deployment,
which is built and published from [ReubenBond/fast-caspaxos](https://github.com/ReubenBond/fast-caspaxos) at the
commit Orleans' consensus family imports (`4dad617`, `src/Consensus/`). An application connects the `caspaxos`
MetaStore provider to such a cluster by constructing `CasPaxosMetaStoreFactory` over a register service it owns, such as
the upstream gRPC client's. The host's lifecycle guidance is `csharp/src/Orleans.Consensus.CasPaxos.Grpc/README.md` and
`csharp/src/Orleans.Consensus.CasPaxos.Host/README.md` at that commit.

## Host configuration

The host reads standard ASP.NET Core configuration and binds the `FastCASPaxos` section, whose name upstream keeps,
strictly: unknown properties and invalid types, durations, membership, or identities fail startup.

| `FastCASPaxos` setting | Guidance | Upstream default |
| --- | --- | --- |
| `AcceptorId` | This member's identity; appears exactly once in `Peers` | `a` |
| `AcceptorDatabase` | This acceptor's database file, on local durable storage | `data/acceptor-a.db` |
| `ProposerId` | Unique positive integer per proposer for the cluster's lifetime | `1` |
| `ProposerDatabase` | This proposer's allocator file, distinct from the acceptor file | `data/proposer-1.db` |
| `Peers` | Every member: `{ "Id", "Endpoint" (gRPC origin), "HttpEndpoint" (admin origin) }`, identical on all members | one local peer |
| `Mode` | `fast` | `adaptive` |
| `TargetReplicationFactor` | Durable copies a departing member establishes on the other members; `0` selects a majority | `0` |
| `ShutdownMode` | `Departure` drains admitted writes, then replicates local keys to the target on the remaining members; `Restart` drains admitted writes and keeps local state for a restart | `Departure` |
| `OperationTimeout`, `AttemptTimeout`, `PeerCallTimeout`, `RetryBackoff` | Register deadline, phase attempt deadline, peer RPC deadline, retry delay | `10s`, `1s`, `5s`, `10ms` |
| `QueueCapacity`, `MaxBatchSize`, `Capacity` | SQLite admission queue, transaction batch size, register admission bound | `256`, `64`, `256` |
| `RepairInterval`, `RepairRetryInterval`, `RepairPageSize` | Periodic repair scan, shutdown retry delay, digest page size | `1m`, `1s`, `128` |
| `MaxReceiveMessageBytes`, `MaxSendMessageBytes` | gRPC message limits for servers and peer channels | `4194304` each |

Configure listeners under `Kestrel:Endpoints` with `Url`, `Protocols`, and `Certificate`: an `Http2`
endpoint for gRPC and a separate endpoint for the admin API. Use TLS with certificates trusted by every
peer and client for any network deployment; the host validates outgoing peer connections with the
platform's normal HTTPS validation. Both APIs grant callers access to consensus state, so restrict them to
the cluster's trusted network. Set `HostOptions:ShutdownTimeout` (default 30 s) to cover the final repair
scan.

A host document for member `clog-0` of a five-member cluster:

```json
{
  "FastCASPaxos": {
    "AcceptorId": "clog-0",
    "ProposerId": 1,
    "Mode": "fast",
    "Peers": [
      { "Id": "clog-0", "Endpoint": "https://clog-0.clog-members.ns.svc.cluster.local:7446", "HttpEndpoint": "https://clog-0.clog-members.ns.svc.cluster.local:7447" }
    ],
    "TargetReplicationFactor": 3,
    "AcceptorDatabase": "/var/lib/clog/metadata/acceptor.db",
    "ProposerDatabase": "/var/lib/clog/metadata/proposer.db"
  },
  "Kestrel": {
    "Endpoints": {
      "Grpc": { "Url": "https://0.0.0.0:7446", "Protocols": "Http2", "Certificate": { "Path": "/tls/tls.crt", "KeyPath": "/tls/tls.key" } },
      "Admin": { "Url": "https://0.0.0.0:7447", "Protocols": "Http1AndHttp2", "Certificate": { "Path": "/tls/tls.crt", "KeyPath": "/tls/tls.key" } }
    }
  },
  "HostOptions": { "ShutdownTimeout": "00:01:00" }
}
```

`Peers` lists all five members (`clog-0` through `clog-4`), and proposer identities 1 through 5 map one to
one onto the members.

## Client configuration

An embedding application constructs the provider, `CasPaxosMetaStoreFactory` in `Orleans.Durable.Storage.CasPaxos`, from
the frontends' gRPC endpoints, a `TimeProvider`, and the upstream `GrpcRegisterClientOptions`:

```csharp
var options = new GrpcRegisterClientOptions
{
    CallTimeout = TimeSpan.FromSeconds(30),
    AttemptTimeout = TimeSpan.FromSeconds(5),
    MaxReceiveMessageBytes = 4 * 1024 * 1024,
    MaxSendMessageBytes = 4 * 1024 * 1024,
};
using var metaStore = new CasPaxosMetaStoreFactory(
    [
        new Uri("https://member-0.internal:7446"),
        new Uri("https://member-1.internal:7446"),
        new Uri("https://member-2.internal:7446"),
        new Uri("https://member-3.internal:7446"),
        new Uri("https://member-4.internal:7446"),
    ],
    TimeProvider.System,
    options);
```

The client contract is in [MetaStore §8.1](../spec/components/metastore.md#81-fast-caspaxos-semantics). In
short:

- The endpoints are distinct absolute HTTP(S) origins of frontends of one cluster. Use HTTPS with trusted
  certificates for remote members; plain HTTP belongs only on an explicitly trusted development network.
- `CallTimeout` bounds a whole register operation, including failover; `AttemptTimeout` bounds one endpoint
  attempt within it.
- `MaxReceiveMessageBytes` and `MaxSendMessageBytes` (default 4 MiB each) bound serialized gRPC messages
  including envelope overhead; set compatible host limits for the largest expected register value.
- The client keeps affinity to the last successful endpoint, tries each endpoint at most once per call, and
  fails over within `CallTimeout`. It retries a write on another endpoint only after a server-confirmed
  rejection before mutation.

Every register is scoped by the caller's cluster GUID; several Clog clusters can share one Fast CASPaxos
cluster.

## SQLite durability

- Keep each acceptor database, each proposer allocator database, and their WAL files for the lifetime of
  that identity. A member which restarts with its original files rejoins safely in any order.
- Put them on local filesystems with SQLite locking and durable flush. Give the acceptor and the allocator
  distinct paths; never let two processes open the same database.
- Before a member's first start, create the parent of each database's directory on durable storage. For the
  example document's `/var/lib/clog/metadata/acceptor.db`, the database directory is `/var/lib/clog/metadata`,
  and its parent `/var/lib/clog` is a mount point or, on Linux, a directory created and then `sync`ed. A missing
  parent fails startup. On every start the host creates the database directory under that parent when it is
  missing and flushes the parent, then each database flushes its directory, so the directory, database, and WAL
  names are durable before the member answers; repeating the flushes on each start makes durable a name a crashed
  earlier start left unflushed.
- Linux and macOS flush a directory with `fsync`. On Windows, data directories lie on local NTFS volumes, the
  only Windows file system whose directory flush makes entries durable; any other volume fails startup. The
  host's account needs the right to add subdirectories to each parent and the right to add files to each
  database directory, which the directory flush opens its handle with.
- Keep the identity-to-storage mapping fixed. A peer id denotes one physical acceptor: every DNS name or
  proxy which reaches the same database is the same identity.
- An additional direct proposer (an application which owns a proposer) needs its own unique `ProposerId`
  and its own retained allocator, and its state joins the backup inventory.
- A member started on an empty disk under a former identity, or a proposer allocator rolled back to older
  state, can forget promises or ballot allocations other members relied on. Both are unsupported.

## Lifecycle and maintenance

| Operation | Procedure |
| --- | --- |
| First start | Create the parent of each database's directory durably (see [SQLite durability](#sqlite-durability)), then start all members with the same `Peers`; any order. |
| Restart | Restart a member with its original configuration, databases, and WAL files. |
| Repair | Each member runs bounded repair passes periodically. `POST /v1/admin/repair` on any member stages a pass on every member: HTTP 202 means every member accepted, HTTP 503 means only some did. `GET /v1/admin/repair` reports `nodeId`, `bootId`, `draining`, `shutdownComplete`, the replication target, and the latest repair outcome. |
| Graceful stop | Shutdown closes mutation admission, waits for admitted mutations to reach their durable outcomes, and, in `Departure` mode, repairs every local key until the target number of **other** members holds a durable copy. The process exits `0` after a completed barrier and `1` after an interrupted one; a directory flush which fails at startup exits `74` (see [failures](#failures-operators-observe)). |
| Rolling update | Change one member at a time. Before stopping the next member, confirm through the admin API, matched to the departing member's `bootId`, that its drain completed, and confirm that the replacement process serves. |

Set the orchestrator's termination grace period longer than `HostOptions:ShutdownTimeout` plus storage and
HTTP close time. An interrupted drain leaves the member's keys possibly below their replication target:
keep that member's storage and stop the rollout. With a target of three on five members, a drain needs
three other members durable, so it cannot complete while two other members are down. `/health/live` and a
Kubernetes Ready condition report process liveness only; a register operation establishes quorum
availability. The `bootId` correlates observations of one process; it grants no protocol fencing.

## Backup and restore

- **Backup.** Use SQLite's online backup, or copy the complete durable state (acceptor database, allocator
  database, and WAL files) after a clean stop. A consistent cluster backup captures every member's state
  while client admission and maintenance are stopped, records the acknowledged-write boundary, and records
  each identity's acceptor, allocator, and data-volume association.
- **Restore.** Restart an identity only from its latest durable state. Restoring any member, or the whole
  cluster, from an older snapshot requires an offline recovery procedure which preserves every acknowledged
  promise and allocation and fences the original members, writers, and clients; restore stays gated until such a
  procedure is qualified. Fresh proposer storage takes a new, never-used `ProposerId`.
- **What a backup proves.** A captured backup is an artifact; it establishes no voting eligibility for a
  member restored from it.

## Failures operators observe

| Condition | Effect | Action |
| --- | --- | --- |
| One of five members down | Fast path continues | Restore the member with its original state |
| Two of five members down | Writes continue through classic recovery, with extra round trips | Restore members; a departing member's drain cannot complete meanwhile |
| Fewer than a classic quorum reachable | Register reads and writes fail, so every operation which must read or write a register waits: ledger-map transitions, and the drains and drops which depend on them. Writes and hot reads continue, and data already acknowledged stays durable. | Restore a quorum; members rejoin with their original state |
| A write's response is lost, times out, or is cancelled after dispatch | The outcome is unknown: the register may or may not have changed. The caller re-reads the register and tests its goal ([MetaStore §2.2](../spec/components/metastore.md#22-iconditionalregister)). | None; a client-level timeout or cancellation leaves the write possibly applied, so reconcile against the register |
| A frontend is at capacity | The call fails as busy before mutation | Retry later or raise `Capacity` |
| A member restarted on an empty disk under its old identity | Unsupported; may violate protocol safety | Keep the member out of the cluster; member replacement is [deferred work](../spec/deferred.md) |
| A directory flush fails at startup | The host writes the operation, the path, and the operating-system error to standard error and exits with code 74 (`EX_IOERR`) before it answers; afterward it cannot know what the device holds | Let the supervisor restart the member, which recovers from the device's state; replace the device if the failure repeats |
| A database directory's parent is missing, a directory open is denied, or a Windows volume is not local NTFS | Startup fails with that error before any flush, so the next start's flushes still make the names durable | Create the parent durably, grant the directory rights, or move the data to a local NTFS volume |
| A SQLite commit or fsync fails while the member runs | The acceptor writer or proposer allocator fails every later request while the process stays up and `/health/live` reports live ([fast-caspaxos#14](https://github.com/ReubenBond/fast-caspaxos/issues/14)) | Restart the member, which recovers from the device's state |
| Opening a database at startup fails with a SQLite I/O error | The exception ends the host with the runtime's generic crash exit code ([fast-caspaxos#14](https://github.com/ReubenBond/fast-caspaxos/issues/14)) | Let the supervisor restart the member; replace the device if the failure repeats |

## Verification

`just caspaxos` builds and tests the MetaStore adapter, its shared simulation, and the architecture suite, and
checks their xUnit reports under `artifacts/validation/CasPaxos`. The adapter's fault scenarios
(`CasPaxosMetaStoreSimulationTests` in `Orleans.Durable.Storage.CasPaxos.Tests`) run the production adapter against
upstream's `RegisterSimulationCluster` under delay, loss, restart, and quorum failure; `CasPaxosGrpcSqliteTests`
exercises real SQLite hosts over gRPC.
