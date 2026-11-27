# Messaging connection specification

[`connection.qnt`](connection.qnt) is a [Quint](https://quint-lang.org) specification of the connection handshake, connection lifecycle, in-process forwarding, and responses, as designed in [Messaging](../../docs/design/messaging.md) ([N2](../../docs/design/messaging.md#n2-handshake), [N12](../../docs/design/messaging.md#n12-connection-topology), [N13](../../docs/design/messaging.md#n13-owner-threads), [N18](../../docs/design/messaging.md#n18-connection-lifecycle), and [S1](../../docs/design/messaging.md#s1-identity-and-authentication)). Messaging is membership-free ([Connectivity](../../docs/design/connectivity.md)), so the model has no membership.

## Model

- **Instances.** The dialing process and the accepting process each restart as new instances. An old dialer instance may keep running. An acceptor restart resets its connections and empties its queues.
- **Identity.** Each instance has its own TLS key. A connection authenticates the dialing instance's key, claims an instance, and names the acceptor instance it expects, or none when dialing a configured address.
- **Topology.** One connection per dialer instance at a time, read by one acceptor thread, which forwards each request to its destination thread's FIFO queue, or answers it `NotStarted` without forwarding it, as it does for an incompatible target or a refusal by the interface's admission check ([N29](../../docs/design/messaging.md#n29-admission)). The destination thread runs the request and answers it on the request's connection.
- **Dialer side.** The owner thread keeps a queue of unwritten frames and a pending-call table of written requests, and routes each response to the thread that sent the request. When the dialer's side fails, unwritten frames fail with `NotSent` and pending calls with an unknown outcome, while the acceptor may still read what it already received.
- **Actions:** dialer and acceptor restarts, dialing with any claimed instance and target, the handshake (accept or reject at once, closing the older connection from the same instance), sends, writes, reads and forwarding, `NotStarted` answers, dispatch and reply, reading replies, dialer-side failures (including `close(instance)`), and acceptor-side closes. Process crashes and handshake timeouts only disable later actions, so for safety they are covered by idle processes and failed streams.
- **Oracles.** `delivered`, `responses`, and `notSent` are ghost logs of every dispatch, every completed call, and every `NotSent` failure; every invariant is stated over them.

## Properties

| Invariant | Meaning |
| --- | --- |
| `authenticated` | Every dispatched request comes from the instance its connection claims. |
| `rightTarget` | A request whose sender named an acceptor instance reaches only that instance. |
| `atMostOnce` | Each request is dispatched at most once. |
| `fifoWithGaps` | From one sender instance to one thread of one acceptor instance, dispatches follow send order; requests may be missing, never reordered. |
| `noEffectOnNotSent` | A request that failed with `NotSent` was never dispatched. |
| `responsesSound` | Each response completes the call of the thread that sent its request, at most once. |
| `outcomesSound` | A response saying the turn ran follows its request's dispatch, and a `NotStarted` response's request is never dispatched. |

`safety` is their conjunction.

## Mutants

Each mutant reintroduces one bug through a flag of `stepWith`, and must violate the invariant named above it.

| Mutant | Broken rule | Violates | Checked by |
| --- | --- | --- | --- |
| `mutantSkipKeyCheck` | Trust the claimed instance without comparing the TLS key | `authenticated` | simulation, `impersonationTest` |
| `mutantSkipTargetCheck` | Accept a `Hello` naming another acceptor instance | `rightTarget` | simulation |
| `mutantNotSentAfterWrite` | Report written but unanswered requests as `NotSent` when the connection fails | `noEffectOnNotSent` | simulation, `notSentAfterWriteTest` |
| `mutantNotStartedAfterDispatch` | Answer `NotStarted` for a request whose turn already ran | `outcomesSound` | simulation, `notStartedAfterDispatchTest` |
| `tlcMutantResendOnReconnect` | Resend on a new connection what older connections carried | `atMostOnce` | TLC, `resendDuplicatesTest` |
| `tlcMutantKeepOlder` | Keep the older inbound connection from an instance open | `fifoWithGaps` | TLC, `keepOlderReordersTest` |
| `tlcMutantReadAfterClose` | Keep reading a connection after closing it | `fifoWithGaps` | TLC, `readAfterCloseReordersTest` |

The shared gate, [`check-mutants.sh`](../check-mutants.sh), runs every `action mutant*` by random simulation. The `tlcMutant*` mutants need a reconnect sequence that random walks rarely complete, so [`check-tlc-mutants.sh`](check-tlc-mutants.sh) runs them with TLC under the same rules: a mutant counts only when the run reports a violation of its named invariant.

## Running

```sh
quint test connection.qnt --main=connectionSim --match=Test
quint run connection.qnt --main=connectionSim --backend=rust --invariant=safety --max-steps=40 --max-samples=20000
../check-mutants.sh connection.qnt connectionMutants safety --backend=rust --max-steps=40 --max-samples=100000 --seed=7
./check-tlc-mutants.sh connection.qnt connectionMutants safety
quint verify connection.qnt --main=connectionTlc --backend=tlc --invariant=safety
```

All checks run automatically in [`.github/workflows/specs-messaging.yml`](../../.github/workflows/specs-messaging.yml).

## Results

- Directed scenarios: all 12 pass.
- Random simulation: no violation in 20,000 traces of up to 40 steps (`connectionSim`: 5 instances, 4 connections, 4 requests, 2 threads).
- Mutants: the four simulation mutants violate their invariants within 100,000 traces; TLC finds each `tlcMutant*` violation on `connectionMutants`. One local run of the TLC gate stopped with a TLC error rather than a verdict, which the gate counts as a failure; the rerun caught all three.
- TLC on `connectionTlc` (3 instances, 2 connections, 2 requests, 2 threads): complete; no violation in 4,647,791 distinct states, depth 20, in about a minute.
- Together, the gates take about 40 minutes locally.

## Not yet modeled

- Liveness: two live processes on a working network eventually connect, and every call completes or fails by its deadline.
- Clients, the `CODE_WRONG_THREAD` redirect, and more than one dialing node.
