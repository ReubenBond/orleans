# Formal Specifications (`specs/`)

All formal Quint protocol models for Roux live in this directory so the repository is self-contained. Every model is checked by GitHub Actions CI (typechecking, directed scenarios, randomized simulation, guard-disabling mutants, and exhaustive TLC model checking).

| Directory | Layer / component | Design doc | CI workflow |
| --- | --- | --- | --- |
| [`messaging/`](messaging/README.md) | L1 `roux-net` connections, handshakes, lanes, backpressure | [`docs/design/connectivity.md`](../docs/design/connectivity.md) | `specs-messaging.yml` |
| [`fast-caspaxos/`](fast-caspaxos/README.md) | L2 `clog-caspaxos` Fast CASPaxos linearizable register, acceptor floor fencing, and Vertical Paxos I voter reconfiguration | [`docs/design/fast-caspaxos.md`](../docs/design/fast-caspaxos.md) | `specs-storage.yml` |
| [`ledger-protocol/`](ledger-protocol/README.md) | L2 `clog-ledger-protocol` quorum replication, sealing, recovery, `AtomicLedger` refinement | [`docs/design/storage.md`](../docs/design/storage.md), [`docs/design/clog/`](../docs/design/clog/README.md) | `specs-storage.yml` |
| [`ledgers/`](ledgers/README.md) | L2 `clog-ledgers` keyspace map, splits/merges/takeovers, segments, drops & tombstones | [`docs/design/storage.md`](../docs/design/storage.md), [`docs/design/clog/`](../docs/design/clog/README.md) | `specs-storage.yml` |
| [`archive/`](archive/README.md) | L2 `clog-archive` single-writer SlateDB drain, atomic watermarks, `DbReader` refresh (`ST2`, `ST3`) | [`docs/design/storage.md`](../docs/design/storage.md) | `specs-storage.yml` |
| [`message-drain/`](message-drain/README.md) | L2 `clog-ledgers` + `clog-archive` + `clog-journals` general-purpose unversioned `RowKind::Message = 3`, single-pass `DrainScan` message folding, deterministic idempotent durable sink commits (`ST12`, `LG-I15`, `LG-I16`, `JS-I11`, `JS-I12`) | [`docs/design/storage.md`](../docs/design/storage.md), [`docs/design/clog/`](../docs/design/clog/README.md) | `specs-storage.yml` |
| [`rapid/`](rapid/README.md) | L3 `roux-membership` Rapid `CutDetector` and Fast/Classic Paxos view-change agreement | [`docs/design/rapid.md`](../docs/design/rapid.md) | `specs-membership.yml` |
| [`membership/`](membership/README.md) | L3 `roux-membership` durable `StoredView` log, no resurrection, self-fencing | [`docs/design/membership.md`](../docs/design/membership.md) | `specs-membership.yml` |
| [`range-handoff/`](range-handoff/README.md) | L2+L4 ledger-replica quorum leases, `boot_lease_horizon`, `PrepareRelease` cooperative drain, seal-gated takeover (`ST8`, `R33`) | [`docs/design/storage.md`](../docs/design/storage.md), [`docs/design/runtime.md`](../docs/design/runtime.md) | `specs-grain-runtime.yml` |
| [`grain-runtime/`](grain-runtime/README.md) | L4 `roux-runtime` turns, staging, nil-externality, inbox/outbox, `Replace`/`ReplaceRequired`, `Rejected` rebase, `Fenced` resend (`R1`–`R3`, `R11`, `R14`, `R30`, `R31`) | [`docs/design/runtime.md`](../docs/design/runtime.md) | `specs-grain-runtime.yml` |
| [`jobs/`](jobs/README.md) | L4 `roux-jobs` `JobShard` + `JobCursor` window closure (`R32`) and grain-local `DurableJobQueue` + `GrainReminderCoordinator` (`"roux.reminder"`, `R37`) | [`docs/design/runtime.md`](../docs/design/runtime.md) | `specs-grain-runtime.yml` |
