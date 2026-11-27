// The membership core (layer L3): recording and applying decided entries.
//
// See docs/design/membership.md, "Agreement", "Recording", "Batches", and
// "Callers and fencing".
//
// Modeling choices:
// - Agreement is abstract. `decide` picks the next entry from submitted
//   batches, validated against the log headState; Rapid's cut detection and
//   consensus are not modeled. `staleAppend` is a writer from a stale
//   configuration that appends a different valid entry, so the log arbitrates.
// - The `membership` register in clog-metastore (L2) is abstract: `log` is the
//   history of its writes, one entry per view. Processes read only the head:
//   the stored view and the entry that produced it. A process one view behind
//   applies that entry; one further behind resyncs to the stored view.
// - Each node runs one process at a time; a restart is a new process with a
//   fresh instance ID that joins with the node's NodeId (Clog's UUID, modeled
//   as an int).
// - Submitters resend their batches until they see them committed or
//   invalid, which models lost acknowledgements.
// - Faults are flags enabled by the mutant actions; each breaks one rule.
module membership {

  // ------------------------------------------------------------ Constants

  const NODES: Set[int]
  const MAX_LOG: int
  const MAX_IDS: int          // bound on instance IDs
  const RETAINED: int         // defunct records retained

  // ------------------------------------------------------------ Types

  type Option[a] = | Some(a) | None

  type Status = | Joining | Active | ShuttingDown | Left | Dead

  type MemberId = { node: int, pid: int }

  type Removal = | RemoveDead | RemoveLeft

  // Commands a member submits about itself; `Configure` changes no status and
  // is valid only while the member is Active.
  type MemberCmd = | Activate | BeginShutdown | Configure

  // Each batch kind carries exactly what its origin may submit (M35).
  type Batch =
    | GenesisBatch
    // `follows` is the version this Join targets as its predecessor.
    | JoinBatch({ instance: int, node: int, follows: int })
    | MemberBatch({ member: MemberId, seq: int, cmds: List[MemberCmd] })
    | CutBatch({ member: MemberId, removal: Removal })

  // The model keeps `instance` on defunct records too; the design drops it,
  // which joins' `follows` rule makes harmless.
  type MemberRec = { status: Status, since: int, instance: int, lastBatch: Option[int] }

  type MState = MemberId -> MemberRec

  type Lookup = | LUnknown | LLive | LDefunct

  type Fault = | NoReplace | NoDedup | PruneForgets | PublishDecided | ResyncUnfenced

  type Proc = {
    up: bool,
    instance: int,
    me: Option[MemberId],
    applied: Option[int],     // entries applied; None before loading
    view: MState,
    nextSeq: int,
    outbox: Set[Batch],
  }

  // ------------------------------------------------------------ Membership state machine

  pure def getOr(o: Option[int], d: int): int = match o { | Some(x) => x | None => d }

  pure def isLive(s: Status): bool = match s { | Joining => true | Active => true | ShuttingDown => true | _ => false }

  pure def statusOf(st: MState, m: MemberId): Option[Status] =
    if (st.keys().contains(m)) Some(st.get(m).status) else None

  pure def live(st: MState, m: MemberId): bool = match statusOf(st, m) { | Some(s) => isLive(s) | None => false }
  pure def defunct(st: MState, m: MemberId): bool = match statusOf(st, m) { | Some(s) => not(isLive(s)) | None => false }
  pure def is(st: MState, m: MemberId, s: Status): bool = statusOf(st, m) == Some(s)

  pure def setStatus(st: MState, m: MemberId, s: Status, v: int): MState =
    if (st.keys().contains(m)) st.set(m, { ...st.get(m), status: s, since: v }) else st

  pure def removeKey(st: MState, k: MemberId): MState = st.keys().exclude(Set(k)).mapBy(x => st.get(x))

  // Keeps the newest RETAINED defunct records.
  pure def prune(st: MState, retained: int): MState = {
    val dead = st.keys().filter(m => defunct(st, m))
    dead.fold(st, (acc, m) => {
      val newer = dead.filter(o => acc.keys().contains(o) and (acc.get(o).since > acc.get(m).since
        or (acc.get(o).since == acc.get(m).since and (o.node > m.node or (o.node == m.node and o.pid > m.pid)))))
      if (acc.keys().contains(m) and newer.size() >= retained) removeKey(acc, m) else acc
    })
  }

  pure def hasInstance(st: MState, i: int): bool = st.keys().exists(m => st.get(m).instance == i)

  pure def replaceNode(st: MState, n: int, v: int, faults: Set[Fault]): MState =
    if (faults.contains(NoReplace)) st
    else st.keys().fold(st, (acc, m) => if (m.node == n and live(acc, m)) setStatus(acc, m, Dead, v) else acc)

  pure def applyMemberCmd(st: MState, m: MemberId, v: int, c: MemberCmd): MState = match c {
    | Activate => setStatus(st, m, Active, v)
    | BeginShutdown => setStatus(st, m, ShuttingDown, v)
    | Configure => st
  }

  pure def validMemberCmd(st: MState, m: MemberId, c: MemberCmd): bool = match c {
    | Activate => is(st, m, Joining)
    | BeginShutdown => is(st, m, Active)
    | Configure => is(st, m, Active)
  }

  pure def applyBatch(st: MState, v: int, b: Batch, faults: Set[Fault]): MState = prune(match b {
    | GenesisBatch => st
    | JoinBatch(j) => replaceNode(st, j.node, v, faults)
        .put({ node: j.node, pid: v }, { status: Joining, since: v, instance: j.instance, lastBatch: None })
    | MemberBatch(mb) => {
      val s1 = mb.cmds.foldl(st, (acc, c) => applyMemberCmd(acc, mb.member, v, c))
      if (s1.keys().contains(mb.member)) s1.set(mb.member, { ...s1.get(mb.member), lastBatch: Some(mb.seq) }) else s1
    }
    | CutBatch(c) => setStatus(st, c.member, match c.removal { | RemoveDead => Dead | RemoveLeft => Left }, v)
  }, RETAINED)

  pure def validBatch(st: MState, v: int, b: Batch, faults: Set[Fault]): bool = match b {
    | GenesisBatch => v == 0
    // A Join is valid only after the version it targets, as in Rapid, so a
    // stale request cannot commit after its instance's record is pruned.
    | JoinBatch(j) => v > 0 and j.follows == v - 1 and not(hasInstance(st, j.instance))
    // Batch sequence numbers make resubmission safe (M30).
    | MemberBatch(mb) => live(st, mb.member)
        and (faults.contains(NoDedup) or mb.seq > getOr(st.get(mb.member).lastBatch, 0))
        and mb.cmds.foldl({ ok: true, st: st }, (acc, c) =>
          { ok: acc.ok and validMemberCmd(acc.st, mb.member, c), st: applyMemberCmd(acc.st, mb.member, v, c) }).ok
    | CutBatch(c) => live(st, c.member) and (c.removal == RemoveDead or is(st, c.member, ShuttingDown))
  }

  pure def stateAt(l: List[Batch], k: int, faults: Set[Fault]): MState =
    l.foldl({ s: Map(), i: 0 }, (acc, b) =>
      { s: if (acc.i < k) applyBatch(acc.s, acc.i, b, faults) else acc.s, i: acc.i + 1 }).s

  // View::lookup (M34): status of a MemberId in a view at `version`.
  pure def lookup(st: MState, version: int, m: MemberId, faults: Set[Fault]): Lookup =
    match statusOf(st, m) {
      | Some(s) => if (isLive(s)) LLive else LDefunct
      | None => if (m.pid <= version and not(faults.contains(PruneForgets))) LDefunct else LUnknown
    }

  pure def rank(p: Lookup): int = match p { | LUnknown => 0 | LLive => 1 | LDefunct => 2 }

  // ------------------------------------------------------------ State

  // Each entry holds one batch, so a log position is a batch.
  var log: List[Batch]
  var decided: Option[Batch]     // consensus's value for position log.length()
  var procs: int -> Proc
  var nextId: int
  var faults: Set[Fault]
  // Ghost.
  var everDefunct: Set[MemberId]
  var answers: int -> (MemberId -> Lookup)
  var errors: Set[str]

  // ------------------------------------------------------------ Helpers

  def headState: MState = stateAt(log, log.length(), faults)

  pure val UNIVERSE_PIDS = 0.to(8)
  def universe: Set[MemberId] = tuples(NODES, UNIVERSE_PIDS).map(t => { node: t._1, pid: t._2 })

  def check(ok: bool, label: str): Set[str] = if (ok) errors else errors.union(Set(label))


  pure def freshProc(i: int): Proc =
    { up: true, instance: i, me: None, applied: None, view: Map(), nextSeq: 1, outbox: Set() }

  // Publishing a view: the process applies entry v, records its lookups in
  // the new view, and stops if it is defunct in it (C6). Lookups must only move forward.
  def publish(n: int, p: Proc, v: int, b: Batch): { proc: Proc, answers: MemberId -> Lookup, ok: bool } =
    adopt(n, { ...p, outbox: p.outbox.exclude(Set(b)) }, applyBatch(p.view, v, b, faults), v, true)

  // A process finds its MemberId by its instance ID once its Join commits.
  pure def findMe(view: MState, p: Proc): Option[MemberId] = match p.me {
    | Some(m) => Some(m)
    | None => {
      val mine = view.keys().filter(m => view.get(m).instance == p.instance)
      if (mine.size() == 1) Some(mine.fold({ node: 0, pid: 0 }, (_, m) => m)) else None
    }
  }

  // Adopts `view` at version `v`: records the process's lookups, which must
  // only move forward, and stops the process if it is defunct (C6) unless
  // `fence` is false.
  def adopt(n: int, p: Proc, view: MState, v: int, fence: bool): { proc: Proc, answers: MemberId -> Lookup, ok: bool } = {
    val me = findMe(view, p)
    val fenced = fence and match me { | Some(m) => defunct(view, m) | None => false }
    val old = answers.get(n)
    val fresh = universe.mapBy(m => lookup(view, v, m, faults))
    val forward = universe.forall(m => rank(fresh.get(m)) >= rank(old.get(m)))
    val outbox = p.outbox.filter(o => validBatch(view, v + 1, o, faults))
    { proc: { ...p, view: view, applied: Some(v + 1), me: me, up: p.up and not(fenced), outbox: outbox },
      answers: fresh, ok: forward }
  }

  // Loads the stored view: at startup, or to resync after falling more than
  // one view behind. A resync is a view change like any other (C6).
  def loadHead(n: int, p: Proc): { proc: Proc, answers: MemberId -> Lookup, ok: bool } =
    adopt(n, p, headState, log.length() - 1, not(faults.contains(ResyncUnfenced)))

  action unchanged = all {
    log' = log, decided' = decided, procs' = procs,
    nextId' = nextId, faults' = faults, everDefunct' = everDefunct, answers' = answers, errors' = errors,
  }

  // ------------------------------------------------------------ Init

  action init = all {
    log' = List(),
    decided' = None,
    procs' = NODES.mapBy(n => { ...freshProc(0), up: false }),
    nextId' = 1,
    faults' = Set(),
    everDefunct' = Set(),
    answers' = NODES.mapBy(n => tuples(NODES, 0.to(8)).map(t => { node: t._1, pid: t._2 }).mapBy(m => LUnknown)),
    errors' = Set(),
  }

  // ------------------------------------------------------------ Processes

  action start(n: int): bool = all {
    not(procs.get(n).up), nextId < MAX_IDS,
    procs' = procs.set(n, freshProc(nextId)),
    nextId' = nextId + 1,
    // A new process has no answers yet.
    answers' = answers.set(n, universe.mapBy(m => LUnknown)),
    log' = log, decided' = decided, faults' = faults, everDefunct' = everDefunct, errors' = errors,
  }

  action crash(n: int): bool = all {
    procs.get(n).up,
    procs' = procs.set(n, { ...procs.get(n), up: false }),
    log' = log, decided' = decided, nextId' = nextId,
    faults' = faults, everDefunct' = everDefunct, answers' = answers, errors' = errors,
  }

  // Reads the register at startup: the stored view, or an empty one before genesis.
  action load(n: int): bool = {
    val p = procs.get(n)
    val r = loadHead(n, p)
    all {
      p.up, p.applied == None,
      procs' = procs.set(n, r.proc), answers' = answers.set(n, r.answers), errors' = check(r.ok, "lookup-monotonic"),
      log' = log, decided' = decided, nextId' = nextId, faults' = faults, everDefunct' = everDefunct,
    }
  }

  // One view behind the register: applies the stored entry (C2: only
  // committed entries are published).
  action readNext(n: int): bool = {
    val p = procs.get(n)
    val v = getOr(p.applied, -1)
    all {
      p.up, v >= 0, v == log.length() - 1,
      val r = publish(n, p, v, log[v])
      all { procs' = procs.set(n, r.proc), answers' = answers.set(n, r.answers), errors' = check(r.ok, "lookup-monotonic") },
      log' = log, decided' = decided, nextId' = nextId,
      faults' = faults, everDefunct' = everDefunct,
    }
  }

  // More than one view behind the register: loads the stored view.
  action resync(n: int): bool = {
    val p = procs.get(n)
    val v = getOr(p.applied, -1)
    val r = loadHead(n, p)
    all {
      p.up, v >= 0, v < log.length() - 1,
      procs' = procs.set(n, r.proc), answers' = answers.set(n, r.answers), errors' = check(r.ok, "lookup-monotonic"),
      log' = log, decided' = decided, nextId' = nextId, faults' = faults, everDefunct' = everDefunct,
    }
  }

  // ------------------------------------------------------------ Submitting

  action submit(n: int, b: Batch, seqUsed: bool): bool = {
    val p = procs.get(n)
    all {
      p.up, p.applied != None,
      procs' = procs.set(n, { ...p, outbox: p.outbox.union(Set(b)), nextSeq: if (seqUsed) p.nextSeq + 1 else p.nextSeq }),
      log' = log, decided' = decided, nextId' = nextId,
      faults' = faults, everDefunct' = everDefunct, answers' = answers, errors' = errors,
    }
  }

  // A joiner submits against its current view, and again after its view advances.
  action submitJoin(n: int): bool = {
    val p = procs.get(n)
    all { p.me == None, p.outbox == Set(), log.length() > 0,
          submit(n, JoinBatch({ instance: p.instance, node: n, follows: getOr(p.applied, 0) - 1 }), false) }
  }

  // Commands a member submits about itself.
  action submitSelf(n: int, kind: int): bool = {
    val p = procs.get(n)
    match p.me {
      | Some(me) => all { p.nextSeq <= MAX_LOG, submit(n, MemberBatch({ member: me, seq: p.nextSeq,
          cmds: [if (kind == 0) Activate else if (kind == 1) BeginShutdown else Configure] }), true) }
      | None => all { false, unchanged }
    }
  }

  // ------------------------------------------------------------ Agreement and recording

  // Consensus decides the next entry: genesis, a submitted batch, or a cut.
  action decide(b: Batch): bool = all {
    decided == None, log.length() < MAX_LOG,
    validBatch(headState, log.length(), b, faults),
    decided' = Some(b),
    log' = log, procs' = procs, nextId' = nextId,
    faults' = faults, everDefunct' = everDefunct, answers' = answers, errors' = errors,
  }

  def candidates: Set[Batch] = {
    val submitted = NODES.map(n => procs.get(n).outbox).flatten()
    val liveNow = headState.keys().filter(m => live(headState, m))
    val cuts = liveNow.map(m => CutBatch({ member: m, removal: RemoveDead }))
      .union(liveNow.filter(m => is(headState, m, ShuttingDown)).map(m => CutBatch({ member: m, removal: RemoveLeft })))
    val genesis = if (log.length() == 0) Set(GenesisBatch) else Set()
    submitted.union(cuts).union(genesis)
  }

  // The recorder writes the decided entry's view to the register,
  // conditionally on the previous view; the write is the commit point.
  action record: bool = match decided {
    | Some(b) => {
      val log2 = log.append(b)
      val s = stateAt(log2, log2.length(), faults)
      all {
        log' = log2,
        decided' = None,
        everDefunct' = everDefunct.union(s.keys().filter(m => defunct(s, m))),
        procs' = procs, nextId' = nextId,
        faults' = faults, answers' = answers, errors' = errors,
      }
    }
    | None => all { false, unchanged }
  }

  // A stale configuration's writer commits a different valid entry first; the
  // register arbitrates, and the decided value is discarded.
  action staleAppend(b: Batch): bool = all {
    log.length() < MAX_LOG,
    validBatch(headState, log.length(), b, faults),
    val log2 = log.append(b)
    val s = stateAt(log2, log2.length(), faults)
    all {
      log' = log2,
      decided' = None,
      everDefunct' = everDefunct.union(s.keys().filter(m => defunct(s, m))),
      procs' = procs, nextId' = nextId,
      faults' = faults, answers' = answers, errors' = errors,
    }
  }

  // ------------------------------------------------------------ Step

  action step = {
    nondet n = NODES.oneOf()
    nondet kind = 0.to(2).oneOf()
    any {
      start(n), crash(n), load(n), readNext(n), resync(n),
      submitJoin(n), submitSelf(n, kind),
      all { candidates != Set(), nondet b = candidates.oneOf() any { decide(b), staleAppend(b) } },
      record,
    }
  }

  // ------------------------------------------------------------ Invariants

  // C1, C8: every committed batch is valid against the state it applies to.
  val logValid = log.indices().forall(i => validBatch(stateAt(log, i, faults), i, log[i], faults))

  // C8: no batch is committed twice, and each joiner instance joins at most once.
  val batchesOnce = log.indices().forall(i => log.indices().forall(j =>
    i == j or match log[i] { | MemberBatch(_) => log[i] != log[j] | _ => true }))
  val instancesOnce = headState.keys().forall(a => headState.keys().forall(b =>
    a == b or headState.get(a).instance != headState.get(b).instance))

  // C2: each process's view is exactly the log prefix it has applied.
  val viewsArePrefixes = NODES.forall(n => {
    val p = procs.get(n)
    not(p.up) or match p.applied {
      | Some(k) => k <= log.length() and p.view == stateAt(log, k, faults)
      | None => true
    }
  })

  // C3: a defunct member never becomes live again; one live process per node.
  val noResurrection = everDefunct.forall(m => not(live(headState, m)))
  val oneLivePerNode = NODES.forall(n => headState.keys().filter(m => m.node == n and live(headState, m)).size() <= 1)

  // C6: a running process never holds a view in which it is defunct.
  val selfFencing = NODES.forall(n => {
    val p = procs.get(n)
    not(p.up) or match p.me { | Some(m) => not(defunct(p.view, m)) | None => true }
  })

  // Member lookups only move forward, including across pruning.
  val lookupMonotonic = errors == Set()

  val safety = logValid and batchesOnce and instancesOnce and viewsArePrefixes and noResurrection
    and oneLivePerNode and selfFencing and lookupMonotonic

  // ------------------------------------------------------------ Mutants

  action enable(f: Fault): bool = all {
    not(faults.contains(f)),
    faults' = faults.union(Set(f)),
    log' = log, decided' = decided, procs' = procs,
    nextId' = nextId, everDefunct' = everDefunct, answers' = answers, errors' = errors,
  }

  // A process publishes the decided entry before it is durable; a stale
  // writer then takes the position.
  action publishDecided(n: int): bool = {
    val p = procs.get(n)
    val v = getOr(p.applied, -1)
    match decided {
      | Some(b) => all {
        p.up, v == log.length(),
        val r = publish(n, p, v, b)
        all { procs' = procs.set(n, r.proc), answers' = answers.set(n, r.answers) },
        log' = log, decided' = decided, nextId' = nextId,
        faults' = faults, everDefunct' = everDefunct, errors' = errors,
      }
      | None => all { false, unchanged }
    }
  }

  // A restart's Join leaves the node's previous process live.
  // breaks: oneLivePerNode
  action mutantNoReplace = any { step, enable(NoReplace) }
  // Resubmitted batches are not deduplicated by sequence number.
  // breaks: batchesOnce
  action mutantNoDedup = any { step, enable(NoDedup) }
  // Lookups answer Unknown for pruned members.
  // breaks: lookupMonotonic
  action mutantPruneForgets = any { step, enable(PruneForgets) }
  // Views are published before their entries are durable.
  // breaks: viewsArePrefixes
  action mutantPublishDecided = { nondet n = NODES.oneOf() any { step, publishDecided(n) } }
  // A process that resyncs to the stored view keeps running although the view
  // shows it defunct.
  // breaks: selfFencing
  action mutantResyncUnfenced = any { step, enable(ResyncUnfenced) }
}

// Configuration for random simulation and directed scenarios.
module membershipSim {
  import membership(NODES = Set(1, 2, 3), MAX_LOG = 8, MAX_IDS = 12, RETAINED = 1).*

  pure val m1 = { node: 1, pid: 1 }
  pure val g = GenesisBatch
  pure val activate1 = MemberBatch({ member: m1, seq: 1, cmds: [Activate] })
  pure val configure1 = MemberBatch({ member: m1, seq: 2, cmds: [Configure] })
  pure val kill1 = CutBatch({ member: m1, removal: RemoveDead })

  // Genesis, then node 1 joins and activates.
  run oneActive =
    init
      .then(start(1)).then(load(1))
      .then(decide(g)).then(record).then(readNext(1))
      .then(submitJoin(1)).then(decide(JoinBatch({ instance: 1, node: 1, follows: 0 })))
      .then(record).then(readNext(1))
      .expect(procs.get(1).me == Some({ node: 1, pid: 1 }))
      .then(submitSelf(1, 0)).then(decide(activate1))
      .then(record).then(readNext(1))
      .expect(safety and is(headState, { node: 1, pid: 1 }, Active))

  run oneActiveTest = oneActive

  // The Activate batch is resubmitted after a lost acknowledgement; its
  // sequence number makes the retry invalid.
  run resubmitRejectedTest =
    oneActive.then(decide(activate1).fail())

  // A stale writer takes version 3 after node 1's Configure was decided; nothing
  // was published early, so views stay prefixes.
  run staleWriterTest =
    oneActive
      .then(submitSelf(1, 2)).then(decide(configure1))
      .then(staleAppend(kill1))
      .then(readNext(1))
      .expect(safety and not(procs.get(1).up))

  // Without sequence-number deduplication, a resent batch commits twice.
  run noDedupTest =
    oneActive
      .then(submitSelf(1, 2)).then(decide(configure1)).then(record)
      .then(enable(NoDedup)).then(decide(configure1)).then(record)
      .expect(not(batchesOnce))

  run publishDecidedTest =
    oneActive
      .then(submitSelf(1, 2)).then(decide(configure1))
      .then(publishDecided(1))
      .then(staleAppend(kill1))
      .expect(not(safety))


  // Node 1 restarts; the new process's Join replaces the old one.
  run restartReplacesTest =
    oneActive
      .then(crash(1)).then(start(1)).then(load(1))
      .then(submitJoin(1)).then(decide(JoinBatch({ instance: 2, node: 1, follows: 2 })))
      .then(record).then(readNext(1))
      .expect(safety and statusOf(headState, { node: 1, pid: 1 }) == Some(Dead) and procs.get(1).me == Some({ node: 1, pid: 3 }))

  // Node 1 falls two views behind, so it cannot apply the stored entry and
  // resyncs; the stored view shows it dead, so it stops.
  run resyncFencesTest =
    oneActive
      .then(submitSelf(1, 2)).then(decide(configure1)).then(record)
      .then(decide(kill1)).then(record)
      .expect(procs.get(1).applied == Some(log.length() - 2))
      .then(resync(1))
      .expect(safety and not(procs.get(1).up))

  // Without the C6 check on resync, the dead process keeps running.
  run resyncUnfencedTest =
    oneActive
      .then(submitSelf(1, 2)).then(decide(configure1)).then(record)
      .then(decide(kill1)).then(record)
      .then(enable(ResyncUnfenced)).then(resync(1))
      .expect(not(selfFencing))
}

// Smaller configuration for exhaustive model checking with TLC.
module membershipTlc {
  import membership(NODES = Set(1, 2), MAX_LOG = 3, MAX_IDS = 3, RETAINED = 1).*
}
