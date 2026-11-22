// -*- mode: Bluespec; -*-
/**
 * The ledger storage contract's abstract model (ledger-protocol.md §2.7,
 * LL-I1 to LL-I9).
 *
 * Models one ledger as specs/ledgers/LedgerMap.qnt models it, where appends
 * land atomically and sealing and recovery are left to the storage below,
 * together with the seal readers observe, readers' tail checks, and fetchers'
 * reads and drains below a learned contiguous tail. LedgerProtocol.qnt checks
 * step-by-step refinement against AtomicState via isAtomicStep, discharging
 * LedgerMap's atomic close and carrying the contract's properties
 * (OpenCheckSound, CheckTailBelowEnd, BelowTailDecided) to the quorum protocol.
 *
 * Actions:
 * - issue: acquires the next slot (at any time, including after close).
 * - appendSlot: while open, acknowledges the next issued slot in slot order.
 * - seal: begins a recovery's seal, after which a check may report sealed (LL-I4).
 * - fence: decides a recovery's candidate end, after which any check that begins
 *   must report sealed (LL-I5).
 * - close: once fenced, records T in [acked, issued] and closes the ledger once.
 * - acknowledge: after close, acknowledges an issued slot below T (a deposed
 *   writer learning a slot's durable outcome).
 * - drop: drops a closed ledger.
 * - beginCheck / answerCheck: a reader's tail check carrying a tail <= bound
 *   and reporting a tail in [carry, bound] (LL-I7), sealed only after seal
 *   began, and sealed if begun once fenced (LL-I5).
 * - learnTail / read / drain: a fetcher learns a contiguous tail <= bound and
 *   reads or drains only below it (LL-I6).
 *
 * Mutants:
 * - atomic-late-open: lets a check begun after fence report open (breaks OpenCheckSound).
 * - atomic-check-issued: lets a check report a tail up to issued (breaks CheckTailBelowEnd).
 * - atomic-learn-issued: lets a fetcher learn a tail up to issued (breaks BelowTailDecided).
 */
/**
 * Pure contract types and single-step transition relation shared by
 * AtomicLedger (standalone model) and refining models such as LedgerProtocol.
 */
module AtomicLedgerContract {
  // Status values: "open", "closed", "dropped"
  // Seal values:   "none", "sealing", "sealed"
  // Check phases:  "idle", "reading", "done"

  type AtomicCheckState = {
    phase: str,
    carry: int,
    late: bool,
    sealed: bool,
    tail: int,
  }

  type AtomicState = {
    status: str,
    issued: int,
    acked: int,
    // Recorded end T once closed; -1 while open.
    end: int,
    seal: str,
    checks: int -> AtomicCheckState,
    known: int -> int,
    // Set of (position, wasOpen) pairs served to fetchers.
    served: Set[(int, bool)],
    drained: int,
  }

  pure val noAtomicCheck: AtomicCheckState = {
    phase: "idle",
    carry: 0,
    late: false,
    sealed: false,
    tail: 0,
  }

  pure def atomicEnded(a: AtomicState): bool = a.status != "open"

  // Every position below atomicBound is decided: acked while open, end once closed.
  pure def atomicBound(a: AtomicState): int =
    if (a.status == "open") a.acked else a.end

  pure def atomicDecided(a: AtomicState, p: int): bool =
    if (atomicEnded(a)) p < a.end else p < a.acked

  pure def atomicInitialState(readers: Set[int], fetchers: Set[int]): AtomicState = {
    status: "open",
    issued: 0,
    acked: 0,
    end: -1,
    seal: "none",
    checks: readers.mapBy(_ => noAtomicCheck),
    known: fetchers.mapBy(_ => 0),
    served: Set(),
    drained: 0,
  }

  // Pure single-step transition relation used by refining models (RefinesAtomicLedger).
  pure def isAtomicStep(
    pre: AtomicState,
    post: AtomicState,
    slots: int,
    readers: Set[int],
    fetchers: Set[int],
    mutation: str
  ): bool = or {
    // Stutter
    post == pre,
    // Issue
    pre.issued < slots and post == { ...pre, issued: pre.issued + 1 },
    // Append
    pre.status == "open" and pre.acked < pre.issued and post == { ...pre, acked: pre.acked + 1 },
    // Seal
    pre.status == "open" and pre.seal == "none" and post == { ...pre, seal: "sealing" },
    // Fence
    pre.status == "open" and pre.seal == "sealing" and post == { ...pre, seal: "sealed" },
    // Close
    pre.status == "open" and pre.seal == "sealed" and
      pre.acked <= post.end and post.end <= pre.issued and
      post == { ...pre, status: "closed", end: post.end },
    // Acknowledge (after close, below T)
    pre.status != "open" and pre.acked < pre.end and post == { ...pre, acked: pre.acked + 1 },
    // Drop
    pre.status == "closed" and post == { ...pre, status: "dropped" },
    // BeginCheck
    readers.exists(c =>
      val nw = post.checks.get(c)
      pre.checks.get(c).phase == "idle" and
      nw.carry >= 0 and nw.carry <= atomicBound(pre) and
      post == {
        ...pre,
        checks: pre.checks.set(c, {
          phase: "reading",
          carry: nw.carry,
          late: pre.seal == "sealed",
          sealed: false,
          tail: nw.carry,
        }),
      }
    ),
    // AnswerCheck
    readers.exists(c =>
      val old = pre.checks.get(c)
      val nw = post.checks.get(c)
      val upper = if (mutation == "atomic-check-issued") pre.issued else atomicBound(pre)
      old.phase == "reading" and
      (nw.sealed implies pre.seal != "none") and
      (old.late implies (nw.sealed or mutation == "atomic-late-open")) and
      nw.tail >= old.carry and nw.tail <= upper and
      post == {
        ...pre,
        checks: pre.checks.set(c, { ...old, phase: "done", sealed: nw.sealed, tail: nw.tail }),
      }
    ),
    // LearnTail
    fetchers.exists(f =>
      val t = post.known.get(f)
      val upper = if (mutation == "atomic-learn-issued") pre.issued else atomicBound(pre)
      pre.known.get(f) < t and t <= upper and
      post == { ...pre, known: pre.known.set(f, t) }
    ),
    // Read
    fetchers.exists(f =>
      val added = post.served.exclude(pre.served)
      added.size() == 1 and
      added.forall(entry =>
        entry._1 >= 0 and entry._1 < slots and
        entry._1 < pre.known.get(f) and
        entry._2 == (pre.status == "open")
      ) and
      post == { ...pre, served: pre.served.union(added) }
    ),
    // Drain
    fetchers.exists(f =>
      pre.drained < pre.known.get(f) and
      post == { ...pre, drained: pre.known.get(f) }
    ),
  }
}

module AtomicLedger {
  import AtomicLedgerContract.*

  // Slots the writer may acquire (0 .. Slots - 1).
  const Slots: int
  // Reader IDs issuing tail checks.
  const Readers: Set[int]
  // Fetcher IDs reading and draining below a learned contiguous tail.
  const Fetchers: Set[int]
  // "none", or one of "atomic-late-open", "atomic-check-issued", "atomic-learn-issued".
  const Mutation: str

  var s: AtomicState

  val positions: Set[int] = if (Slots > 0) 0.to(Slots - 1) else Set()

  action init = s' = atomicInitialState(Readers, Fetchers)

  action issue: bool = all {
    s.issued < Slots,
    s' = { ...s, issued: s.issued + 1 },
  }

  action appendSlot: bool = all {
    s.status == "open",
    s.acked < s.issued,
    s' = { ...s, acked: s.acked + 1 },
  }

  action seal: bool = all {
    s.status == "open",
    s.seal == "none",
    s' = { ...s, seal: "sealing" },
  }

  action fence: bool = all {
    s.status == "open",
    s.seal == "sealing",
    s' = { ...s, seal: "sealed" },
  }

  action close: bool = all {
    s.status == "open",
    s.seal == "sealed",
    nondet t = s.acked.to(s.issued).oneOf()
    s' = { ...s, status: "closed", end: t },
  }

  action acknowledge: bool = all {
    s.status != "open",
    s.acked < s.end,
    s' = { ...s, acked: s.acked + 1 },
  }

  action drop: bool = all {
    s.status == "closed",
    s' = { ...s, status: "dropped" },
  }

  action beginCheck(c: int, t: int): bool = all {
    s.checks.get(c).phase == "idle",
    t >= 0 and t <= atomicBound(s),
    s' = {
      ...s,
      checks: s.checks.set(c, {
        phase: "reading",
        carry: t,
        late: s.seal == "sealed",
        sealed: false,
        tail: t,
      }),
    },
  }

  action answerCheck(c: int, isSealed: bool, t: int): bool = {
    val chk = s.checks.get(c)
    val upper = if (Mutation == "atomic-check-issued") s.issued else atomicBound(s)
    all {
      chk.phase == "reading",
      isSealed implies s.seal != "none",
      chk.late implies (isSealed or Mutation == "atomic-late-open"),
      t >= chk.carry and t <= upper,
      s' = {
        ...s,
        checks: s.checks.set(c, { ...chk, phase: "done", sealed: isSealed, tail: t }),
      },
    }
  }

  action learnTail(f: int, t: int): bool = {
    val upper = if (Mutation == "atomic-learn-issued") s.issued else atomicBound(s)
    all {
      s.known.get(f) < t,
      t <= upper,
      s' = { ...s, known: s.known.set(f, t) },
    }
  }

  action read(f: int, p: int): bool = {
    val entry = (p, s.status == "open")
    all {
      p < s.known.get(f),
      not(s.served.contains(entry)),
      s' = { ...s, served: s.served.union(Set(entry)) },
    }
  }

  action drain(f: int): bool = all {
    s.drained < s.known.get(f),
    s' = { ...s, drained: s.known.get(f) },
  }

  action step = any {
    issue,
    appendSlot,
    seal,
    fence,
    close,
    acknowledge,
    drop,
    all {
      Readers != Set(),
      nondet c = Readers.oneOf()
      nondet t = 0.to(Slots).oneOf()
      any {
        beginCheck(c, t),
        nondet isSealed = Set(true, false).oneOf()
        answerCheck(c, isSealed, t),
      },
    },
    all {
      Fetchers != Set(),
      nondet f = Fetchers.oneOf()
      any {
        nondet t = 0.to(Slots).oneOf()
        learnTail(f, t),
        all {
          positions != Set(),
          nondet p = positions.oneOf()
          read(f, p),
        },
        drain(f),
      },
    },
  }

  // ---------------------------------------------------------------------------
  // Contract properties (inherited by every refining model)
  // ---------------------------------------------------------------------------

  val TypeOK: bool = and {
    s.status == "open" or s.status == "closed" or s.status == "dropped",
    s.seal == "none" or s.seal == "sealing" or s.seal == "sealed",
    s.issued >= 0 and s.issued <= Slots,
    s.acked >= 0 and s.acked <= s.issued,
    s.end >= -1 and s.end <= Slots,
    Readers.forall(c =>
      val chk = s.checks.get(c)
      (chk.phase == "idle" or chk.phase == "reading" or chk.phase == "done") and
      chk.tail >= 0 and chk.tail <= Slots
    ),
    Fetchers.forall(f => s.known.get(f) >= 0 and s.known.get(f) <= Slots),
    s.served.forall(entry => entry._1 >= 0 and entry._1 < Slots),
    s.drained >= 0 and s.drained <= Slots,
  }

  // A check begun once a recovery decided its candidate end, or after the close,
  // reports the ledger sealed (LL-I5, LP-I9).
  val OpenCheckSound: bool =
    Readers.forall(c =>
      val chk = s.checks.get(c)
      (chk.phase == "done" and chk.late) implies chk.sealed
    )

  // No check reports a tail past T, whenever it began: meets the Delos erratum's
  // tailpos1 <= tailpos2 (LL-I5, LL-I7).
  val CheckTailBelowEnd: bool =
    atomicEnded(s) implies Readers.forall(c =>
      val chk = s.checks.get(c)
      (chk.phase == "done") implies (chk.tail <= s.end)
    )

  // Every position a fetcher read or drained below a learned tail is decided:
  // acknowledged while open, and below T once closed (LL-I6).
  val BelowTailDecided: bool =
    s.served.map(entry => entry._1)
      .union(positions.filter(q => q < s.drained))
      .forall(p => atomicDecided(s, p))
}
