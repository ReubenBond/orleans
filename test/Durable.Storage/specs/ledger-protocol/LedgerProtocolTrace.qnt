// -*- mode: Bluespec; -*-
// Trace validation of LedgerProtocol (test/Durable.Storage/docs/spec/verification.md §5.1): Quint's
// TypeScript runner replays each simulation's LedgerTrace as a sequence of
// LedgerProtocol actions (with SeparateAnswers and TailWrites), advancing any
// silent steps between logged events and asserting TraceInvariants after every
// step. Each generated LedgerTraceData module imports this module and runs
// `traceReplay`.
module LedgerProtocolTrace {
  const TraceReplicas: Set[int]
  const TraceQuorum: int
  const TraceWindow: int
  const TraceSlots: int
  const TraceRecoverers: Set[int]
  const TraceReaders: Set[int]
  const TraceFetchers: Set[int]
  const TraceDuplicates: int
  const TraceFaults: Set[str]

  import LedgerProtocol(
    Replicas = TraceReplicas,
    Quorum = TraceQuorum,
    Window = TraceWindow,
    Slots = TraceSlots,
    Recoverers = TraceRecoverers,
    Readers = TraceReaders,
    Fetchers = TraceFetchers,
    Faults = TraceFaults,
    Duplicates = TraceDuplicates,
    Mutation = "none",
    SeparateAnswers = true,
    TailWrites = true,
    Live = Set()
  ).* from "./LedgerProtocol"

  // Extra trace-validation state.
  // rcv: (checkId, replica) check answers that arrived before their serve.
  // alias: maps a learned-T writer write (r, p, t) to the model's write (r, p, knownTail).
  // dmg: nodes named by a damage event.
  // chkDoneCompleted: writer.completed at the step a check decided (phase became "done"),
  //   bounding the known contiguous tail a writer's ReadTailAsync could return before TailReadAnswered logs.
  var rcv: Set[(int, int)]
  var alias: (int, int, int) -> (int, int, int)
  var dmg: Set[int]
  var chkDoneCompleted: int -> int

  pure val DroppedAnswer: str = "Dropped"
  pure val CorruptAnswer: str = "LedgerReplicaCorruptException"

  // All safety invariants checked after every trace step and every silent step.
  val TraceInvariants: bool = and {
    TypeOK,
    AcknowledgedSurvive,
    NoAcknowledgementAtOrAboveEnd,
    EndSealed,
    TombstoneExcludesReplica,
    NoRevokedAnswer,
    ReadersAgree,
    ReadableBelowEnd,
    RecoveriesConverge,
    OpenCheckSound,
    ReadersSeeClose,
    CheckTailBelowEnd,
    BelowTailDecided,
    RecoveryBounded,
    RetainedBelowEnd,
    RefinesAtomicLedger,
  }

  action unchangedVars: bool = all {
    replicas' = replicas,
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

  action traceUnchanged: bool = all {
    rcv' = rcv,
    alias' = alias,
    dmg' = dmg,
    chkDoneCompleted' = chkDoneCompleted,
  }

  action traceStutter: bool = all {
    unchangedVars,
    traceUnchanged,
  }

  action traceInit: bool = all {
    init,
    rcv' = Set(),
    alias' = Map(),
    dmg' = Set(),
    chkDoneCompleted' = Map(),
  }

  // ---------------------------------------------------------------------------
  // Helpers
  // ---------------------------------------------------------------------------

  def writerKnown(f: int): int =
    if (f >= 0 and TraceFetchers.contains(f)) maxInt(writer.completed, fetch.known.get(f))
    else writer.completed

  def writeMsg(r: int, p: int, t: int): WriteMsg = {
    val k = (r, p, t)
    val target = if (alias.keys().contains(k)) alias.get(k) else k
    { replica: target._1, pos: target._2, carriedTail: target._3 }
  }

  def writeServed(r: int, p: int): str = {
    val rep = replicas.get(r)
    if (answersDropped(rep)) DroppedAnswer
    else if (rep.exists and rep.sealed) "Sealed"
    else if (rep.corrupt.contains(p)) CorruptAnswer
    else "Written"
  }

  def readServed(r: int, p: int): str = {
    val rep = replicas.get(r)
    if (answersDropped(rep)) DroppedAnswer
    else if (rep.corrupt.contains(p)) CorruptAnswer
    else if (rep.stored.contains(p)) "Batch"
    else "Missing"
  }

  def sealServedMatches(c: int, r: int, ans: str, repTail: int, isSealed: bool): bool = {
    val rep = replicas.get(r)
    if (answersDropped(rep)) ans == DroppedAnswer
    else and {
      ans == "Tail",
      repTail == maxInt(if (rep.exists) rep.retained else 0, rec.get(c).known),
      isSealed == true,
    }
  }

  def checkServedMatches(c: int, r: int, ans: str, repTail: int, isSealed: bool): bool = {
    val rep = replicas.get(r)
    if (answersDropped(rep)) ans == DroppedAnswer
    else and {
      ans == "Tail",
      repTail == answerTail(chk.get(c), rep),
      isSealed == (rep.exists and rep.sealed),
    }
  }

  pure def resultOf(ans: str): str =
    if (ans == DroppedAnswer) "dropped"
    else if (ans == CorruptAnswer) "corrupt"
    else if (ans == "Tail") "ok"
    else if (ans == "Written") "written"
    else if (ans == "Sealed") "sealed"
    else if (ans == "Batch") "batch"
    else if (ans == "Missing") "missing"
    else ans

  def writeMatches(a: Answer, r: int, p: int, ans: str): bool = and {
    a.kind == "write",
    a.replica == r,
    a.pos == p,
    a.result == resultOf(ans),
  }

  def sealMatches(a: Answer, c: int, r: int, ans: str, repTail: int): bool = and {
    a.kind == "seal",
    a.client == c,
    a.replica == r,
    a.result == resultOf(ans),
    a.tail == repTail,
  }

  def readMatches(a: Answer, c: int, r: int, p: int, marked: bool, ans: str): bool = and {
    a.kind == "read",
    a.client == c,
    a.replica == r,
    a.pos == p,
    a.result == resultOf(ans),
    (a.result == "missing") implies (a.flag == marked),
  }

  def copyMatches(a: Answer, fromFetcher: bool, c: int, r: int, p: int, ans: str): bool = and {
    a.kind == "copy",
    a.flag == not(fromFetcher),
    a.client == c,
    a.replica == r,
    a.pos == p,
    a.result == resultOf(ans),
  }

  def recoveredStateMatches(r: int, held: bool, isSealed: bool, data: Set[int], faulty: Set[int]): bool = {
    val rep = replicas.get(r)
    and {
      rep.exists == held,
      held implies and {
        rep.sealed == isSealed,
        rep.stored.exclude(rep.corrupt).subseteq(data),
        rep.stored.subseteq(data.union(faulty)),
        data.subseteq(rep.stored),
      },
    }
  }

  // Deterministic selectors (no nondet, so quint test executes one exact replay).
  pure def pickBestWrite(msgs: Set[WriteMsg]): WriteMsg =
    msgs.fold({ replica: -1, pos: -1, carriedTail: -1 }, (best, m) =>
      if (best.replica < 0 or m.carriedTail > best.carriedTail or (m.carriedTail == best.carriedTail and m.pos < best.pos))
        m
      else best
    )

  pure def pickMinWrite(msgs: Set[WriteMsg]): WriteMsg =
    msgs.fold({ replica: -1, pos: -1, carriedTail: -1 }, (best, m) =>
      if (best.replica < 0 or m.carriedTail < best.carriedTail or (m.carriedTail == best.carriedTail and m.pos < best.pos))
        m
      else best
    )

  pure def pickBestCheck(cands: Set[(int, int)]): (int, int) =
    cands.fold((-1, -1), (best, x) =>
      if (best._1 < 0 or x._2 > best._2 or (x._2 == best._2 and x._1 < best._1)) x else best
    )

  pure def pickAnswer(cands: Set[Answer]): Answer =
    cands.fold({ kind: "", client: -1, replica: -1, pos: -1, result: "", flag: false, tail: -1 }, (best, a) =>
      if (best.kind == "" or a.tail < best.tail or (a.tail == best.tail and not(a.flag) and best.flag))
        a
      else best
    )

  pure def pickInt(s: Set[int]): int =
    s.fold(-1, (best, x) => if (best < 0 or x < best) x else best)

  // ---------------------------------------------------------------------------
  // Deterministic silent transitions (matching LedgerProtocolTrace's Silent)
  // ---------------------------------------------------------------------------

  action ensureAcquire(p: int): bool =
    if (p <= writer.nextPos) traceStutter
    else TraceSlots.reps(_ => (if (p > writer.nextPos) all { acquire(true), traceUnchanged } else traceStutter).expect(TraceInvariants))

  action ensureCompleted(needed: int): bool =
    if (writer.completed >= needed) traceStutter
    else TraceSlots.reps(_ => (if (writer.completed < needed) all { complete, traceUnchanged } else traceStutter).expect(TraceInvariants))

  action silentAdmitForCorrupt(r: int): bool = {
    val rep = replicas.get(r)
    val cands = net.writes.filter(m => m.replica == r)
    if (dmg.contains(r) and not(rep.exists) and cands != Set())
      all { admitWrite(pickMinWrite(cands)), traceUnchanged }
    else traceStutter
  }

  action silentCorruptOne(r: int, p: int): bool = {
    val rep = replicas.get(r)
    if (dmg.contains(r) and not(rep.corrupt.contains(p)))
      all { corruptSlot(r, p), traceUnchanged }
    else traceStutter
  }

  action ensureCorrupt(r: int, p: int): bool =
    silentAdmitForCorrupt(r).expect(TraceInvariants)
      .then(silentCorruptOne(r, p).expect(TraceInvariants))

  action silentAdmitForRetained(r: int, baseTail: int, targetTail: int): bool = {
    val rep = replicas.get(r)
    val curTail = maxInt(if (rep.exists) rep.retained else 0, baseTail)
    val cands = net.writes.filter(m => m.replica == r and m.carriedTail <= targetTail and (not(rep.exists) or rep.retained < m.carriedTail))
    if (curTail < targetTail and cands != Set())
      all { admitWrite(pickBestWrite(cands)), traceUnchanged }
    else traceStutter
  }

  action silentCheckForRetained(r: int, baseTail: int, targetTail: int): bool = {
    val rep = replicas.get(r)
    val curTail = maxInt(if (rep.exists) rep.retained else 0, baseTail)
    val cands = TraceReaders
      .filter(c => chk.get(c).phase == "reading" and not(chk.get(c).asked.contains(r)) and rep.exists and rep.retained < chk.get(c).carry and chk.get(c).carry <= targetTail)
      .map(c => (c, chk.get(c).carry))
    if (curTail < targetTail and cands != Set())
      all { checkAnswer(pickBestCheck(cands)._1, r, false), traceUnchanged }
    else traceStutter
  }

  action ensureRetained(r: int, baseTail: int, targetTail: int): bool =
    silentAdmitForRetained(r, baseTail, targetTail).expect(TraceInvariants)
      .then(silentCheckForRetained(r, baseTail, targetTail).expect(TraceInvariants))

  action silentLandWrite(r: int, entry: (int, int, bool)): bool = {
    val m = writeMsg(r, entry._1, entry._2)
    if (not(replicas.get(r).stored.contains(entry._1)) and net.writes.contains(m))
      all { deliverWrite(m, entry._3, false), traceUnchanged }
    else traceStutter
  }

  action silentLandCopy(r: int, entry: (bool, int, int, int, bool)): bool = {
    val m: CopyMsg = { fromRecoverer: not(entry._1), senderId: entry._2, replica: r, pos: entry._3, carriedTail: entry._4 }
    if (not(replicas.get(r).stored.contains(entry._3)) and net.copies.contains(m))
      all { deliverCopy(m, entry._5, false), traceUnchanged }
    else traceStutter
  }

  action silentLandSeal(r: int, entry: (int, bool)): bool = {
    val rep = replicas.get(r)
    val m: SealMsg = { rec: entry._1, replica: r }
    if (not(rep.exists and rep.sealed) and net.seals.contains(m))
      all { deliverSeal(m, entry._2, false), traceUnchanged }
    else traceStutter
  }

  action ensureLandings(
    r: int,
    landed: List[(int, int, bool)],
    copied: List[(bool, int, int, int, bool)],
    sealedBy: List[(int, bool)]
  ): bool =
    if (landed.length() == 0 and copied.length() == 0 and sealedBy.length() == 0) traceStutter
    else
      (if (landed.length() == 0) traceStutter
       else landed.length().reps(i => silentLandWrite(r, landed[i]).expect(TraceInvariants)))
        .then(if (copied.length() == 0) traceStutter
              else copied.length().reps(i => silentLandCopy(r, copied[i]).expect(TraceInvariants)))
        .then(if (sealedBy.length() == 0) traceStutter
              else sealedBy.length().reps(i => silentLandSeal(r, sealedBy[i]).expect(TraceInvariants)))

  action silentAdmitForRestart(r: int, held: bool): bool = {
    val rep = replicas.get(r)
    val cands = net.writes.filter(m => m.replica == r)
    if (held and not(rep.exists) and cands != Set())
      all { admitWrite(pickMinWrite(cands)), traceUnchanged }
    else traceStutter
  }

  action silentCorruptRestartOne(r: int, p: int): bool = {
    val rep = replicas.get(r)
    if (dmg.contains(r) and rep.stored.contains(p) and not(rep.corrupt.contains(p)))
      all { corruptSlot(r, p), traceUnchanged }
    else traceStutter
  }

  action silentReceiveCheck(c: int, r: int): bool = {
    val cands = answers.filter(a => a.kind == "check" and a.client == c and a.replica == r)
    if (rcv.contains((c, r)) and canCheckReceive(chk, c, r) and cands != Set()) {
      val a = pickAnswer(cands)
      val nextChk = applyCheckAnswer(chk, ledgerMap.status, a.client, a.replica, a.flag, a.tail)
      all {
        receiveAnswer(a),
        rcv' = rcv.exclude(Set((c, r))),
        alias' = alias,
        dmg' = dmg,
        chkDoneCompleted' =
          if (nextChk.get(c).phase == "done") chkDoneCompleted.put(c, writer.completed)
          else chkDoneCompleted,
      }
    } else traceStutter
  }

  // ---------------------------------------------------------------------------
  // Trace step actions (one per event kind / op)
  // ---------------------------------------------------------------------------

  action stepAcquire(p: int, t: int): bool =
    ensureAcquire(p)
      .then(ensureCompleted(t))
      .then(all {
        if (p < writer.nextPos)
          if (writer.unsent.contains(p)) all { knownTail(writer) == t, sendDeferred(p) }
          else unchangedVars
        else all {
          p == writer.nextPos,
          knownTail(writer) == t,
          acquire(false),
        },
        traceUnchanged,
      }.expect(TraceInvariants))

  action stepCallWrite(r: int, p: int, t: int, learned: bool): bool =
    (if (not(learned)) ensureCompleted(t) else traceStutter)
      .then(
        (if (learned) {
          val m: WriteMsg = { replica: r, pos: p, carriedTail: knownTail(writer) }
          all {
            if (net.writes.contains(m)) unchangedVars else resendWrite(r, p),
            rcv' = rcv,
            alias' = alias.put((r, p, t), (r, p, knownTail(writer))),
            dmg' = dmg,
            chkDoneCompleted' = chkDoneCompleted,
          }
        } else {
          val m: WriteMsg = { replica: r, pos: p, carriedTail: t }
          all {
            if (net.writes.contains(m)) unchangedVars
            else all { knownTail(writer) == t, resendWrite(r, p) },
            traceUnchanged,
          }
        }).expect(TraceInvariants)
      )

  action stepCallSeal(c: int, r: int): bool = {
    val m: SealMsg = { rec: c, replica: r }
    all {
      if (net.seals.contains(m)) unchangedVars else resendSeal(c, r),
      traceUnchanged,
    }
  }.expect(TraceInvariants)

  action stepCallRead(c: int, r: int, p: int, marked: bool): bool = {
    val m: ReadMsg = { rec: c, replica: r, pos: p, marked: marked }
    all {
      if (net.reads.contains(m)) unchangedVars
      else all { rec.get(c).sealedBy.contains(r) == marked, resendRead(c, r, p) },
      traceUnchanged,
    }
  }.expect(TraceInvariants)

  action stepCallCopy(fromFetcher: bool, c: int, r: int, p: int, t: int): bool = {
    val m: CopyMsg = { fromRecoverer: not(fromFetcher), senderId: c, replica: r, pos: p, carriedTail: t }
    all {
      if (fromFetcher) all { net.copies.contains(m), unchangedVars }
      else if (net.copies.contains(m)) unchangedVars
      else all { copyTail(rec.get(c), p) == t, resendCopy(c, r, p) },
      traceUnchanged,
    }
  }.expect(TraceInvariants)

  action stepServeWrite(
    r: int, p: int, t: int, ans: str, keep: bool, deliver: bool,
    landed: List[(int, int, bool)],
    copied: List[(bool, int, int, int, bool)],
    sealedBy: List[(int, bool)]
  ): bool =
    ensureLandings(r, landed, copied, sealedBy)
      .then(if (ans == CorruptAnswer) ensureCorrupt(r, p) else traceStutter)
      .then({
        val m = writeMsg(r, p, t)
        all {
          net.writes.contains(m),
          writeServed(r, p) == ans,
          deliverWrite(m, keep, deliver),
          traceUnchanged,
        }
      }.expect(TraceInvariants))

  action stepServeSeal(
    c: int, r: int, ans: str, repTail: int, isSealed: bool, keep: bool, deliver: bool,
    landed: List[(int, int, bool)],
    copied: List[(bool, int, int, int, bool)],
    sealedBy: List[(int, bool)]
  ): bool =
    ensureLandings(r, landed, copied, sealedBy)
      .then(if (ans == "Tail") ensureRetained(r, rec.get(c).known, repTail) else traceStutter)
      .then({
        val m: SealMsg = { rec: c, replica: r }
        all {
          net.seals.contains(m),
          sealServedMatches(c, r, ans, repTail, isSealed),
          deliverSeal(m, keep, deliver),
          traceUnchanged,
        }
      }.expect(TraceInvariants))

  action stepServeRead(
    c: int, r: int, p: int, marked: bool, ans: str, keep: bool, deliver: bool,
    landed: List[(int, int, bool)],
    copied: List[(bool, int, int, int, bool)],
    sealedBy: List[(int, bool)]
  ): bool =
    ensureLandings(r, landed, copied, sealedBy)
      .then(if (ans == CorruptAnswer) ensureCorrupt(r, p) else traceStutter)
      .then({
        val m: ReadMsg = { rec: c, replica: r, pos: p, marked: marked }
        all {
          net.reads.contains(m),
          readServed(r, p) == ans,
          deliverRead(m, keep, deliver),
          traceUnchanged,
        }
      }.expect(TraceInvariants))

  action stepServeCopy(
    fromFetcher: bool, c: int, r: int, p: int, t: int, ans: str, keep: bool, deliver: bool,
    landed: List[(int, int, bool)],
    copied: List[(bool, int, int, int, bool)],
    sealedBy: List[(int, bool)]
  ): bool =
    ensureLandings(r, landed, copied, sealedBy)
      .then({
        val m: CopyMsg = { fromRecoverer: not(fromFetcher), senderId: c, replica: r, pos: p, carriedTail: t }
        all {
          net.copies.contains(m),
          (if (answersDropped(replicas.get(r))) DroppedAnswer else "Written") == ans,
          deliverCopy(m, keep, deliver),
          traceUnchanged,
        }
      }.expect(TraceInvariants))

  action stepServeCheck(
    c: int, r: int, ans: str, repTail: int, isSealed: bool, deliver: bool,
    landed: List[(int, int, bool)],
    copied: List[(bool, int, int, int, bool)],
    sealedBy: List[(int, bool)]
  ): bool =
    ensureLandings(r, landed, copied, sealedBy)
      .then(if (ans == "Tail") ensureRetained(r, chk.get(c).carry, repTail) else traceStutter)
      .then(all {
        checkServedMatches(c, r, ans, repTail, isSealed),
        checkAnswer(c, r, deliver),
        traceUnchanged,
      }.expect(TraceInvariants))
      .then(silentReceiveCheck(c, r).expect(TraceInvariants))

  action stepServePeek(
    r: int, p: int, t: int, ans: str, repair: int,
    landed: List[(int, int, bool)],
    copied: List[(bool, int, int, int, bool)],
    sealedBy: List[(int, bool)]
  ): bool =
    ensureLandings(r, landed, copied, sealedBy)
      .then(if (ans == CorruptAnswer) ensureCorrupt(r, p) else traceStutter)
      .then(all {
        readServed(r, p) == ans,
        not(replicas.get(r).exists) or t <= replicas.get(r).retained,
        if (repair >= 0) {
          val sources = TraceReplicas.filter(src => {
            val srep = replicas.get(src)
            not(faults.lost.contains(src)) and
            srep.stored.exclude(srep.corrupt.union(srep.volData)).contains(p)
          })
          all {
            sources != Set(),
            readBelowTail(repair, p, pickInt(sources), r),
          }
        } else unchangedVars,
        traceUnchanged,
      }.expect(TraceInvariants))

  action stepServeTail(
    r: int, t: int, keep: bool,
    landed: List[(int, int, bool)],
    copied: List[(bool, int, int, int, bool)],
    sealedBy: List[(int, bool)]
  ): bool =
    ensureLandings(r, landed, copied, sealedBy)
      .then({
        val m: TailMsg = { replica: r, carriedTail: t }
        all {
          net.tails.contains(m),
          deliverTail(m, keep),
          traceUnchanged,
        }
      }.expect(TraceInvariants))

  action stepServeLearn(
    c: int, r: int, t: int, ans: str, repTail: int, isSealed: bool,
    landed: List[(int, int, bool)],
    copied: List[(bool, int, int, int, bool)],
    sealedBy: List[(int, bool)]
  ): bool =
    ensureLandings(r, landed, copied, sealedBy)
      .then(if (ans == "Tail") ensureRetained(r, t, repTail) else traceStutter)
      .then({
        val rep = replicas.get(r)
        all {
          t == fetch.known.get(c),
          if (answersDropped(rep)) all { ans == DroppedAnswer, unchangedVars }
          else all {
            ans == "Tail",
            repTail == (if (rep.exists) maxInt(rep.retained, t) else t),
            isSealed == (rep.exists and rep.sealed),
            if (canLearnTail(c, r)) learnTail(c, r) else unchangedVars,
          },
          traceUnchanged,
        }
      }.expect(TraceInvariants))

  action stepReceiveWrite(r: int, p: int, ans: str): bool = {
    val cands = answers.filter(a => writeMatches(a, r, p, ans))
    all {
      cands != Set(),
      receiveAnswer(pickAnswer(cands)),
      traceUnchanged,
    }
  }.expect(TraceInvariants)

  action stepReceiveSeal(c: int, r: int, ans: str, repTail: int): bool = {
    val cands = answers.filter(a => sealMatches(a, c, r, ans, repTail))
    all {
      cands != Set(),
      receiveAnswer(pickAnswer(cands)),
      traceUnchanged,
    }
  }.expect(TraceInvariants)

  action stepReceiveRead(c: int, r: int, p: int, marked: bool, ans: str): bool = {
    val cands = answers.filter(a => readMatches(a, c, r, p, marked, ans))
    all {
      cands != Set(),
      receiveAnswer(pickAnswer(cands)),
      traceUnchanged,
    }
  }.expect(TraceInvariants)

  action stepReceiveCopy(fromFetcher: bool, c: int, r: int, p: int, ans: str): bool = {
    val cands = answers.filter(a => copyMatches(a, fromFetcher, c, r, p, ans))
    all {
      cands != Set(),
      receiveAnswer(pickAnswer(cands)),
      traceUnchanged,
    }
  }.expect(TraceInvariants)

  action stepArrive(c: int, r: int): bool = all {
    unchangedVars,
    rcv' = rcv.union(Set((c, r))),
    alias' = alias,
    dmg' = dmg,
    chkDoneCompleted' = chkDoneCompleted,
  }.expect(TraceInvariants)
    .then(silentReceiveCheck(c, r).expect(TraceInvariants))

  action stepAppended(p: int, ans: str): bool = all {
    (ans == "Written") implies (writer.written.get(p).size() >= TraceQuorum),
    unchangedVars,
    traceUnchanged,
  }.expect(TraceInvariants)

  action stepCompleted(p: int): bool =
    ensureCompleted(p + 1)
      .then(all {
        writer.completed > p,
        unchangedVars,
        traceUnchanged,
      }.expect(TraceInvariants))

  action stepStopped: bool = traceStutter.expect(TraceInvariants)

  action stepRecover(c: int, t: int): bool =
    ensureCompleted(t)
      .then(all {
        t <= writer.completed,
        beginRecovery(c, t),
        traceUnchanged,
      }.expect(TraceInvariants))

  action stepRecovered(c: int, start: int, end: int): bool = all {
    rec.get(c).phase == "closing",
    copiesDone(rec, c),
    (start >= 0) implies (rec.get(c).start == start),
    rec.get(c).end == end,
    unchangedVars,
    traceUnchanged,
  }.expect(TraceInvariants)

  action stepClose(c: int, end: int, acked: int): bool =
    ensureCompleted(acked)
      .then(all {
        rec.get(c).end == end,
        ledgerMap.version == rec.get(c).version,
        writer.completed >= acked,
        closeLedger(c),
        traceUnchanged,
      }.expect(TraceInvariants))

  action stepSuperseded(c: int): bool = all {
    ledgerMap.version != rec.get(c).version,
    closeLedger(c),
    traceUnchanged,
  }.expect(TraceInvariants)

  action stepLearn(f: int, t: int): bool = all {
    mapEnded(ledgerMap),
    ledgerMap.end == t,
    if (fetch.known.get(f) < ledgerMap.end) readMap(f) else unchangedVars,
    traceUnchanged,
  }.expect(TraceInvariants)

  action stepRetain(f: int, r: int): bool = all {
    if (canLearnTail(f, r)) learnTail(f, r) else unchangedVars,
    traceUnchanged,
  }.expect(TraceInvariants)

  action stepCheck(c: int, t: int, learned: bool): bool =
    (if (not(learned)) ensureCompleted(t) else traceStutter)
      .then(all {
        if (learned) all {
          mapEnded(ledgerMap),
          t == ledgerMap.end,
          beginCheck(c, minInt(t, writer.completed)),
        } else all {
          t <= writer.completed,
          beginCheck(c, t),
        },
        traceUnchanged,
      }.expect(TraceInvariants))

  action stepTail(t: int): bool =
    ensureCompleted(t)
      .then(all {
        knownTail(writer) == t,
        writeTail,
        traceUnchanged,
      }.expect(TraceInvariants))

  action stepChecked(c: int, repTail: int, isSealed: bool, isWriterCheck: bool, writerId: int): bool =
    (if (isWriterCheck and repTail > maxInt(chk.get(c).reportedTail, writerKnown(writerId))) ensureCompleted(repTail) else traceStutter)
      .then(all {
        chk.get(c).phase == "done",
        if (isWriterCheck) {
          val minTail = maxInt(chk.get(c).reportedTail, chkDoneCompleted.get(c))
          val maxTail = maxInt(chk.get(c).reportedTail, writerKnown(writerId))
          repTail >= minTail and repTail <= maxTail
        } else repTail == chk.get(c).reportedTail,
        chk.get(c).sealed == isSealed,
        unchangedVars,
        traceUnchanged,
      }.expect(TraceInvariants))

  action stepRestart(
    r: int, held: bool, isSealed: bool, data: Set[int], faulty: List[int],
    landed: List[(int, int, bool)],
    copied: List[(bool, int, int, int, bool)],
    sealedBy: List[(int, bool)]
  ): bool =
    ensureLandings(r, landed, copied, sealedBy)
      .then(silentAdmitForRestart(r, held).expect(TraceInvariants))
      .then(if (faulty.length() == 0) traceStutter
            else faulty.length().reps(i => silentCorruptRestartOne(r, faulty[i]).expect(TraceInvariants)))
      .then(all {
        if (canRestart(r)) restart(r) else unchangedVars,
        traceUnchanged,
      }.expect(and {
        TraceInvariants,
        recoveredStateMatches(r, held, isSealed, data, faulty.foldl(Set(), (s, p) => s.union(Set(p)))),
      }))

  action stepDamage(r: int): bool = all {
    unchangedVars,
    rcv' = rcv,
    alias' = alias,
    dmg' = dmg.union(Set(r)),
    chkDoneCompleted' = chkDoneCompleted,
  }.expect(TraceInvariants)
}
