// -*- mode: Bluespec; -*-
/**
 * The Archive LSM's manifest, compaction, clock-free garbage collection, and history reads
 * (test/Durable.Storage/docs/spec/components/archive-lsm.md §5, §8; test/Durable.Storage/docs/spec/components/archive.md §8.1).
 *
 * One key's decided history lies in two ledgers of one lineage: Ledger1, closed, and its successor Ledger2, open until
 * it closes, as entry kinds "A" (Append), "R" (Replace), and "E" (Expired), each tagged (ledger, slot) as
 * ledger * 10 + slot, and markers, "M", batches which hold no entry. The fold returns the current incarnation (the
 * entries after the last Expired) from its last Replace.
 *
 * Each ledger is cut into segments, which start at the slots Starts1 and Starts2 name, slot 0 and every marker among
 * them. Ledger2's batches are decided in slot order, a segment at a time: each step decides through the next segment's
 * start batch, and Ledger2 closes once every batch is decided. A segment is closed once a later segment starts, a
 * decided batch with the start flag, or once its ledger closes, when it ends at the ledger's end T.
 *
 * Workers are stateless and unfenced, and every upload and commit is a separate step, so stale workers race freely. A
 * drain uploads the entries of the closed segment which starts at a ledger's watermark, [watermark, b) with b the next
 * segment's start or T, as an object, empty for a segment which holds a marker alone, and commits it iff the watermark
 * still equals its start, so every watermark is a segment start or T (LSM-I2), and no drain's change is superseded:
 * the watermark is its start or past its end. The open Ledger2 drains a segment only once a contiguous tail the drainer
 * learns, at most the decided prefix, passes the next segment's start batch. A ledger is complete once it is closed
 * and its watermark reaches its end. A compaction uploads the reduction of committed objects:
 * an entry is dropped when a newer Replace or Expired in its inputs, of a ledger complete when the compaction reads the
 * manifest, supersedes it, and such an Expired too once no older entry of the key lies outside its inputs, in a
 * committed object or undrained in the live ledgers; a worker which judged by older knowledge, of fewer complete ledgers,
 * or keeping an Expired it could drop, uploads a superset of it. It commits iff every input is still listed. A complete ledger
 * leaves the ledger map, in lineage order. The sweeper lists the store, then snapshots the manifest, then deletes listed
 * objects which the snapshot does not reference and which could never be committed.
 *
 * A reader takes the ledger map, then snapshots the manifest, starting again when the map lists Ledger2 open and the
 * manifest shows it complete; it then reads the objects, retrying when one is missing, and folds the committed entries
 * of the ledgers its map no longer lists, the prefix, followed by every entry of each ledger its map lists, from slot 0,
 * in tag order: Ledger1's to its end, and Ledger2's to its end when the map lists it closed, or to a tail the reader
 * chooses, at most the decided prefix and lagging any drain, while it is open.
 */
module Archive {
  const Ledger1: List[str]
  const Ledger2: List[str]
  const Starts1: Set[int]
  const Starts2: Set[int]
  const MaxObjects: int
  const Mutation: str

  type Row = { tag: int, kind: str }
  type Obj = { used: bool, exists: bool, drain: bool, ledger: int, a: int, b: int, rows: Set[Row], inputs: Set[int] }
  type Sweeper = { phase: int, listed: bool, listing: Set[int], snapped: bool, refs: Set[int], w: int -> int }
  type Reader = { phase: int, listed: Set[int], closed2: bool, refs: Set[int], w: int -> int }
  type State = {
    obj: int -> Obj,
    nextObj: int,
    // The manifest: committed objects and each ledger's watermark.
    refs: Set[int],
    watermark: int -> int,
    lowered: bool,
    // The ledger map: the ledgers it lists, and whether Ledger2 is closed.
    listed: Set[int],
    closed2: bool,
    // The decided prefix of Ledger2: its batches below it are decided.
    decided2: int,
    sweeper: Sweeper,
    reader: Reader,
    badRead: bool,
  }

  pure val Ledgers = Set(1, 2)
  pure val Objs = 1.to(MaxObjects)
  pure def entries(i: int): List[str] = if (i == 1) Ledger1 else Ledger2
  pure def recordedT(i: int): int = entries(i).length()
  pure def starts(i: int): Set[int] = if (i == 1) Starts1 else Starts2
  // The end of the segment which starts at a: the next segment's start, or T.
  pure def segmentEnd(i: int, a: int): int =
    starts(i).fold(recordedT(i), (e, x) => if (x > a and x < e) x else e)
  pure val MaxTail = if (recordedT(1) > recordedT(2)) recordedT(1) else recordedT(2)
  pure def slot(r: Row): int = r.tag % 10
  pure def ledgerOf(r: Row): int = r.tag / 10
  pure def live(i: int): Set[Row] =
    0.to(recordedT(i) - 1).filter(p => entries(i)[p] != "M").map(p => { tag: i * 10 + p, kind: entries(i)[p] })
  pure val Decided = live(1).union(live(2))

  pure def lastOf(rows: Set[Row], kind: str): int = rows.fold(0, (m, r) => if (r.kind == kind and r.tag > m) r.tag else m)
  pure def reduced(rows: Set[Row]): Set[Row] =
    val inc = rows.filter(r => r.tag > lastOf(rows, "E"))
    inc.filter(r => r.tag >= lastOf(inc, "R"))
  pure val Truth = reduced(Decided)

  // A state after one more entry: a Replace becomes the base, an Expired empties it, and an Append adds to it.
  pure def apply(state: Set[Row], r: Row): Set[Row] =
    if (r.kind == "R") Set(r) else if (r.kind == "E") Set() else state.union(Set(r))
  // A prefix's state followed by entries, in tag order.
  pure def foldOnto(state: Set[Row], rows: Set[Row]): Set[Row] =
    range(0, 30).foldl(state, (st, t) => rows.filter(r => r.tag == t).fold(st, (s2, r) => apply(s2, r)))

  pure val noObj: Obj = { used: false, exists: false, drain: true, ledger: 1, a: 0, b: 0, rows: Set(), inputs: Set() }
  pure val idleSweeper: Sweeper =
    { phase: 0, listed: false, listing: Set(), snapped: false, refs: Set(), w: Ledgers.mapBy(_ => 0) }
  pure val idleReader: Reader = { phase: 0, listed: Set(), closed2: false, refs: Set(), w: Ledgers.mapBy(_ => 0) }

  var s: State

  action init = s' = {
    obj: Objs.mapBy(_ => noObj),
    nextObj: 1,
    refs: Set(),
    watermark: Ledgers.mapBy(_ => 0),
    lowered: false,
    listed: Ledgers,
    closed2: false,
    decided2: if (recordedT(2) > 0) 1 else 0,
    sweeper: idleSweeper,
    reader: idleReader,
    badRead: false,
  }

  def closed(i: int): bool = i == 1 or s.closed2
  // The decided prefix: all of the closed Ledger1, and Ledger2's below decided2.
  def decided(i: int): int = if (i == 1) recordedT(1) else s.decided2
  // The manifest shows a ledger complete: closed, with its watermark at its end.
  def complete(i: int): bool = closed(i) and s.watermark.get(i) == recordedT(i)
  def committed(refs: Set[int]): Set[Row] = refs.map(o => s.obj.get(o).rows).flatten()
  def undrained: Set[Row] = Ledgers.map(i => live(i).filter(r => slot(r) >= s.watermark.get(i))).flatten()
  // The history the manifest and the live ledgers hold now.
  def view: Set[Row] = committed(s.refs).union(undrained)

  def upload(x: Obj): State = { ...s, nextObj: s.nextObj + 1, obj: s.obj.set(s.nextObj, x) }

  // ---------------------------------------------------------------------------
  // Drains of whole closed segments. A drain of the open Ledger2 learns a
  // contiguous tail at most the decided prefix, and takes a segment only when
  // the next segment's start batch lies below it. The stale-drain mutant
  // commits without checking the watermark; the lsm-mid-segment-watermark
  // mutant drains below any decided slot, inside a segment too.
  // ---------------------------------------------------------------------------
  def drainsTo(i: int, a: int, b: int): bool =
    if (Mutation == "lsm-mid-segment-watermark") a < b and (b < decided(i) or (closed(i) and b <= recordedT(i)))
    else a < recordedT(i) and b == segmentEnd(i, a) and (b < decided(i) or closed(i))

  action drainUpload(i: int, b: int): bool =
    val a = s.watermark.get(i)
    all {
      drainsTo(i, a, b), s.nextObj <= MaxObjects,
      s' = upload({
        ...noObj, used: true, exists: true, ledger: i, a: a, b: b,
        rows: live(i).filter(r => slot(r) >= a and slot(r) < b),
      }),
    }

  action drainCommit(o: int): bool =
    val x = s.obj.get(o)
    val w = s.watermark.get(x.ledger)
    all {
      x.used, x.drain, not(s.refs.contains(o)),
      Mutation == "stale-drain" or w == x.a,
      s' = { ...s, refs: s.refs.union(Set(o)), watermark: s.watermark.set(x.ledger, x.b), lowered: s.lowered or x.b < w },
    }

  // ---------------------------------------------------------------------------
  // Ledger2's batches are decided through the next segment's start batch, and
  // the ledger map closes it once every batch is decided; a complete ledger
  // leaves the map, Ledger1 first (LG-I10, LSM-I3).
  // ---------------------------------------------------------------------------
  action decideStart: bool =
    val nextStart = segmentEnd(2, s.decided2 - 1)
    all {
      s.decided2 < recordedT(2),
      s' = { ...s, decided2: if (nextStart < recordedT(2)) nextStart + 1 else recordedT(2) },
    }

  action close2: bool = all { not(s.closed2), s.decided2 == recordedT(2), s' = { ...s, closed2: true } }

  action drop(i: int): bool = all {
    s.listed.contains(i), complete(i), i == 1 or not(s.listed.contains(1)),
    s' = { ...s, listed: s.listed.exclude(Set(i)) },
  }

  // ---------------------------------------------------------------------------
  // Compaction. A worker judges by what it read, which may be older than the
  // state its upload lands in: it supersedes only by entries of the ledgers it
  // knows complete, among those complete now, and may keep an Expired it could
  // drop, as one which read an older manifest, met a Bloom filter's false
  // positive, or kept the Expired by the code's coarser rule while an older
  // ledger containing its key was incomplete. The drop-ignoring-sections and
  // drop-ignoring-live mutants each disable one half of the Expired drop rule's
  // check for older entries; the supersede-by-open mutant lets a Replace or
  // Expired of a ledger not yet complete supersede.
  // ---------------------------------------------------------------------------
  def outside(inputs: Set[int]): Set[Row] =
    val sections = if (Mutation == "drop-ignoring-sections") Set() else committed(s.refs.exclude(inputs))
    val liveRows = if (Mutation == "drop-ignoring-live") Set() else undrained
    sections.union(liveRows)

  def completeLedgers: Set[int] = Ledgers.filter(i => complete(i))

  // The reduction a worker which knows the ledgers in known complete makes, dropping an Expired only when dropExpired.
  def reduceAs(inputs: Set[int], known: Set[int], dropExpired: bool): Set[Row] =
    val rows = committed(inputs)
    val out = outside(inputs)
    def supersedes(x: Row): bool =
      (x.kind == "R" or x.kind == "E") and (Mutation == "supersede-by-open" or known.contains(ledgerOf(x)))
    rows.filter(r => and {
      not(rows.exists(x => x.tag > r.tag and supersedes(x))),
      r.kind != "E" or not(dropExpired) or not(supersedes(r)) or out.exists(y => y.tag < r.tag),
    })

  // The reduction of a worker which knows the state its upload lands in: it drops the most.
  def reduce(inputs: Set[int]): Set[Row] = reduceAs(inputs, completeLedgers, true)

  // A compaction uploads every row of its inputs the reduction keeps, and perhaps more: the step keeps what a worker
  // judging by older knowledge keeps, and trace validation the rows a compaction output holds, which the invariants judge.
  action compactKeep(inputs: Set[int], rows: Set[Row]): bool = all {
    inputs != Set(), inputs.subseteq(s.refs), s.nextObj <= MaxObjects,
    reduce(inputs).subseteq(rows), rows.subseteq(committed(inputs)),
    s' = upload({ ...noObj, used: true, exists: true, drain: false, rows: rows, inputs: inputs }),
  }

  // A worker's compaction, judged by the knowledge it read: the reduction, or a superset of it (see reduceAs). The
  // choices are scoped to this action, so they multiply no other step's successors.
  action compactJudged(inputs: Set[int]): bool = {
    nondet known = completeLedgers.powerset().oneOf()
    nondet dropExpired = Set(true, false).oneOf()
    compactKeep(inputs, reduceAs(inputs, known, dropExpired))
  }

  action compactCommit(o: int): bool =
    val x = s.obj.get(o)
    all {
      x.used, not(x.drain), not(s.refs.contains(o)), x.inputs.subseteq(s.refs),
      s' = { ...s, refs: s.refs.exclude(x.inputs).union(Set(o)) },
    }

  // ---------------------------------------------------------------------------
  // The sweeper: list, then snapshot, then delete what the snapshot proves can
  // never be committed. The snapshot-before-list mutant reverses the first two
  // steps; the sweep-* mutants delete an unreferenced object without its
  // condition.
  // ---------------------------------------------------------------------------
  def withListing(x: Sweeper): Sweeper = { ...x, listed: true, listing: Objs.filter(o => s.obj.get(o).exists) }
  def withSnapshot(x: Sweeper): Sweeper = { ...x, snapped: true, refs: s.refs, w: s.watermark }

  action sweepFirst: bool =
    val x = { ...idleSweeper, phase: 1 }
    all {
      s.sweeper.phase == 0,
      s' = { ...s, sweeper: if (Mutation == "snapshot-before-list") withSnapshot(x) else withListing(x) },
    }

  action sweepSecond: bool =
    val x = s.sweeper
    val both = if (x.listed) withSnapshot(x) else withListing(x)
    all {
      x.phase == 1,
      s' = { ...s, sweeper: { ...both, phase: 2 } },
    }

  def neverCommitted(x: Obj, w: Sweeper): bool =
    if (x.drain) Mutation == "sweep-drain" or w.w.get(x.ledger) > x.a
    else Mutation == "sweep-compaction" or not(x.inputs.subseteq(w.refs))

  action sweepDelete(o: int): bool =
    val w = s.sweeper
    val x = s.obj.get(o)
    all {
      w.phase == 2, w.listing.contains(o), x.exists, not(w.refs.contains(o)), neverCommitted(x, w),
      s' = { ...s, obj: s.obj.set(o, { ...x, exists: false }) },
    }

  action sweepDone: bool = all {
    s.sweeper.phase == 2,
    s' = { ...s, sweeper: idleSweeper },
  }

  // ---------------------------------------------------------------------------
  // Reads: the map, then the manifest, then the objects and the live ledgers.
  // The ignore-missing mutant reads what remains instead of retrying; the
  // hold-by-watermark mutant holds every committed entry and scans each listed
  // ledger from its watermark, the rejected merge; the no-predates-check mutant
  // reads with a map which lists Ledger2 open while the manifest shows it
  // complete.
  // ---------------------------------------------------------------------------
  action readMap: bool = all {
    s.reader.phase == 0,
    s' = { ...s, reader: { ...idleReader, phase: 1, listed: s.listed, closed2: s.closed2 } },
  }

  action readManifest: bool =
    val r = s.reader
    val predates = r.listed.contains(2) and not(r.closed2) and complete(2)
    all {
      r.phase == 1,
      if (predates and Mutation != "no-predates-check") s' = { ...s, reader: idleReader }
      // Only the hold-by-watermark mutant reads the watermarks; keeping them otherwise would only multiply states.
      else s' = { ...s, reader: { ...r, phase: 2, refs: s.refs, w: if (Mutation == "hold-by-watermark") s.watermark else r.w } },
    }

  // The read's result with the open Ledger2 read below upTo, its tail, and the history at that cut.
  def readResult(r: Reader, present: Set[int], upTo: int): Set[Row] =
    val scanned = r.listed.map(i => live(i).filter(x => slot(x) < (if (i == 2 and not(r.closed2)) upTo else recordedT(i)))).flatten()
    if (Mutation == "hold-by-watermark")
      foldOnto(reduced(committed(present)), scanned.filter(x => slot(x) >= r.w.get(ledgerOf(x))))
    else
      foldOnto(reduced(committed(present).filter(x => not(r.listed.contains(ledgerOf(x))))), scanned)

  def cut(r: Reader, upTo: int): Set[Row] =
    if (r.listed.contains(2) and not(r.closed2)) live(1).union(live(2).filter(x => slot(x) < upTo)) else Decided

  action readFinish(upTo: int): bool =
    val r = s.reader
    val present = r.refs.filter(o => s.obj.get(o).exists)
    val missing = present != r.refs
    all {
      r.phase == 2, upTo <= s.decided2,
      if (missing and Mutation != "ignore-missing") s' = { ...s, reader: idleReader }
      else s' = { ...s, reader: idleReader, badRead: s.badRead or missing or readResult(r, present, upTo) != reduced(cut(r, upTo)) },
    }

  action step = any {
    nondet i = Ledgers.oneOf()
    nondet b = 1.to(MaxTail).oneOf()
    nondet o = Objs.oneOf()
    nondet inputs = s.refs.powerset().oneOf()
    nondet upTo = 0.to(MaxTail).oneOf()
    any { drainUpload(i, b), drainCommit(o), compactJudged(inputs), compactCommit(o), sweepDelete(o), drop(i), readFinish(upTo) },
    decideStart, close2, sweepFirst, sweepSecond, sweepDone, readMap, readManifest,
  }

  // ---------------------------------------------------------------------------
  // Invariants
  // ---------------------------------------------------------------------------
  // Every object the manifest references exists.
  val ReferencedExist = s.refs.forall(o => s.obj.get(o).exists)

  // The manifest and the undrained live-ledger entries fold to the decided history's
  // fold: compaction preserves every key's fold.
  val FoldPreserved = reduced(view) == Truth

  // Watermarks never decrease.
  val WatermarksMonotone = not(s.lowered)

  // Every watermark is a segment start or its ledger's end (LSM-I2).
  val WatermarksAtSegmentStarts = Ledgers.forall(i => starts(i).contains(s.watermark.get(i)) or s.watermark.get(i) == recordedT(i))

  // No drain's change is superseded: the watermark of every drain object not referenced, uploaded and not yet
  // committed or committed and since compacted away, is its start or at least its end, so its commit applies, or finds
  // its goal held (LSM-I2).
  val NoDrainSuperseded = Objs.forall(o =>
    val x = s.obj.get(o)
    not(x.used and x.drain and not(s.refs.contains(o))) or s.watermark.get(x.ledger) == x.a or s.watermark.get(x.ledger) >= x.b)

  // A read which finds every object it needs returns the fold of the decided history at its cut: the prefix of the
  // ledgers its map no longer lists, followed by every listed ledger's entries from slot 0.
  val ReadsCorrect = not(s.badRead)
}
