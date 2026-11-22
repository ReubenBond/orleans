# Linearizability vectors

Published client histories with known verdicts, which pin the linearizability checker in `tests/shared/Linearizability`
([verification §3.4](../../../docs/spec/verification.md#34-linearizability)) to the Porcupine
checker it ports. `Orleans.Durable.Storage.Testing.Tests` (`PorcupineVectorTests`) reads these files directly.

## Files

| File | Contract |
| --- | --- |
| `histories.json` | Each history's file, its verdict under Porcupine's key-value model partitioned by key, and whether the unpartitioned model (one map over every key) is checked as well |
| `porcupine-kv/c01-ok.txt`, `c01-bad.txt` | A key-value store with one client: linearizable, and not |
| `porcupine-kv/c10-ok.txt`, `c10-bad.txt` | Ten clients: linearizable, and not |
| `porcupine-kv/c50-ok.txt`, `c50-bad.txt` | Fifty clients: linearizable, and not |

Each line of a `porcupine-kv` file is one invocation (`:type :invoke`) or response (`:type :ok`) of a `get`, `put`, or
`append` on a string key, in the order they happened. An invocation without a response at the end of the file
responds after every other event with an empty value, as Porcupine's `parseKvLog` does.

## Provenance and licenses

| Files | Source | License |
| --- | --- | --- |
| `porcupine-kv/*.txt` | [Porcupine](https://github.com/anishathalye/porcupine) `test_data/kv/` at commit `97cd067defd99df172053ba7f4ca5bbae57fe446`, unchanged; the verdicts are Porcupine's `TestKv*` tests | MIT, Copyright (c) Anish Athalye: [`PORCUPINE-LICENSE.md`](../../shared/Linearizability/PORCUPINE-LICENSE.md) |

Porcupine's other published histories, `test_data/jepsen/etcd_*.log`, come from
[ahorn/linearizability-checker](https://github.com/ahorn/linearizability-checker), which grants no license, so they
are not copied. Equivalent cases are generated instead: `LinearizabilityPropertyTests` checks etcd-shaped histories
(five clients reading, writing, and comparing-and-setting one register, with timed-out reads and indeterminate
writes) under Porcupine's `etcdModel`, accepting those generated from sequential executions and rejecting those with
an injected stale read.
