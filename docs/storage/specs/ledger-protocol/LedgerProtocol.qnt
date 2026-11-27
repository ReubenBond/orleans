// -*- mode: Bluespec; -*-
/**
 * BookKeeper's quorum ledger protocol for one Clog ledger (paired with
 * LedgerProtocol.tla; docs/spec/components/ledger-protocol.md §6–§8).
 *
 * Adapted from Jack Vanlightly's BookKeeperProtocol.tla
 * (https://github.com/Vanlightly/bookkeeper-tlaplus, commit
 * fe199dc717b5723f5308aca74eda6ab3b03917e5), MIT License; see LICENSE and
 * README.md. Positions start at 0, as Clog's slots do.
 *
 * Scope:
 * - Single writer (the ledger's primary) that never stops, with window K
 *   (LP-I2), deferred sends, repeats within a duplicate budget (LP-I12),
 *   in-order completions (LP-I3, LP-I4), and optional explicit contiguous-tail
 *   writes (TailWrites, ledger-protocol.md §8.2).
 * - N replicas with quorum Q and no striping: lazy creation on first write,
 *   recovery write, or seal unless the node holds the ledger's tombstone
 *   (LR-I7), durable seals (LR-I8), retained contiguous tails (LR-I6),
 *   restarts (WAL replay rebuilding retained tail as max(0, h + 1 - K)), and
 *   durable-answer checks across restarts (NoRevokedAnswer, conventions §13).
 * - Concurrent recovery executors: sealing to quorum coverage N - Q + 1
 *   (LP-I6), pipelined forward reads up to K ahead and within [c, c + 2K]
 *   (RecoveryBounded), re-replication of every found position to Q replicas
 *   (LP-I7), and conditional close in the ledger map (RecoveriesConverge).
 * - Drops: removal from the ledger map and per-node reconciliation recording a
 *   tombstone atomically with dropping the replica (LS-I2, LR-I7), plus stray
 *   replicas created by delayed requests on nodes without tombstones.
 * - Readers' tail checks (LP-I9, Delos erratum: CheckTailBelowEnd,
 *   ReadersSeeClose, OpenCheckSound) and fetchers' single-replica reads,
 *   read repair, and drains below a learned contiguous tail (LP-I10, LG-I8,
 *   LSM-I2, BelowTailDecided).
 * - Faults within a Q - 1 replica budget: node disk loss (LS-I6, OR-I14),
 *   slot integrity corruption (LR-I2, LR-I1), read failures, plus assumption
 *   witnesses for amnesia and tombstone loss.
 * - Optional SeparateAnswers mode where replica answers travel back to clients
 *   asynchronously (or drop) and clients retry unanswered requests.
 * - Step-by-step refinement of AtomicLedger.qnt checked as a state invariant
 *   (RefinesAtomicLedger). Liveness (RecoveriesEnd under weak fairness) is
 *   checked in the paired TLA+ specification (LedgerProtocolLiveness.cfg).
 */
module LedgerProtocol {
  import QuorumCoverage.* from "./QuorumCoverage"
  import AtomicLedgerContract.* from "./AtomicLedger"

  // ---------------------------------------------------------------------------
  // Parameters
  // ---------------------------------------------------------------------------

  const Replicas: Set[int]        // N replica IDs
  const Quorum: int               // Q: write quorum (1 .. N)
  const Slots: int                // writer appends positions 0 .. Slots - 1
  const Window: int               // K: max acquired-but-uncompleted slots (LP-I2)
  const Recoverers: Set[int]      // concurrent recovery executor IDs
  const Readers: Set[int]         // tail-check reader IDs
  const Fetchers: Set[int]        // single-replica reader / drainer IDs
  const TailWrites: bool          // primary sends explicit contiguous-tail writes
  const Faults: Set[str]          // subset of {"lose-node", "corrupt", "amnesia", "tombstone-loss", "read-failure"}
  const Duplicates: int           // budget for repeated deliveries and client resends
  const SeparateAnswers: bool     // replica answers travel asynchronously and may drop
  const Mutation: str             // "none", or the guard-disabling mutant name

  // ---------------------------------------------------------------------------
  // Types (string tags keep compiled Quint IR compact for Apalache/TLC)
  // ---------------------------------------------------------------------------

  // Map status:     "open" | "closed" | "dropped"
  // Recovery phase: "idle" | "sealing" | "reading" | "closing" | "closed" | "superseded"
  // Stray status:   "none" | "held" | "dropped"
  // Write result:   "written" | "sealed" | "dropped" | "corrupt"
  // Seal result:    "ok" | "dropped"
  // Read result:    "batch" | "missing" | "corrupt" | "dropped"
  // Copy result:    "written" | "dropped"
  // Answer kind:    "write" | "seal" | "read" | "copy" | "check"

  type WriteMsg = { replica: int, pos: int, carriedTail: int }
  type SealMsg = { rec: int, replica: int }
  type ReadMsg = { rec: int, replica: int, pos: int, marked: bool }
  type CopyMsg = { fromRecoverer: bool, senderId: int, replica: int, pos: int, carriedTail: int }
  type TailMsg = { replica: int, carriedTail: int }

  // Unified asynchronous reply envelope (used only when SeparateAnswers = true):
  // - "write": replica, pos, result, flag = fresh
  // - "seal":  client = rec, replica, result, tail = reportedTail
  // - "read":  client = rec, replica, pos, result, flag = marked
  // - "copy":  client = senderId, flag = fromRecoverer, replica, pos, result, tail = (1 if repaired else 0)
  // - "check": client = reader, replica, flag = isSealed, tail = reportedTail
  type Answer = {
    kind: str,
    client: int,
    replica: int,
    pos: int,
    result: str,
    flag: bool,
    tail: int,
  }

  type ReplicaState = {
    exists: bool,
    stored: Set[int],
    sealed: bool,
    retained: int,
    tomb: bool,
    createDecided: bool,
    stray: str,
    corrupt: Set[int],
    // Volatile (not-yet-durable) state populated only under volatile-answer mutants.
    volData: Set[int],
    volSeal: Set[int],
    volCreate: bool,
    // Answers emitted by this replica since creation (checked across restarts).
    ansData: Set[int],
    ansSealed: bool,
  }

  type WriterState = {
    nextPos: int,
    completed: int,
    unsent: Set[int],
    written: int -> Set[int],
    sawSealed: bool,
    ackAfterSealed: bool,
    repeats: int -> int,
    repeated: bool,
    carried: int,
    sent: int -> int,
    freshRepeat: bool,
  }

  type RecovererState = {
    phase: str,
    version: int,
    sealedBy: Set[int],
    known: int,
    start: int,
    pos: int,
    issued: int,
    absent: int -> Set[int],
    found: Set[int],
    copied: int -> Set[int],
    end: int,
    learned: bool,
    repaired: bool,
  }

  type MapState = {
    status: str,
    end: int,
    version: int,
  }

  type CloseState = {
    closes: Set[int],
    ackedAtClose: int,
  }

  type CheckState = {
    phase: str,
    carry: int,
    asked: Set[int],
    answered: Set[int],
    sealedAnswers: Set[int],
    reportedTail: int,
    sealed: bool,
    late: bool,
    open: bool,
  }

  type FetchState = {
    known: int -> int,
    served: Set[(int, bool)],
    drained: int,
    repairs: bool,
  }

  type NetworkState = {
    writes: Set[WriteMsg],
    tails: Set[TailMsg],
    seals: Set[SealMsg],
    reads: Set[ReadMsg],
    copies: Set[CopyMsg],
  }

  type FaultState = {
    faulty: Set[int],
    lost: Set[int],
    amnesiac: Set[int],
    dups: int,
  }

  type RecoveryEffect = {
    rec: int -> RecovererState,
    reads: Set[ReadMsg],
    copies: Set[CopyMsg],
  }

  // ---------------------------------------------------------------------------
  // State variables
  // ---------------------------------------------------------------------------

  var replicas: int -> ReplicaState
  var writer: WriterState
  var rec: int -> RecovererState
  var chk: int -> CheckState
  var ledgerMap: MapState
  var closeLog: CloseState
  var fetch: FetchState
  var net: NetworkState
  var answers: Set[Answer]
  var faults: FaultState
  var refinesAtomic: bool

  // ---------------------------------------------------------------------------
  // Pure helpers and constants
  // ---------------------------------------------------------------------------

  pure def maxInt(a: int, b: int): int = if (a >= b) a else b
  pure def minInt(a: int, b: int): int = if (a <= b) a else b
  pure def rangeSet(lo: int, hi: int): Set[int] = if (lo <= hi) lo.to(hi) else Set()
  pure def maxOf(xs: Set[int]): int = xs.fold(-1, (best, x) => if (x > best) x else best)

  val replicaCount: int = Replicas.size()
  val positions: Set[int] = rangeSet(0, Slots - 1)
  val readSlots: Set[int] = rangeSet(0, Slots)

  pure val initialReplica: ReplicaState = {
    exists: false,
    stored: Set(),
    sealed: false,
    retained: 0,
    tomb: false,
    createDecided: false,
    stray: "none",
    corrupt: Set(),
    volData: Set(),
    volSeal: Set(),
    volCreate: false,
    ansData: Set(),
    ansSealed: false,
  }

  pure val initialCheck: CheckState = {
    phase: "idle",
    carry: 0,
    asked: Set(),
    answered: Set(),
    sealedAnswers: Set(),
    reportedTail: 0,
    sealed: false,
    late: false,
    open: false,
  }

  pure def hasVolatile(rep: ReplicaState): bool =
    rep.volData != Set() or rep.volSeal != Set() or rep.volCreate

  pure def isActive(phase: str): bool =
    phase == "sealing" or phase == "reading" or phase == "closing"

  pure def isFinished(phase: str): bool =
    phase == "closing" or phase == "closed" or phase == "superseded"

  // Quorum coverage (QuorumCoverage.qnt): N - Q + 1 replicas.
  def covered(rs: Set[int]): bool =
    hasQuorumCoverageAffirmative(rs, replicaCount, Quorum)

  def sealCovered(rs: Set[int]): bool =
    if (Mutation == "seal-short") rs.size() >= replicaCount - Quorum else covered(rs)

  def absenceCovered(rs: Set[int]): bool =
    if (Mutation == "short-negatives") rs.size() >= replicaCount - Quorum else covered(rs)

  val checkSize: int = if (Mutation == "open-check-short") Quorum - 1 else Quorum

  def reportsSealed(sealedAnswers: Set[int]): bool =
    if (Mutation == "sealed-quorum") sealedAnswers.size() >= Quorum else sealedAnswers != Set()

  val writesAnswerAtFlush: bool = Mutation == "read-volatile"
  val sealsAnswerAtFlush: bool =
    Mutation == "repeat-seal-volatile" or
    Mutation == "write-sealed-volatile" or
    Mutation == "check-sealed-volatile"

  def keepsOptions(dups: int): Set[bool] =
    if (dups < Duplicates) Set(true, false) else Set(false)

  val deliversOptions: Set[bool] =
    if (SeparateAnswers) Set(true, false) else Set(true)

  pure def answersDropped(rep: ReplicaState): bool =
    not(rep.exists) and rep.tomb and not(rep.createDecided)

  def applyCreate(
    repMap: int -> ReplicaState,
    r: int,
    rep: ReplicaState,
    creating: bool
  ): ReplicaState =
    if (not(creating)) rep
    else {
      ...rep,
      tomb: false,
      createDecided: if (rep.tomb) false else rep.createDecided,
      stray:
        if (not(rep.tomb) and Replicas.exclude(Set(r)).exists(q => repMap.get(q).tomb)) "held"
        else rep.stray,
    }

  pure def mapClosed(m: MapState): bool = m.status == "closed"
  pure def mapEnded(m: MapState): bool = m.status != "open"

  // ---------------------------------------------------------------------------
  // Projection to AtomicLedger (RefinesAtomicLedger)
  // ---------------------------------------------------------------------------

  pure def atomicCheckOf(c: CheckState): AtomicCheckState = {
    val done = c.phase == "done"
    {
      phase: c.phase,
      carry: c.carry,
      late: c.late,
      sealed: done and c.sealed,
      tail: if (done) c.reportedTail else c.carry,
    }
  }

  def sealOf(mStatus: str, rs: int -> RecovererState): str =
    if (mStatus != "open" or Recoverers.exists(i => isFinished(rs.get(i).phase))) "sealed"
    else if (Recoverers.exists(i => rs.get(i).phase != "idle")) "sealing"
    else "none"

  val currentAtomic: AtomicState = {
    status: ledgerMap.status,
    issued: writer.nextPos,
    acked: writer.completed,
    end: ledgerMap.end,
    seal: sealOf(ledgerMap.status, rec),
    checks: Readers.mapBy(c => atomicCheckOf(chk.get(c))),
    known: fetch.known,
    served: fetch.served,
    drained: fetch.drained,
  }

  def checkAtomic(post: AtomicState): bool =
    refinesAtomic and isAtomicStep(currentAtomic, post, Slots, Readers, Fetchers, "none")

  // ---------------------------------------------------------------------------
  // Initial state
  // ---------------------------------------------------------------------------

  action init = {
    val initRec: RecovererState = {
      phase: "idle",
      version: 0,
      sealedBy: Set(),
      known: 0,
      start: 0,
      pos: 0,
      issued: 0,
      absent: readSlots.mapBy(_ => Set()),
      found: Set(),
      copied: positions.mapBy(_ => Set()),
      end: -1,
      learned: false,
      repaired: false,
    }
    all {
      replicas' = Replicas.mapBy(_ => initialReplica),
      writer' = {
        nextPos: 0,
        completed: 0,
        unsent: Set(),
        written: positions.mapBy(_ => Set()),
        sawSealed: false,
        ackAfterSealed: false,
        repeats: positions.mapBy(_ => 0),
        repeated: false,
        carried: 0,
        sent: positions.mapBy(_ => 0),
        freshRepeat: false,
      },
      rec' = Recoverers.mapBy(_ => initRec),
      chk' = Readers.mapBy(_ => initialCheck),
      ledgerMap' = { status: "open", end: -1, version: 0 },
      closeLog' = { closes: Set(), ackedAtClose: 0 },
      fetch' = { known: Fetchers.mapBy(_ => 0), served: Set(), drained: 0, repairs: false },
      net' = { writes: Set(), tails: Set(), seals: Set(), reads: Set(), copies: Set() },
      answers' = Set(),
      faults' = { faulty: Set(), lost: Set(), amnesiac: Set(), dups: 0 },
      refinesAtomic' = true,
    }
  }

  // ---------------------------------------------------------------------------
  // Writer actions (LP-I1 to LP-I4, LP-I11, LP-I12)
  // ---------------------------------------------------------------------------

  def quorumTail(w: WriterState): int = {
    val acked = positions.filter(p => w.written.get(p).size() >= Quorum)
    if (acked == Set()) 0 else maxOf(acked) + 1
  }

  def knownTail(w: WriterState): int =
    if (Mutation == "eager-tail") maxInt(w.completed, quorumTail(w)) else w.completed

  def updatedCarried(w: WriterState, t: int): int =
    if (TailWrites) maxInt(w.carried, t) else w.carried

  action acquire(defer: bool): bool = {
    val kt = knownTail(writer)
    val newWrites =
      if (defer) net.writes
      else net.writes.union(Replicas.map(r => { replica: r, pos: writer.nextPos, carriedTail: kt }))
    val newWriter =
      if (defer) { ...writer, nextPos: writer.nextPos + 1, unsent: writer.unsent.union(Set(writer.nextPos)) }
      else {
        ...writer,
        nextPos: writer.nextPos + 1,
        carried: updatedCarried(writer, kt),
        sent: writer.sent.set(writer.nextPos, if (Duplicates > 0) kt else 0),
      }
    all {
      writer.nextPos < Slots,
      writer.nextPos - writer.completed < Window,
      defer implies (writer.nextPos + 1 - writer.completed < Window),
      replicas' = replicas,
      writer' = newWriter,
      rec' = rec,
      chk' = chk,
      ledgerMap' = ledgerMap,
      closeLog' = closeLog,
      fetch' = fetch,
      net' = { ...net, writes: newWrites },
      answers' = answers,
      faults' = faults,
      refinesAtomic' = checkAtomic({ ...currentAtomic, issued: newWriter.nextPos }),
    }
  }

  action sendDeferred(p: int): bool = {
    val kt = knownTail(writer)
    all {
      writer.unsent.contains(p),
      replicas' = replicas,
      writer' = {
        ...writer,
        unsent: writer.unsent.exclude(Set(p)),
        carried: updatedCarried(writer, kt),
        sent: writer.sent.set(p, if (Duplicates > 0) kt else 0),
      },
      rec' = rec,
      chk' = chk,
      ledgerMap' = ledgerMap,
      closeLog' = closeLog,
      fetch' = fetch,
      net' = { ...net, writes: net.writes.union(Replicas.map(r => { replica: r, pos: p, carriedTail: kt })) },
      answers' = answers,
      faults' = faults,
      refinesAtomic' = refinesAtomic,
    }
  }

  action resendWrite(r: int, p: int): bool = {
    val kt = knownTail(writer)
    val msg: WriteMsg = { replica: r, pos: p, carriedTail: kt }
    all {
      faults.dups < Duplicates,
      p >= writer.completed and p < writer.nextPos,
      not(writer.unsent.contains(p)),
      SeparateAnswers or not(writer.written.get(p).contains(r)),
      not(net.writes.contains(msg)),
      replicas' = replicas,
      writer' = { ...writer, carried: updatedCarried(writer, kt) },
      rec' = rec,
      chk' = chk,
      ledgerMap' = ledgerMap,
      closeLog' = closeLog,
      fetch' = fetch,
      net' = { ...net, writes: net.writes.union(Set(msg)) },
      answers' = answers,
      faults' = { ...faults, dups: faults.dups + 1 },
      refinesAtomic' = refinesAtomic,
    }
  }

  def applyWriterAnswer(
    w: WriterState,
    r: int,
    p: int,
    result: str,
    fresh: bool
  ): WriterState = {
    val counts =
      result == "written" or
      (result == "sealed" and Mutation == "count-sealed") or
      (result == "dropped" and Mutation == "count-dropped")
    val isRepeat = counts and w.written.get(p).contains(r)
    val newWritten =
      if (counts) w.written.set(p, w.written.get(p).union(Set(r))) else w.written
    val newRepeats =
      if (isRepeat and Mutation == "count-answers") w.repeats.set(p, w.repeats.get(p) + 1)
      else w.repeats
    {
      ...w,
      written: newWritten,
      repeats: newRepeats,
      repeated: w.repeated or (isRepeat and result == "written"),
      freshRepeat: w.freshRepeat or fresh,
      sawSealed: w.sawSealed or result == "sealed" or result == "dropped",
    }
  }

  action deliverWrite(m: WriteMsg, keep: bool, deliver: bool): bool = {
    val r = m.replica
    val p = m.pos
    val rep = replicas.get(r)
    val isLost = faults.lost.contains(r)
    val refused = answersDropped(rep)
    val isSealed = rep.exists and rep.sealed
    val isCorrupt = rep.corrupt.contains(p)
    val accepted = not(refused) and not(isCorrupt) and (not(isSealed) or Mutation == "sealed-accepts")
    val isNew = accepted and not(rep.stored.contains(p))
    val awaitsFlush = isNew and writesAnswerAtFlush and not(isLost)
    val fresh = Duplicates > 0 and accepted and rep.stored.contains(p) and m.carriedTail > writer.sent.get(p)
    val result =
      if (refused) "dropped"
      else if (accepted) "written"
      else if (isSealed) "sealed"
      else "corrupt"
    val ans: Answer = { kind: "write", client: 0, replica: r, pos: p, result: result, flag: fresh, tail: 0 }
    val nextWrites = if (awaitsFlush or keep) net.writes else net.writes.exclude(Set(m))
    val nextDups = if (keep and not(awaitsFlush)) faults.dups + 1 else faults.dups
    val updatedRep =
      if (isLost) rep
      else {
        val baseRep = {
          ...rep,
          exists: if (refused) rep.exists else true,
          sealed: if (rep.exists or refused) rep.sealed else false,
          retained: if (refused) rep.retained else maxInt(rep.retained, m.carriedTail),
          stored: if (accepted) rep.stored.union(Set(p)) else rep.stored,
          volData:
            if (isNew and (awaitsFlush or Mutation == "write-volatile"))
              rep.volData.union(Set(p))
            else rep.volData,
          volCreate: rep.volCreate or (not(refused) and not(rep.exists) and Mutation == "create-volatile"),
          ansData: if (not(awaitsFlush) and accepted) rep.ansData.union(Set(p)) else rep.ansData,
          ansSealed: rep.ansSealed or (not(awaitsFlush) and isSealed and not(accepted)),
        }
        applyCreate(replicas, r, baseRep, not(refused) and not(rep.exists))
      }
    val nextWriter =
      if (isLost or awaitsFlush or SeparateAnswers) writer
      else applyWriterAnswer(writer, r, p, result, fresh)
    val nextAnswers =
      if (isLost or awaitsFlush or not(SeparateAnswers) or not(deliver)) answers
      else answers.union(Set(ans))
    all {
      not(rep.volData.contains(p)),
      rep.volSeal == Set() or Mutation == "write-sealed-volatile",
      replicas' = if (isLost) replicas else replicas.set(r, updatedRep),
      writer' = nextWriter,
      rec' = rec,
      chk' = chk,
      ledgerMap' = ledgerMap,
      closeLog' = closeLog,
      fetch' = fetch,
      net' = { ...net, writes: nextWrites },
      answers' = nextAnswers,
      faults' = { ...faults, dups: nextDups },
      refinesAtomic' = refinesAtomic,
    }
  }

  action admitWrite(m: WriteMsg): bool = {
    val r = m.replica
    val rep = replicas.get(r)
    val baseRep = {
      ...rep,
      exists: true,
      sealed: if (rep.exists) rep.sealed else false,
      retained: maxInt(rep.retained, m.carriedTail),
    }
    val updatedRep = applyCreate(replicas, r, baseRep, not(rep.exists))
    all {
      not(faults.lost.contains(r)),
      not(answersDropped(rep)),
      not(rep.exists) or rep.retained < m.carriedTail,
      replicas' = replicas.set(r, updatedRep),
      writer' = writer,
      rec' = rec,
      chk' = chk,
      ledgerMap' = ledgerMap,
      closeLog' = closeLog,
      fetch' = fetch,
      net' = net,
      answers' = answers,
      faults' = faults,
      refinesAtomic' = refinesAtomic,
    }
  }

  action complete: bool = {
    val nextWriter = {
      ...writer,
      completed: writer.completed + 1,
      ackAfterSealed: writer.ackAfterSealed or writer.sawSealed,
    }
    all {
      writer.completed < writer.nextPos,
      writer.written.get(writer.completed).size() + writer.repeats.get(writer.completed) >= Quorum,
      replicas' = replicas,
      writer' = nextWriter,
      rec' = rec,
      chk' = chk,
      ledgerMap' = ledgerMap,
      closeLog' = closeLog,
      fetch' = fetch,
      net' = net,
      answers' = answers,
      faults' = faults,
      refinesAtomic' = checkAtomic({ ...currentAtomic, acked: nextWriter.completed }),
    }
  }

  action writeTail: bool = {
    val kt = knownTail(writer)
    all {
      TailWrites,
      kt > writer.carried,
      replicas' = replicas,
      writer' = { ...writer, carried: kt },
      rec' = rec,
      chk' = chk,
      ledgerMap' = ledgerMap,
      closeLog' = closeLog,
      fetch' = fetch,
      net' = { ...net, tails: net.tails.union(Replicas.map(r => { replica: r, carriedTail: kt })) },
      answers' = answers,
      faults' = faults,
      refinesAtomic' = refinesAtomic,
    }
  }

  action deliverTail(m: TailMsg, keep: bool): bool = {
    val r = m.replica
    val rep = replicas.get(r)
    val nextReplicas =
      if (rep.exists and not(faults.lost.contains(r)))
        replicas.set(r, { ...rep, retained: maxInt(rep.retained, m.carriedTail) })
      else replicas
    all {
      replicas' = nextReplicas,
      writer' = writer,
      rec' = rec,
      chk' = chk,
      ledgerMap' = ledgerMap,
      closeLog' = closeLog,
      fetch' = fetch,
      net' = { ...net, tails: if (keep) net.tails else net.tails.exclude(Set(m)) },
      answers' = answers,
      faults' = { ...faults, dups: if (keep) faults.dups + 1 else faults.dups },
      refinesAtomic' = refinesAtomic,
    }
  }

  // ---------------------------------------------------------------------------
  // Recovery actions (LP-I6, LP-I7, LP-I8, LP-I11)
  // ---------------------------------------------------------------------------

  def copyTail(x: RecovererState, p: int): int =
    if (Mutation == "retain-recovered") p + 1 else x.start

  def sendReads(i: int, p: int, sealedBy: Set[int]): Set[ReadMsg] =
    Replicas.map(r => { rec: i, replica: r, pos: p, marked: sealedBy.contains(r) })

  def markRefused(recs: int -> RecovererState, fromRec: bool, senderId: int): int -> RecovererState =
    if (fromRec and isActive(recs.get(senderId).phase) and Mutation != "ignore-dropped")
      recs.set(senderId, { ...recs.get(senderId), phase: "superseded" })
    else recs

  def copyTo(i: int, x: RecovererState, p: int): Set[CopyMsg] = {
    val targets =
      if (Mutation == "no-late-copy") x.absent.get(p)
      else Replicas.exclude(x.copied.get(p))
    if (Mutation == "no-rereplication") Set()
    else targets.map(q => {
      fromRecoverer: true,
      senderId: i,
      replica: q,
      pos: p,
      carriedTail: copyTail(x, p),
    })
  }

  pure def firstUndecided(found: Set[int], pos: int, maxSlot: int): int =
    rangeSet(pos, maxSlot).fold(maxSlot, (best, p) =>
      if (not(found.contains(p)) and p < best) p else best
    )

  def applyDecide(
    recs: int -> RecovererState,
    pendingCopies: Set[CopyMsg],
    pendingReads: Set[ReadMsg],
    i: int,
    x: RecovererState
  ): RecoveryEffect = {
    val q = firstUndecided(x.found, x.pos, Slots)
    val ended = absenceCovered(x.absent.get(q))
    val hi =
      if (ended) x.issued - 1
      else minInt(minInt(q + Window - 1, x.start + 2 * Window), Slots)
    val decided = rangeSet(x.pos, q - 1).filter(d => x.copied.get(d).size() < Quorum)
    val updatedRec = {
      ...x,
      pos: q,
      phase: if (ended) "closing" else x.phase,
      end: if (ended) q else x.end,
      issued: maxInt(x.issued, hi + 1),
      absent: readSlots.mapBy(a => if (a < q) Set() else x.absent.get(a)),
    }
    val newCopies = decided.map(d => copyTo(i, x, d)).flatten()
    val newReads = rangeSet(x.issued, hi).map(a => sendReads(i, a, x.sealedBy)).flatten()
    {
      rec: recs.set(i, updatedRec),
      copies: pendingCopies.union(newCopies),
      reads: pendingReads.union(newReads),
    }
  }

  def applySealAnswer(
    recs: int -> RecovererState,
    pendingCopies: Set[CopyMsg],
    pendingReads: Set[ReadMsg],
    i: int,
    r: int,
    result: str,
    reportedTail: int
  ): RecoveryEffect = {
    val cur = recs.get(i)
    if (result == "dropped") {
      rec: markRefused(recs, true, i),
      copies: pendingCopies,
      reads: pendingReads,
    } else {
      val newSealedBy = cur.sealedBy.union(Set(r))
      val isSealing = cur.phase == "sealing"
      val newStart = if (isSealing) maxInt(cur.start, reportedTail) else cur.start
      val beginReading = isSealing and sealCovered(newSealedBy)
      val updated = {
        ...cur,
        sealedBy: newSealedBy,
        start: newStart,
        phase: if (beginReading) "reading" else cur.phase,
        pos: if (beginReading) newStart else cur.pos,
        issued: if (beginReading) newStart else cur.issued,
      }
      if (beginReading) applyDecide(recs, pendingCopies, pendingReads, i, updated)
      else { rec: recs.set(i, updated), copies: pendingCopies, reads: pendingReads }
    }
  }

  def applyReadAnswer(
    recs: int -> RecovererState,
    pendingCopies: Set[CopyMsg],
    pendingReads: Set[ReadMsg],
    i: int,
    r: int,
    p: int,
    result: str,
    marked: bool
  ): RecoveryEffect = {
    val cur = recs.get(i)
    if (result == "dropped") {
      rec: markRefused(recs, true, i),
      copies: pendingCopies,
      reads: pendingReads,
    } else {
      val present = result == "batch"
      val counted = marked or Mutation == "unsealed-negatives"
      val held =
        if (present) {
          ...cur,
          found: cur.found.union(Set(p)),
          copied: cur.copied.set(p, cur.copied.get(p).union(Set(r))),
        } else cur
      val updated =
        if (result == "missing" and counted and p >= cur.pos and not(cur.found.contains(p)))
          { ...held, absent: held.absent.set(p, held.absent.get(p).union(Set(r))) }
        else held
      if (cur.phase == "reading") applyDecide(recs, pendingCopies, pendingReads, i, updated)
      else if (cur.phase == "closing" and present and cur.found.contains(p))
        { rec: recs.set(i, held), copies: pendingCopies, reads: pendingReads }
      else { rec: recs, copies: pendingCopies, reads: pendingReads }
    }
  }

  def applyCopyAnswer(
    recs: int -> RecovererState,
    fromRec: bool,
    senderId: int,
    r: int,
    p: int,
    result: str,
    repaired: bool
  ): int -> RecovererState =
    if (result == "dropped") markRefused(recs, fromRec, senderId)
    else if (fromRec) {
      val cur = recs.get(senderId)
      if (isActive(cur.phase))
        recs.set(senderId, {
          ...cur,
          copied: cur.copied.set(p, cur.copied.get(p).union(Set(r))),
          repaired: cur.repaired or repaired,
        })
      else recs
    } else recs

  action beginRecovery(i: int, t: int): bool = {
    val cur = rec.get(i)
    val nextRec = rec.set(i, {
      ...cur,
      phase: "sealing",
      version: ledgerMap.version,
      known: t,
      start: t,
    })
    all {
      cur.phase == "idle",
      ledgerMap.status == "open",
      replicas' = replicas,
      writer' = writer,
      rec' = nextRec,
      chk' = chk,
      ledgerMap' = ledgerMap,
      closeLog' = closeLog,
      fetch' = fetch,
      net' = { ...net, seals: net.seals.union(Replicas.map(r => { rec: i, replica: r })) },
      answers' = answers,
      faults' = faults,
      refinesAtomic' = checkAtomic({ ...currentAtomic, seal: sealOf(ledgerMap.status, nextRec) }),
    }
  }

  action deliverSeal(m: SealMsg, keep: bool, deliver: bool): bool = {
    val i = m.rec
    val r = m.replica
    val rep = replicas.get(r)
    val isLost = faults.lost.contains(r)
    val serves = not(isLost) and not(answersDropped(rep))
    val create = rep.exists or Mutation != "seal-no-create"
    val marks = create and not(rep.exists and rep.sealed)
    val awaitsFlush = serves and marks and sealsAnswerAtFlush
    val reportedTail = maxInt(if (rep.exists) rep.retained else 0, rec.get(i).known)
    val res = if (serves) "ok" else "dropped"
    val ans: Answer = {
      kind: "seal",
      client: i,
      replica: r,
      pos: 0,
      result: res,
      flag: false,
      tail: if (serves) reportedTail else 0,
    }
    val nextSeals = if (awaitsFlush or keep) net.seals else net.seals.exclude(Set(m))
    val nextDups = if (keep and not(awaitsFlush)) faults.dups + 1 else faults.dups
    val updatedRep =
      if (not(serves)) rep
      else {
        val baseRep = {
          ...rep,
          exists: if (create) true else rep.exists,
          sealed: if (create) true else rep.sealed,
          retained: if (create) reportedTail else rep.retained,
          volSeal:
            if (marks and (awaitsFlush or Mutation == "seal-volatile")) Set(i)
            else rep.volSeal,
          volCreate: rep.volCreate or (create and not(rep.exists) and Mutation == "create-volatile"),
          ansSealed: if (awaitsFlush) rep.ansSealed else true,
        }
        applyCreate(replicas, r, baseRep, create and not(rep.exists))
      }
    val applyInline = not(isLost) and not(awaitsFlush) and not(SeparateAnswers)
    val eff =
      if (applyInline)
        applySealAnswer(rec, net.copies, net.reads, i, r, res, if (serves) reportedTail else 0)
      else { rec: rec, copies: net.copies, reads: net.reads }
    val nextAnswers =
      if (not(isLost) and not(awaitsFlush) and SeparateAnswers and deliver) answers.union(Set(ans))
      else answers
    all {
      rep.volData == Set(),
      rep.volSeal == Set() or (Mutation == "repeat-seal-volatile" and not(rep.volSeal.contains(i))),
      replicas' = if (serves) replicas.set(r, updatedRep) else replicas,
      writer' = writer,
      rec' = eff.rec,
      chk' = chk,
      ledgerMap' = ledgerMap,
      closeLog' = closeLog,
      fetch' = fetch,
      net' = { ...net, seals: nextSeals, reads: eff.reads, copies: eff.copies },
      answers' = nextAnswers,
      faults' = { ...faults, dups: nextDups },
      refinesAtomic' =
        if (applyInline) checkAtomic({ ...currentAtomic, seal: sealOf(ledgerMap.status, eff.rec) })
        else refinesAtomic,
    }
  }

  action deliverRead(m: ReadMsg, keep: bool, deliver: bool): bool = {
    val i = m.rec
    val r = m.replica
    val p = m.pos
    val rep = replicas.get(r)
    val isLost = faults.lost.contains(r)
    val serves = not(isLost) and not(answersDropped(rep))
    val isCorrupt = rep.corrupt.contains(p)
    val visible =
      if (Mutation == "read-volatile") rep.stored
      else rep.stored.exclude(rep.volData)
    val present = visible.contains(p) and not(isCorrupt)
    val result =
      if (not(serves)) "dropped"
      else if (present) "batch"
      else if (isCorrupt and Mutation != "corrupt-as-missing") "corrupt"
      else "missing"
    val ans: Answer = { kind: "read", client: i, replica: r, pos: p, result: result, flag: m.marked, tail: 0 }
    val leftReads = if (keep) net.reads else net.reads.exclude(Set(m))
    val updatedRep = {
      ...rep,
      retained: if (serves and rep.exists) maxInt(rep.retained, rec.get(i).start) else rep.retained,
      ansData: if (serves and present) rep.ansData.union(Set(p)) else rep.ansData,
    }
    val applyInline = not(isLost) and not(SeparateAnswers)
    val eff =
      if (applyInline)
        applyReadAnswer(rec, net.copies, leftReads, i, r, p, result, m.marked)
      else { rec: rec, copies: net.copies, reads: leftReads }
    val nextAnswers =
      if (not(isLost) and SeparateAnswers and deliver) answers.union(Set(ans))
      else answers
    all {
      replicas' = if (serves) replicas.set(r, updatedRep) else replicas,
      writer' = writer,
      rec' = eff.rec,
      chk' = chk,
      ledgerMap' = ledgerMap,
      closeLog' = closeLog,
      fetch' = fetch,
      net' = { ...net, reads: eff.reads, copies: eff.copies },
      answers' = nextAnswers,
      faults' = { ...faults, dups: if (keep) faults.dups + 1 else faults.dups },
      refinesAtomic' =
        if (applyInline) checkAtomic({ ...currentAtomic, seal: sealOf(ledgerMap.status, eff.rec) })
        else refinesAtomic,
    }
  }

  action failRead(m: ReadMsg): bool = all {
    Faults.contains("read-failure"),
    replicas' = replicas,
    writer' = writer,
    rec' = rec,
    chk' = chk,
    ledgerMap' = ledgerMap,
    closeLog' = closeLog,
    fetch' = fetch,
    net' = { ...net, reads: net.reads.exclude(Set(m)) },
    answers' = answers,
    faults' = faults,
    refinesAtomic' = refinesAtomic,
  }

  action deliverCopy(m: CopyMsg, keep: bool, deliver: bool): bool = {
    val r = m.replica
    val p = m.pos
    val rep = replicas.get(r)
    val isLost = faults.lost.contains(r)
    val refused = answersDropped(rep) and Mutation != "copy-ignores-tombstone"
    val res = if (refused) "dropped" else "written"
    val isRepaired = rep.corrupt.contains(p)
    val ans: Answer = {
      kind: "copy",
      client: m.senderId,
      replica: r,
      pos: p,
      result: res,
      flag: m.fromRecoverer,
      tail: if (isRepaired) 1 else 0,
    }
    val updatedRep =
      if (isLost or refused) rep
      else {
        val baseRep = {
          ...rep,
          exists: true,
          sealed: if (not(rep.exists) or Mutation == "copy-unseals") false else rep.sealed,
          stored: rep.stored.union(Set(p)),
          corrupt: rep.corrupt.exclude(Set(p)),
          retained: maxInt(rep.retained, m.carriedTail),
          volData:
            if (not(rep.stored.contains(p)) and Mutation == "copy-volatile")
              rep.volData.union(Set(p))
            else rep.volData,
          volCreate: rep.volCreate or (not(rep.exists) and Mutation == "create-volatile"),
          ansData: rep.ansData.union(Set(p)),
        }
        applyCreate(replicas, r, baseRep, not(rep.exists))
      }
    val applyInline = not(isLost) and not(SeparateAnswers)
    val nextRec =
      if (applyInline) applyCopyAnswer(rec, m.fromRecoverer, m.senderId, r, p, res, isRepaired)
      else rec
    val nextAnswers =
      if (not(isLost) and SeparateAnswers and deliver) answers.union(Set(ans))
      else answers
    all {
      not(rep.volData.contains(p)),
      replicas' = if (isLost or refused) replicas else replicas.set(r, updatedRep),
      writer' = writer,
      rec' = nextRec,
      chk' = chk,
      ledgerMap' = ledgerMap,
      closeLog' = closeLog,
      fetch' = { ...fetch, repairs: fetch.repairs or (not(m.fromRecoverer) and not(isLost) and not(refused)) },
      net' = { ...net, copies: if (keep) net.copies else net.copies.exclude(Set(m)) },
      answers' = nextAnswers,
      faults' = { ...faults, dups: if (keep) faults.dups + 1 else faults.dups },
      refinesAtomic' =
        if (applyInline) checkAtomic({ ...currentAtomic, seal: sealOf(ledgerMap.status, nextRec) })
        else refinesAtomic,
    }
  }

  def copiesDone(recs: int -> RecovererState, i: int): bool = {
    val x = recs.get(i)
    Mutation == "no-rereplication" or
    x.found.filter(q => q < x.end).forall(p => x.copied.get(p).size() >= Quorum)
  }

  action closeLedger(i: int): bool = {
    val x = rec.get(i)
    val wins = ledgerMap.version == x.version or Mutation == "unconditional-close"
    val nextMap =
      if (wins) { status: "closed", end: x.end, version: ledgerMap.version + 1 }
      else ledgerMap
    val nextCloseLog =
      if (wins) {
        closes: closeLog.closes.union(Set(x.end)),
        ackedAtClose: if (ledgerMap.status == "open") writer.completed else closeLog.ackedAtClose,
      } else closeLog
    val nextRec = rec.set(i, { ...x, phase: if (wins) "closed" else "superseded" })
    all {
      x.phase == "closing",
      copiesDone(rec, i),
      replicas' = replicas,
      writer' = writer,
      rec' = nextRec,
      chk' = chk,
      ledgerMap' = nextMap,
      closeLog' = nextCloseLog,
      fetch' = fetch,
      net' = net,
      answers' = answers,
      faults' = faults,
      refinesAtomic' = checkAtomic({
        ...currentAtomic,
        status: nextMap.status,
        end: nextMap.end,
        seal: sealOf(nextMap.status, nextRec),
      }),
    }
  }

  action retryReads(i: int): bool = {
    val x = rec.get(i)
    val openSlots = rangeSet(x.pos, x.issued - 1).filter(q => not(x.found.contains(q)))
    val again = openSlots.map(q =>
      x.sealedBy.exclude(x.absent.get(q))
        .filter(r => not(net.reads.exists(m => m.rec == i and m.replica == r and m.pos == q)))
        .map(r => { rec: i, replica: r, pos: q, marked: true })
    ).flatten()
    all {
      x.phase == "reading",
      again != Set(),
      replicas' = replicas,
      writer' = writer,
      rec' = rec,
      chk' = chk,
      ledgerMap' = ledgerMap,
      closeLog' = closeLog,
      fetch' = fetch,
      net' = { ...net, reads: net.reads.union(again) },
      answers' = answers,
      faults' = faults,
      refinesAtomic' = refinesAtomic,
    }
  }

  action learnEnd(i: int): bool = {
    val x = rec.get(i)
    val v = if (Mutation == "learn-own-end") x.end else ledgerMap.end
    val nextReplicas = Replicas.mapBy(r =>
      val rep = replicas.get(r)
      if (rep.exists and not(faults.lost.contains(r))) { ...rep, retained: maxInt(rep.retained, v) }
      else rep
    )
    all {
      x.phase == "closed" or x.phase == "superseded",
      not(x.learned),
      mapEnded(ledgerMap),
      v >= 0,
      replicas' = nextReplicas,
      writer' = writer,
      rec' = rec.set(i, { ...x, learned: true }),
      chk' = chk,
      ledgerMap' = ledgerMap,
      closeLog' = closeLog,
      fetch' = fetch,
      net' = net,
      answers' = answers,
      faults' = faults,
      refinesAtomic' = refinesAtomic,
    }
  }

  // ---------------------------------------------------------------------------
  // Drops, restarts, and WAL flush (LR-I3, LR-I4, LR-I6, LR-I7, LS-I2)
  // ---------------------------------------------------------------------------

  action dropLedger: bool = {
    val nextMap = { ...ledgerMap, status: "dropped", version: ledgerMap.version + 1 }
    all {
      ledgerMap.status == "closed" or (Mutation == "drop-open" and ledgerMap.status == "open"),
      replicas' = replicas,
      writer' = writer,
      rec' = rec,
      chk' = chk,
      ledgerMap' = nextMap,
      closeLog' = closeLog,
      fetch' = fetch,
      net' = net,
      answers' = answers,
      faults' = faults,
      refinesAtomic' = checkAtomic({
        ...currentAtomic,
        status: nextMap.status,
        seal: sealOf(nextMap.status, rec),
      }),
    }
  }

  action dropReplica(r: int): bool = {
    val rep = replicas.get(r)
    val updatedRep = {
      ...initialReplica,
      tomb: if (Mutation == "no-tombstone") rep.tomb else true,
      createDecided: rep.createDecided,
      stray: if (rep.stray == "held") "dropped" else rep.stray,
    }
    all {
      ledgerMap.status == "dropped",
      rep.exists,
      not(faults.lost.contains(r)),
      replicas' = replicas.set(r, updatedRep),
      writer' = writer,
      rec' = rec,
      chk' = chk,
      ledgerMap' = ledgerMap,
      closeLog' = closeLog,
      fetch' = fetch,
      net' = net,
      answers' = answers,
      faults' = faults,
      refinesAtomic' = refinesAtomic,
    }
  }

  action decideCreate(r: int): bool = {
    val rep = replicas.get(r)
    all {
      Mutation == "split-create",
      not(rep.tomb),
      not(rep.exists),
      not(rep.createDecided),
      replicas' = replicas.set(r, { ...rep, createDecided: true }),
      writer' = writer,
      rec' = rec,
      chk' = chk,
      ledgerMap' = ledgerMap,
      closeLog' = closeLog,
      fetch' = fetch,
      net' = net,
      answers' = answers,
      faults' = faults,
      refinesAtomic' = refinesAtomic,
    }
  }

  action restart(r: int): bool = {
    val rep = replicas.get(r)
    val kept = rep.stored.exclude(rep.volData)
    val h = maxOf(kept)
    val rebuilt =
      if (kept == Set()) 0
      else if (Mutation == "reopen-h1") h + 1
      else maxInt(0, h + 1 - Window)
    val unseal = rep.sealed and (rep.volSeal != Set() or Mutation == "restart-drops-seal")
    val gone = rep.volCreate
    val updatedRep = {
      ...rep,
      exists: if (gone) false else rep.exists,
      stored: if (gone) Set() else kept,
      corrupt: if (gone) Set() else rep.corrupt.exclude(rep.volData),
      retained: if (gone) 0 else rebuilt,
      sealed: if (unseal or gone) false else rep.sealed,
      volData: Set(),
      volSeal: Set(),
      volCreate: false,
    }
    all {
      rep.exists,
      not(faults.lost.contains(r)),
      rebuilt != rep.retained or unseal or hasVolatile(rep),
      replicas' = replicas.set(r, updatedRep),
      writer' = writer,
      rec' = rec,
      chk' = chk,
      ledgerMap' = ledgerMap,
      closeLog' = closeLog,
      fetch' = fetch,
      net' = net,
      answers' = answers,
      faults' = faults,
      refinesAtomic' = refinesAtomic,
    }
  }

  action flushReplica(r: int): bool = {
    val rep = replicas.get(r)
    all {
      hasVolatile(rep),
      replicas' = replicas.set(r, { ...rep, volData: Set(), volSeal: Set(), volCreate: false }),
      writer' = writer,
      rec' = rec,
      chk' = chk,
      ledgerMap' = ledgerMap,
      closeLog' = closeLog,
      fetch' = fetch,
      net' = net,
      answers' = answers,
      faults' = faults,
      refinesAtomic' = refinesAtomic,
    }
  }

  // ---------------------------------------------------------------------------
  // Faults within the Q - 1 replica budget
  // ---------------------------------------------------------------------------

  def mayFault(r: int): bool =
    faults.faulty.contains(r) or faults.faulty.union(Set(r)).size() <= Quorum - 1

  action loseNode(r: int): bool = all {
    Faults.contains("lose-node"),
    not(faults.faulty.contains(r)),
    mayFault(r),
    replicas' = replicas,
    writer' = writer,
    rec' = rec,
    chk' = chk,
    ledgerMap' = ledgerMap,
    closeLog' = closeLog,
    fetch' = fetch,
    net' = net,
    answers' = answers,
    faults' = { ...faults, lost: faults.lost.union(Set(r)), faulty: faults.faulty.union(Set(r)) },
    refinesAtomic' = refinesAtomic,
  }

  action corruptSlot(r: int, p: int): bool = {
    val rep = replicas.get(r)
    all {
      Faults.contains("corrupt"),
      rep.exists,
      not(faults.lost.contains(r)),
      mayFault(r),
      not(rep.corrupt.contains(p)),
      replicas' = replicas.set(r, { ...rep, corrupt: rep.corrupt.union(Set(p)) }),
      writer' = writer,
      rec' = rec,
      chk' = chk,
      ledgerMap' = ledgerMap,
      closeLog' = closeLog,
      fetch' = fetch,
      net' = net,
      answers' = answers,
      faults' = { ...faults, faulty: faults.faulty.union(Set(r)) },
      refinesAtomic' = refinesAtomic,
    }
  }

  action loseReplica(r: int): bool = {
    val rep = replicas.get(r)
    all {
      Faults.contains("amnesia"),
      rep.exists,
      not(faults.faulty.contains(r)),
      mayFault(r),
      rep.stored != Set() or rep.sealed,
      replicas' = replicas.set(r, { ...rep, stored: Set(), sealed: false, retained: 0 }),
      writer' = writer,
      rec' = rec,
      chk' = chk,
      ledgerMap' = ledgerMap,
      closeLog' = closeLog,
      fetch' = fetch,
      net' = net,
      answers' = answers,
      faults' = { ...faults, faulty: faults.faulty.union(Set(r)), amnesiac: faults.amnesiac.union(Set(r)) },
      refinesAtomic' = refinesAtomic,
    }
  }

  action loseTombstone(r: int): bool = {
    val rep = replicas.get(r)
    all {
      Faults.contains("tombstone-loss"),
      rep.tomb,
      not(faults.faulty.contains(r)),
      mayFault(r),
      replicas' = replicas.set(r, { ...rep, tomb: false }),
      writer' = writer,
      rec' = rec,
      chk' = chk,
      ledgerMap' = ledgerMap,
      closeLog' = closeLog,
      fetch' = fetch,
      net' = net,
      answers' = answers,
      faults' = { ...faults, faulty: faults.faulty.union(Set(r)) },
      refinesAtomic' = refinesAtomic,
    }
  }

  // ---------------------------------------------------------------------------
  // Readers: tail checks (LP-I9, Delos erratum)
  // ---------------------------------------------------------------------------

  action beginCheck(c: int, t: int): bool = {
    val cur = chk.get(c)
    val isLate = mapEnded(ledgerMap) or Recoverers.exists(i => isFinished(rec.get(i).phase))
    val nextChk = chk.set(c, {
      ...cur,
      phase: "reading",
      carry: t,
      reportedTail: t,
      late: isLate,
    })
    all {
      cur.phase == "idle",
      replicas' = replicas,
      writer' = writer,
      rec' = rec,
      chk' = nextChk,
      ledgerMap' = ledgerMap,
      closeLog' = closeLog,
      fetch' = fetch,
      net' = net,
      answers' = answers,
      faults' = faults,
      refinesAtomic' = checkAtomic({
        ...currentAtomic,
        checks: Readers.mapBy(k => atomicCheckOf(nextChk.get(k))),
      }),
    }
  }

  def answerTail(cur: CheckState, rep: ReplicaState): int =
    if (not(rep.exists)) cur.carry
    else if (Mutation == "check-stored-tail")
      (if (rep.stored == Set()) 0 else maxOf(rep.stored) + 1)
    else maxInt(rep.retained, cur.carry)

  def canCheckReceive(chks: int -> CheckState, c: int, r: int): bool = {
    val cur = chks.get(c)
    cur.phase == "reading" and not(cur.answered.contains(r))
  }

  def applyCheckAnswer(
    chks: int -> CheckState,
    mStatus: str,
    c: int,
    r: int,
    isSealed: bool,
    reportedTail: int
  ): int -> CheckState = {
    val cur = chks.get(c)
    val responded = cur.answered.union(Set(r))
    val newSealedAnswers =
      if (isSealed) cur.sealedAnswers.union(Set(r)) else cur.sealedAnswers
    val decide =
      responded.size() >= checkSize or (isSealed and Mutation != "sealed-quorum")
    chks.set(c, {
      ...cur,
      answered: responded,
      sealedAnswers: newSealedAnswers,
      reportedTail: maxInt(cur.reportedTail, reportedTail),
      phase: if (decide) "done" else "reading",
      sealed: decide and reportsSealed(newSealedAnswers),
      open: decide and mStatus == "open",
    })
  }

  action checkAnswer(c: int, r: int, deliver: bool): bool = {
    val cur = chk.get(c)
    val rep = replicas.get(r)
    val isSealed =
      ((rep.exists and rep.sealed) or
       (answersDropped(rep) and Mutation != "check-counts-dropped")) and
      Mutation != "check-ignores-seal"
    val t = answerTail(cur, rep)
    val again = cur.asked.contains(r)
    val ans: Answer = { kind: "check", client: c, replica: r, pos: 0, result: "", flag: isSealed, tail: t }
    val updatedRep = {
      ...rep,
      retained: if (rep.exists) maxInt(rep.retained, cur.carry) else rep.retained,
      ansSealed: rep.ansSealed or (isSealed and rep.exists),
    }
    val nextChk =
      if (SeparateAnswers) chk.set(c, { ...cur, asked: cur.asked.union(Set(r)) })
      else applyCheckAnswer(chk, ledgerMap.status, c, r, isSealed, t)
    all {
      not(faults.lost.contains(r)),
      rep.volSeal == Set() or Mutation == "check-sealed-volatile",
      if (SeparateAnswers) cur.phase != "idle" and (not(again) or faults.dups < Duplicates)
      else canCheckReceive(chk, c, r),
      replicas' = replicas.set(r, updatedRep),
      writer' = writer,
      rec' = rec,
      chk' = nextChk,
      ledgerMap' = ledgerMap,
      closeLog' = closeLog,
      fetch' = fetch,
      net' = net,
      answers' = if (SeparateAnswers and deliver) answers.union(Set(ans)) else answers,
      faults' = { ...faults, dups: if (SeparateAnswers and again) faults.dups + 1 else faults.dups },
      refinesAtomic' =
        if (SeparateAnswers) refinesAtomic
        else checkAtomic({ ...currentAtomic, checks: Readers.mapBy(k => atomicCheckOf(nextChk.get(k))) }),
    }
  }

  // ---------------------------------------------------------------------------
  // Fetchers: single-replica reads, read repair, and drains (LP-I10, LG-I8)
  // ---------------------------------------------------------------------------

  def fetchedTail(rep: ReplicaState): int =
    if (Mutation == "fetch-stored-tail" and rep.stored != Set()) maxOf(rep.stored) + 1
    else rep.retained

  action learnTail(f: int, r: int): bool = {
    val rep = replicas.get(r)
    val ft = fetchedTail(rep)
    val fk = fetch.known.get(f)
    val nextKnown = fetch.known.set(f, maxInt(fk, ft))
    all {
      rep.exists,
      not(faults.lost.contains(r)),
      fk < ft or rep.retained < fk,
      replicas' = replicas.set(r, { ...rep, retained: maxInt(rep.retained, fk) }),
      writer' = writer,
      rec' = rec,
      chk' = chk,
      ledgerMap' = ledgerMap,
      closeLog' = closeLog,
      fetch' = { ...fetch, known: nextKnown },
      net' = net,
      answers' = answers,
      faults' = faults,
      refinesAtomic' = checkAtomic({ ...currentAtomic, known: nextKnown }),
    }
  }

  action readMap(f: int): bool = {
    val nextKnown = fetch.known.set(f, ledgerMap.end)
    all {
      mapEnded(ledgerMap),
      fetch.known.get(f) < ledgerMap.end,
      replicas' = replicas,
      writer' = writer,
      rec' = rec,
      chk' = chk,
      ledgerMap' = ledgerMap,
      closeLog' = closeLog,
      fetch' = { ...fetch, known: nextKnown },
      net' = net,
      answers' = answers,
      faults' = faults,
      refinesAtomic' = checkAtomic({ ...currentAtomic, known: nextKnown }),
    }
  }

  def lacksSlot(q: int, p: int): bool = {
    val rep = replicas.get(q)
    not(faults.lost.contains(q)) and not(answersDropped(rep)) and
    (not(rep.stored.contains(p)) or rep.corrupt.contains(p))
  }

  action readBelowTail(f: int, p: int, r: int, q: int): bool = {
    val rep = replicas.get(r)
    val entry = (p, ledgerMap.status == "open")
    val durableIntact = rep.stored.exclude(rep.corrupt.union(rep.volData))
    val repairMsg: CopyMsg = {
      fromRecoverer: false,
      senderId: f,
      replica: q,
      pos: p,
      carriedTail: fetch.known.get(f),
    }
    val nextServed = fetch.served.union(Set(entry))
    all {
      p < fetch.known.get(f),
      not(faults.lost.contains(r)),
      durableIntact.contains(p),
      q == r or lacksSlot(q, p),
      not(fetch.served.contains(entry)) or q != r,
      replicas' = replicas.set(r, { ...rep, ansData: rep.ansData.union(Set(p)) }),
      writer' = writer,
      rec' = rec,
      chk' = chk,
      ledgerMap' = ledgerMap,
      closeLog' = closeLog,
      fetch' = { ...fetch, served: nextServed },
      net' = { ...net, copies: if (q == r) net.copies else net.copies.union(Set(repairMsg)) },
      answers' = answers,
      faults' = faults,
      refinesAtomic' = checkAtomic({ ...currentAtomic, served: nextServed }),
    }
  }

  action drain(f: int): bool = {
    val nextDrained = fetch.known.get(f)
    all {
      fetch.drained < nextDrained,
      replicas' = replicas,
      writer' = writer,
      rec' = rec,
      chk' = chk,
      ledgerMap' = ledgerMap,
      closeLog' = closeLog,
      fetch' = { ...fetch, drained: nextDrained },
      net' = net,
      answers' = answers,
      faults' = faults,
      refinesAtomic' = checkAtomic({ ...currentAtomic, drained: nextDrained }),
    }
  }

  action drainStored(r: int): bool = {
    val rep = replicas.get(r)
    val nextDrained = maxOf(rep.stored) + 1
    all {
      Mutation == "drain-max-stored",
      not(faults.lost.contains(r)),
      rep.stored != Set(),
      fetch.drained < nextDrained,
      replicas' = replicas,
      writer' = writer,
      rec' = rec,
      chk' = chk,
      ledgerMap' = ledgerMap,
      closeLog' = closeLog,
      fetch' = { ...fetch, drained: nextDrained },
      net' = net,
      answers' = answers,
      faults' = faults,
      refinesAtomic' = checkAtomic({ ...currentAtomic, drained: nextDrained }),
    }
  }

  // ---------------------------------------------------------------------------
  // Asynchronous answers and client resends (SeparateAnswers)
  // ---------------------------------------------------------------------------

  action receiveAnswer(a: Answer): bool = {
    val leftAnswers = answers.exclude(Set(a))
    any {
      all {
        a.kind == "write",
        replicas' = replicas,
        writer' = applyWriterAnswer(writer, a.replica, a.pos, a.result, a.flag),
        rec' = rec,
        chk' = chk,
        ledgerMap' = ledgerMap,
        closeLog' = closeLog,
        fetch' = fetch,
        net' = net,
        answers' = leftAnswers,
        faults' = faults,
        refinesAtomic' = refinesAtomic,
      },
      all {
        a.kind == "seal",
        val eff = applySealAnswer(rec, net.copies, net.reads, a.client, a.replica, a.result, a.tail)
        all {
          replicas' = replicas,
          writer' = writer,
          rec' = eff.rec,
          chk' = chk,
          ledgerMap' = ledgerMap,
          closeLog' = closeLog,
          fetch' = fetch,
          net' = { ...net, reads: eff.reads, copies: eff.copies },
          answers' = leftAnswers,
          faults' = faults,
          refinesAtomic' = checkAtomic({ ...currentAtomic, seal: sealOf(ledgerMap.status, eff.rec) }),
        },
      },
      all {
        a.kind == "read",
        val eff = applyReadAnswer(rec, net.copies, net.reads, a.client, a.replica, a.pos, a.result, a.flag)
        all {
          replicas' = replicas,
          writer' = writer,
          rec' = eff.rec,
          chk' = chk,
          ledgerMap' = ledgerMap,
          closeLog' = closeLog,
          fetch' = fetch,
          net' = { ...net, reads: eff.reads, copies: eff.copies },
          answers' = leftAnswers,
          faults' = faults,
          refinesAtomic' = checkAtomic({ ...currentAtomic, seal: sealOf(ledgerMap.status, eff.rec) }),
        },
      },
      all {
        a.kind == "copy",
        val nextRec = applyCopyAnswer(rec, a.flag, a.client, a.replica, a.pos, a.result, a.tail == 1)
        all {
          replicas' = replicas,
          writer' = writer,
          rec' = nextRec,
          chk' = chk,
          ledgerMap' = ledgerMap,
          closeLog' = closeLog,
          fetch' = fetch,
          net' = net,
          answers' = leftAnswers,
          faults' = faults,
          refinesAtomic' = checkAtomic({ ...currentAtomic, seal: sealOf(ledgerMap.status, nextRec) }),
        },
      },
      all {
        a.kind == "check",
        canCheckReceive(chk, a.client, a.replica),
        val nextChk = applyCheckAnswer(chk, ledgerMap.status, a.client, a.replica, a.flag, a.tail)
        all {
          replicas' = replicas,
          writer' = writer,
          rec' = rec,
          chk' = nextChk,
          ledgerMap' = ledgerMap,
          closeLog' = closeLog,
          fetch' = fetch,
          net' = net,
          answers' = leftAnswers,
          faults' = faults,
          refinesAtomic' = checkAtomic({ ...currentAtomic, checks: Readers.mapBy(k => atomicCheckOf(nextChk.get(k))) }),
        },
      },
    }
  }

  action resendSeal(i: int, r: int): bool = {
    val msg: SealMsg = { rec: i, replica: r }
    all {
      SeparateAnswers,
      faults.dups < Duplicates,
      isActive(rec.get(i).phase),
      not(rec.get(i).sealedBy.contains(r)),
      not(net.seals.contains(msg)),
      replicas' = replicas,
      writer' = writer,
      rec' = rec,
      chk' = chk,
      ledgerMap' = ledgerMap,
      closeLog' = closeLog,
      fetch' = fetch,
      net' = { ...net, seals: net.seals.union(Set(msg)) },
      answers' = answers,
      faults' = { ...faults, dups: faults.dups + 1 },
      refinesAtomic' = refinesAtomic,
    }
  }

  action resendRead(i: int, r: int, q: int): bool = {
    val x = rec.get(i)
    val msg: ReadMsg = { rec: i, replica: r, pos: q, marked: x.sealedBy.contains(r) }
    all {
      SeparateAnswers,
      faults.dups < Duplicates,
      x.phase == "reading",
      q >= x.pos and q < x.issued,
      not(x.absent.get(q).contains(r)),
      positions.contains(q) implies not(x.copied.get(q).contains(r)),
      not(net.reads.contains(msg)),
      replicas' = replicas,
      writer' = writer,
      rec' = rec,
      chk' = chk,
      ledgerMap' = ledgerMap,
      closeLog' = closeLog,
      fetch' = fetch,
      net' = { ...net, reads: net.reads.union(Set(msg)) },
      answers' = answers,
      faults' = { ...faults, dups: faults.dups + 1 },
      refinesAtomic' = refinesAtomic,
    }
  }

  action resendCopy(i: int, r: int, p: int): bool = {
    val x = rec.get(i)
    val msg: CopyMsg = { fromRecoverer: true, senderId: i, replica: r, pos: p, carriedTail: copyTail(x, p) }
    all {
      SeparateAnswers,
      faults.dups < Duplicates,
      isActive(x.phase),
      x.found.contains(p),
      p < x.pos,
      x.copied.get(p).size() < Quorum,
      not(x.copied.get(p).contains(r)),
      not(net.copies.exists(m => m.fromRecoverer and m.senderId == i and m.replica == r and m.pos == p)),
      replicas' = replicas,
      writer' = writer,
      rec' = rec,
      chk' = chk,
      ledgerMap' = ledgerMap,
      closeLog' = closeLog,
      fetch' = fetch,
      net' = { ...net, copies: net.copies.union(Set(msg)) },
      answers' = answers,
      faults' = { ...faults, dups: faults.dups + 1 },
      refinesAtomic' = refinesAtomic,
    }
  }

  // ---------------------------------------------------------------------------
  // Next-state relation
  // ---------------------------------------------------------------------------

  action step = any {
    // Writer
    all {
      nondet defer = Set(true, false).oneOf()
      acquire(defer),
    },
    all {
      writer.unsent != Set(),
      nondet p = writer.unsent.oneOf()
      sendDeferred(p),
    },
    all {
      net.writes != Set(),
      nondet m = net.writes.oneOf()
      any {
        all {
          nondet keep = keepsOptions(faults.dups).oneOf()
          nondet deliver = deliversOptions.oneOf()
          deliverWrite(m, keep, deliver),
        },
        admitWrite(m),
      },
    },
    all {
      faults.dups < Duplicates,
      nondet r = Replicas.oneOf()
      nondet p = positions.oneOf()
      resendWrite(r, p),
    },
    complete,
    writeTail,
    all {
      net.tails != Set(),
      nondet m = net.tails.oneOf()
      nondet keep = keepsOptions(faults.dups).oneOf()
      deliverTail(m, keep),
    },
    // Recovery
    all {
      nondet i = Recoverers.oneOf()
      any {
        all {
          nondet t = rangeSet(0, writer.completed).oneOf()
          beginRecovery(i, t),
        },
        closeLedger(i),
        retryReads(i),
        learnEnd(i),
        all {
          SeparateAnswers,
          faults.dups < Duplicates,
          nondet r = Replicas.oneOf()
          any {
            resendSeal(i, r),
            all {
              nondet q = readSlots.oneOf()
              resendRead(i, r, q),
            },
            all {
              nondet p = positions.oneOf()
              resendCopy(i, r, p),
            },
          },
        },
      },
    },
    all {
      net.seals != Set(),
      nondet m = net.seals.oneOf()
      nondet keep = keepsOptions(faults.dups).oneOf()
      nondet deliver = deliversOptions.oneOf()
      deliverSeal(m, keep, deliver),
    },
    all {
      net.reads != Set(),
      nondet m = net.reads.oneOf()
      any {
        failRead(m),
        all {
          nondet keep = keepsOptions(faults.dups).oneOf()
          nondet deliver = deliversOptions.oneOf()
          deliverRead(m, keep, deliver),
        },
      },
    },
    all {
      net.copies != Set(),
      nondet m = net.copies.oneOf()
      nondet keep = keepsOptions(faults.dups).oneOf()
      nondet deliver = deliversOptions.oneOf()
      deliverCopy(m, keep, deliver),
    },
    all {
      answers != Set(),
      nondet a = answers.oneOf()
      receiveAnswer(a),
    },
    // Drops, restarts, WAL flush, and faults
    dropLedger,
    all {
      nondet r = Replicas.oneOf()
      any {
        dropReplica(r),
        decideCreate(r),
        restart(r),
        flushReplica(r),
        loseNode(r),
        loseReplica(r),
        loseTombstone(r),
        all {
          Faults.contains("corrupt"),
          nondet p = positions.oneOf()
          corruptSlot(r, p),
        },
      },
    },
    // Readers
    all {
      Readers != Set(),
      nondet c = Readers.oneOf()
      any {
        all {
          nondet t = rangeSet(0, writer.completed).oneOf()
          beginCheck(c, t),
        },
        all {
          nondet r = Replicas.oneOf()
          nondet deliver = deliversOptions.oneOf()
          checkAnswer(c, r, deliver),
        },
      },
    },
    // Fetchers
    all {
      Fetchers != Set(),
      nondet f = Fetchers.oneOf()
      any {
        readMap(f),
        drain(f),
        all {
          nondet r = Replicas.oneOf()
          any {
            learnTail(f, r),
            drainStored(r),
            all {
              nondet p = positions.oneOf()
              nondet q = Replicas.oneOf()
              readBelowTail(f, p, r, q),
            },
          },
        },
      },
    },
  }

  // ---------------------------------------------------------------------------
  // Safety invariants (LP-I1..LP-I12, LR-I2..LR-I8, LG-I8, LS-I2, LSM-I2)
  // ---------------------------------------------------------------------------

  def holders(p: int): Set[int] =
    Replicas.filter(r => replicas.get(r).stored.contains(p))

  val TypeOK: bool = and {
    Replicas.forall(r =>
      val rep = replicas.get(r)
      rep.stored.subseteq(positions) and
      rep.corrupt.subseteq(positions) and
      rep.retained >= 0 and rep.retained <= Slots + 1 and
      Set("none", "held", "dropped").contains(rep.stray) and
      rep.volData.subseteq(positions) and
      rep.volSeal.subseteq(Recoverers) and
      rep.ansData.subseteq(positions)
    ),
    Set("open", "closed", "dropped").contains(ledgerMap.status),
    writer.nextPos >= 0 and writer.nextPos <= Slots,
    writer.completed >= 0 and writer.completed <= writer.nextPos,
    writer.nextPos - writer.completed <= Window,
    faults.lost.subseteq(faults.faulty),
    faults.amnesiac.subseteq(faults.faulty),
    faults.faulty.size() <= Quorum - 1 or faults.faulty == Set(),
    faults.dups >= 0 and faults.dups <= Duplicates,
    Fetchers.forall(f => fetch.known.get(f) >= 0 and fetch.known.get(f) <= Slots + 1),
    fetch.served.forall(entry => positions.contains(entry._1)),
    fetch.drained >= 0 and fetch.drained <= Slots,
  }

  // No acknowledged position is lost by recovery and close.
  val AcknowledgedSurvive: bool =
    mapEnded(ledgerMap) implies (closeLog.ackedAtClose <= ledgerMap.end)

  // A fenced writer acknowledges nothing at or above T, including after drops (LP-I8, LR-I7).
  val NoAcknowledgementAtOrAboveEnd: bool =
    mapEnded(ledgerMap) implies (writer.completed <= ledgerMap.end)

  // Seal finality across the drop: once ended, fewer than Q non-lost replicas can
  // accept a writer write (LR-I7, LR-I8).
  val EndSealed: bool =
    mapEnded(ledgerMap) implies (
      Replicas.exclude(faults.lost).filter(r =>
        val rep = replicas.get(r)
        not(answersDropped(rep)) and not(rep.exists and rep.sealed)
      ).size() < Quorum
    )

  // A node holds a ledger's replica or its tombstone, never both (LS-I2).
  val TombstoneExcludesReplica: bool =
    Replicas.forall(r =>
      val rep = replicas.get(r)
      not(rep.exists and rep.tomb)
    )

  // A crash never revokes an answer (conventions §13, LR-I4).
  val NoRevokedAnswer: bool =
    Replicas.exclude(faults.amnesiac).forall(r =>
      val rep = replicas.get(r)
      rep.ansData.subseteq(rep.stored) and
      (rep.ansSealed implies rep.sealed)
    )

  // Every position below T on a closed ledger is held by at least Q replicas.
  val ReadersAgree: bool =
    mapClosed(ledgerMap) implies rangeSet(0, ledgerMap.end - 1).forall(p => holders(p).size() >= Quorum)

  // Within the fault budget, every position below T has an intact reachable copy.
  val ReadableBelowEnd: bool =
    mapClosed(ledgerMap) implies rangeSet(0, ledgerMap.end - 1).forall(p =>
      Replicas.exclude(faults.lost).exists(r =>
        val rep = replicas.get(r)
        rep.stored.contains(p) and not(rep.corrupt.contains(p))
      )
    )

  // Concurrent recoveries converge on one recorded end T.
  val RecoveriesConverge: bool = closeLog.closes.size() <= 1

  // Once a recovery decided its end or the ledger ended, every Q non-lost
  // replicas include a sealed or dropped one (LP-I5, LP-I9).
  val OpenCheckSound: bool =
    (mapEnded(ledgerMap) or Recoverers.exists(i => isFinished(rec.get(i).phase))) implies
    Replicas.exclude(faults.lost).powerset().filter(rs => rs.size() == Quorum).forall(rs =>
      rs.exists(r =>
        val rep = replicas.get(r)
        (rep.exists and rep.sealed) or answersDropped(rep)
      )
    )

  // A tail check begun after a recovery decided its end or after close reports sealed (LP-I9).
  val ReadersSeeClose: bool =
    Readers.forall(c =>
      val ch = chk.get(c)
      (ch.phase == "done" and ch.late) implies ch.sealed
    )

  // No check reports a tail past T, meeting the Delos erratum's tailpos1 <= tailpos2 (LR-I6, LP-I9).
  val CheckTailBelowEnd: bool =
    mapEnded(ledgerMap) implies Readers.forall(c =>
      val ch = chk.get(c)
      (ch.phase == "done") implies (ch.reportedTail <= ledgerMap.end)
    )

  def decided(p: int): bool =
    if (mapEnded(ledgerMap)) p < ledgerMap.end
    else rangeSet(0, p).forall(q => writer.written.get(q).size() >= Quorum)

  // Every position a fetcher read or drained below a learned tail is decided (LR-I6, LP-I10, LG-I8, LSM-I2).
  val BelowTailDecided: bool =
    fetch.served.map(entry => entry._1)
      .union(positions.filter(q => q < fetch.drained))
      .forall(p => decided(p))

  // Every position a recovery finds lies below start + 2K (LP-I7).
  val RecoveryBounded: bool =
    Recoverers.forall(i =>
      val x = rec.get(i)
      x.found.forall(p => p < x.start + 2 * Window)
    )

  // Every retained contiguous tail is at most the recorded end T (LR-I6, LP-I11).
  val RetainedBelowEnd: bool =
    mapEnded(ledgerMap) implies Replicas.forall(r => replicas.get(r).retained <= ledgerMap.end)

  // Step-by-step refinement of AtomicLedger.qnt.
  val RefinesAtomicLedger: bool = refinesAtomic

  // ---------------------------------------------------------------------------
  // Witnesses (negated reachability: expected to be violated)
  // ---------------------------------------------------------------------------

  val NeverDivergentEnds: bool =
    not(Recoverers.exists(i => Recoverers.exists(j =>
      rec.get(i).end >= 0 and rec.get(j).end >= 0 and rec.get(i).end != rec.get(j).end
    )))

  val NeverAcknowledgementAfterSealedAnswer: bool = not(writer.ackAfterSealed)

  val NeverStrayAfterDrop: bool =
    Replicas.forall(r => replicas.get(r).stray == "none")

  val NeverStrayDroppedAgain: bool =
    Replicas.forall(r => replicas.get(r).stray != "dropped")

  val NeverCheckAfterDrop: bool =
    not(Readers.exists(c => chk.get(c).phase == "done") and
        Replicas.exists(r => replicas.get(r).tomb))

  val NeverOpenCheckAtEnd: bool =
    not(mapEnded(ledgerMap) and ledgerMap.end > 0 and Readers.exists(c =>
      val ch = chk.get(c)
      ch.phase == "done" and not(ch.sealed) and ch.open and ch.reportedTail == ledgerMap.end
    ))

  val NeverCheckTailAboveCandidate: bool =
    not(Readers.exists(c => Recoverers.exists(i =>
      chk.get(c).phase == "done" and rec.get(i).end >= 0 and
      chk.get(c).reportedTail > rec.get(i).end
    )))

  val NeverDataAtOrAboveEnd: bool =
    not(mapClosed(ledgerMap) and Replicas.exists(r =>
      replicas.get(r).stored.exists(p => p >= ledgerMap.end)
    ))

  val NeverFoundAtBound: bool =
    not(Recoverers.exists(i =>
      val x = rec.get(i)
      x.found.exists(p => p == x.start + 2 * Window - 1)
    ))

  val NeverCopyPendingAtEnd: bool =
    not(Recoverers.exists(i =>
      rec.get(i).phase == "closing" and not(copiesDone(rec, i))
    ))

  val NeverClosedAfterNodeLoss: bool =
    not(mapClosed(ledgerMap) and faults.lost != Set() and closeLog.ackedAtClose > 0)

  val NeverRepairedCorruption: bool =
    not(Recoverers.exists(i => rec.get(i).phase == "closed" and rec.get(i).repaired))

  val NeverRepeatedWritten: bool = not(writer.repeated)

  val NeverFresherRepeat: bool = not(writer.freshRepeat)

  val NeverTakenWhileOpen: bool =
    not(ledgerMap.status == "open" and (fetch.served != Set() or fetch.drained > 0))

  val NeverRepairLanded: bool = not(fetch.repairs)

  val NeverServedOpenThenClosed: bool =
    not(mapEnded(ledgerMap) and fetch.served.exists(entry => entry._2 and entry._1 < ledgerMap.end))

  val NeverEndsAtExactTail: bool =
    not(Recoverers.exists(i =>
      val x = rec.get(i)
      x.phase == "closed" and x.found == Set() and x.end == x.start and x.start > 0
    ))

  val NeverClosedAboveAcked: bool =
    not(mapEnded(ledgerMap) and ledgerMap.end > closeLog.ackedAtClose)

  val NeverAcknowledgedAfterClose: bool =
    not(mapEnded(ledgerMap) and writer.completed > closeLog.ackedAtClose)

  val NeverCoverageWithAnswerInFlight: bool =
    not(Recoverers.exists(i =>
      (rec.get(i).phase == "reading" or rec.get(i).phase == "closing") and
      answers.exists(a => a.kind == "seal" and a.client == i and a.result == "ok")
    ))

  val NeverDeferredSend: bool =
    not(writer.unsent.exists(p => net.writes.exists(m => m.pos > p)))

  val NeverMarkedReadWhileUnmarkedInFlight: bool =
    not(net.reads.exists(m =>
      not(m.marked) and
      net.reads.contains({ rec: m.rec, replica: m.replica, pos: m.pos, marked: true })
    ))

  val NeverSealTailFromAdmittedWrite: bool =
    not(Recoverers.exists(i => net.writes.exists(m =>
      rec.get(i).sealedBy.contains(m.replica) and
      rec.get(i).start == m.carriedTail and
      m.carriedTail > rec.get(i).known and
      Replicas.forall(q => not(replicas.get(q).stored.contains(m.pos)))
    )))

  val NeverCorruptUnwritten: bool =
    not(Replicas.exists(r =>
      val rep = replicas.get(r)
      rep.corrupt.exclude(rep.stored) != Set()
    ))

  val NeverClosedWithFewCopiesAboveEnd: bool =
    not(Recoverers.exists(i =>
      rec.get(i).phase == "closed" and
      rec.get(i).found.exists(p => p >= rec.get(i).end and rec.get(i).copied.get(p).size() < Quorum)
    ))
}
