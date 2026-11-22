// -*- mode: Bluespec; -*-
/**
 * Archive logs: per-segment writes of one key's log, racing and straggler drains, crashes, progress markers, drops, the
 * sweeper, readers, and Journals' loader (test/Durable.Storage/docs/spec/components/archive-logs.md §2.3–§2.5, §6, §8.1–§8.5; journals.md
 * §8.3).
 *
 * One key's decided history lies in two ledgers of one lineage: Ledger1, closed, and its successor Ledger2, open until it
 * closes, as entry kinds "A" (Append), "R" (Replace), and "E" (Delete), each tagged (ledger, slot) as ledger * 10 + slot,
 * "O", another key's row, and markers, "M", batches which hold no row. Each ledger is cut into segments, which start at the
 * slots Starts1 and Starts2 name. Ledger2's batches are decided a segment at a time, as in Archive.qnt, and it closes
 * once every batch is decided. A segment fixes the key's write: an opener, the last "R" or "E", whose base is it and the
 * later "A"s, after the rows it supersedes; or an append run of its "A"s; or none. Every write stores its token, the
 * position the key's previous write leaves, replayed by Plan over the lineage from Origin (JS-I10).
 *
 * The log is Azure's: a write-ahead object at a position (g, r, P), where r counts the key's rows the generation's base
 * folds (§7.2), naming its generation's checkpoint when an opener created the generation, and the checkpoints by
 * generation. Each request is one action, so crash cut points match model states: a first run's Create of generation 1,
 * each Append Block (one block an Append), conditioned on the generation's tag and on P, an opener's Put Blob of its
 * checkpoint, created only when absent, and its Put Blob of the write-ahead object, conditioned on the generation it
 * starts from, and each segment's marker. A failed condition costs one properties read, classified by §8.3's tables;
 * a read which shows the write's predecessor missing, or a log which contradicts the write, blocks the drain. Drainers
 * race freely: any number of them may drain one segment, a drainer may crash at any state, and a crashed drainer's
 * request may land later, beside its restarted worker, as a straggler's does. A drain runs only while no older ledger is
 * listed, starts at the end of the highest marker of its ledger, takes one whole segment closed below the decided prefix,
 * and creates the segment's marker once the key's write is held; a segment of markers alone holds no row and gets no
 * marker. A ledger drops once closed and every segment holding rows is marked, Ledger1 first.
 *
 * The sweeper lists the checkpoints, reads the log's properties, and deletes the listed checkpoints below the current
 * generation, and drops dropped ledgers' markers. A reader reads the map, then the log, and scans each listed ledger, the
 * open one below a tail it learned, which may lag the decided prefix. The loader, as Journals' primary loads a key, reads
 * the map, then the log, and then takes its version: its last live row's, or, with no live row, a rebase to the log's
 * position.
 */
module ArchiveLogs {
  const Ledger1: List[str]
  const Ledger2: List[str]
  const Starts1: Set[int]
  const Starts2: Set[int]
  const Drainers: int
  // Whether the sweeper and the loader run, and a crashed drainer's request may land later; the three-drainer case
  // turns them off to stay finite in reach.
  const Sweeping: bool
  const Loading: bool
  const Stragglers: bool
  const Mutation: str

  // A tag is ledger * 10 + slot, so each ledger holds fewer than ten entries.
  assume EntriesFit = Ledger1.length() < 10 and Ledger2.length() < 10

  type Pos = { g: int, r: int, p: int }
  type Wal = { gen: int, rows: int, len: int, opened: bool, appends: List[int] }
  type Phase = Idle | Create | Unit | ReadRun | Base | WriteAhead | ReadOpen | Mark
  type Drainer = { phase: Phase, ledger: int, start: int, end: int, unit: int }
  type SweepPhase = SweepIdle | SweepListed | SweepRead
  type Sweeper = { phase: SweepPhase, listed: Set[int], current: int }
  type LoadPhase = LoadIdle | LoadFirst | LoadSecond
  type Loader = { phase: LoadPhase, listed: Set[int], pos: Pos }
  type State = {
    wal: Wal,
    // The checkpoints, by name: their contents, and which exist.
    bases: int -> List[int],
    baseExists: Set[int],
    // Every tag a request ever wrote into the log.
    landed: Set[int],
    // The drain markers of each ledger: (start, end).
    marks: int -> Set[(int, int)],
    // The ledger map: the ledgers it lists, and whether Ledger2 is closed.
    listed: Set[int],
    closed2: bool,
    // The decided prefix of Ledger2.
    decided2: int,
    drainers: int -> Drainer,
    // The requests of crashed drainers still in flight, each of which may land once.
    strays: Set[Drainer],
    sweeper: Sweeper,
    loader: Loader,
    // Whether a load took a version which names another state of the key than its state when the load took it.
    badLoad: bool,
    // Whether a read answered content other than the key's state at its cut.
    badRead: bool,
    // Whether a drain blocked, as corruption: a missing predecessor, or a log which contradicts its write.
    blocked: bool,
  }

  pure val Ledgers = Set(1, 2)
  pure val Origin: Pos = { g: 0, r: 0, p: 0 }
  pure val MaxGen = 6
  pure def entries(i: int): List[str] = if (i == 1) Ledger1 else Ledger2
  pure def recordedT(i: int): int = entries(i).length()
  pure def starts(i: int): Set[int] = if (i == 1) Starts1 else Starts2
  pure val MaxT = if (recordedT(1) > recordedT(2)) recordedT(1) else recordedT(2)
  pure def tag(i: int, p: int): int = i * 10 + p
  pure def isK(e: str): bool = e == "A" or e == "R" or e == "E"

  // The end of the segment which starts at a: the next segment's start, or T.
  pure def segEnd(i: int, a: int): int = starts(i).fold(recordedT(i), (e, x) => if (x > a and x < e) x else e)
  // The start of the segment which holds slot c.
  pure def segStart(i: int, c: int): int = starts(i).fold(0, (m, x) => if (x <= c and x > m) x else m)
  pure def realSegs(i: int): List[int] = range(0, recordedT(i)).select(p => starts(i).contains(p))
  // Whether [a, b) holds a row of any key: a segment of markers alone holds none.
  pure def hasRows(i: int, a: int, b: int): bool = range(a, b).select(p => entries(i)[p] != "M").length() > 0

  // The key's write of [a, b): its rows from the last opener on, or every Append, and the rows the opener supersedes.
  pure def kSlots(i: int, a: int, b: int): List[int] = range(a, b).select(p => isK(entries(i)[p]))
  pure def openerSlot(i: int, a: int, b: int): int = kSlots(i, a, b).foldl(-1, (m, p) => if (entries(i)[p] != "A") p else m)
  pure def cmds(i: int, a: int, b: int): List[int] =
    val o = openerSlot(i, a, b)
    if (o < 0) kSlots(i, a, b) else kSlots(i, a, b).select(p => p >= o)
  pure def superseded(i: int, a: int, b: int): int =
    val o = openerSlot(i, a, b)
    if (o < 0) 0 else kSlots(i, a, b).select(p => p < o).length()
  pure def applyK(c: List[int], i: int, p: int): List[int] =
    if (entries(i)[p] == "A") c.append(tag(i, p)) else if (entries(i)[p] == "R") [tag(i, p)] else []
  pure def foldK(c: List[int], i: int, slots: List[int]): List[int] = slots.foldl(c, (x, p) => applyK(x, i, p))

  // Plan: the position after a write from pos (archive-logs.md §2.2, §7.2): an opener's generation counts the rows its base
  // folds, those it superseded among them, so two states of a segment have distinct tokens.
  pure def baseRows(i: int, a: int, b: int): int = superseded(i, a, b) + cmds(i, a, b).length()
  pure def advance(pos: Pos, i: int, a: int, b: int): Pos =
    val n = cmds(i, a, b).length()
    if (n == 0) pos
    else if (openerSlot(i, a, b) >= 0) { g: pos.g + 1, r: baseRows(i, a, b), p: 0 }
    else if (pos.g == 0) { g: 1, r: 0, p: n }
    else { ...pos, p: pos.p + n }
  pure val Chain: List[(int, int)] =
    realSegs(1).foldl([], (l, a) => l.append((1, a))).concat(realSegs(2).foldl([], (l, a) => l.append((2, a))))
  // Each segment's token: the position the key's previous write leaves (JS-I10).
  pure val Tokens: (int, int) -> Pos =
    Chain.foldl({ pos: Origin, m: Map() }, (acc, x) => {
      pos: advance(acc.pos, x._1, x._2, segEnd(x._1, x._2)),
      m: acc.m.put(x, acc.pos),
    }).m
  // A write's token is its segment's, which every row of the key in the segment stores.
  pure def tokenOf(i: int, c: int): Pos = Tokens.get((i, segStart(i, c)))
  pure def runG(t: Pos): int = if (t.g == 0) 1 else t.g
  pure def runP(t: Pos): int = if (t.g == 0) 0 else t.p
  pure def runR(t: Pos): int = if (t.g == 0) 0 else t.r

  // The content the chain's replay leaves at each position it passes: Origin, every block boundary of a run, and every
  // opener's new generation.
  type Replay = { pos: Pos, content: List[int], m: Pos -> List[int] }
  pure def runPointsFrom(r: Replay, i: int, c: List[int], at: Pos): Replay =
    range(0, c.length()).foldl(
      { pos: at, content: r.content, m: r.m.put(at, r.content) },
      (x, j) => {
        pos: { ...at, p: at.p + j + 1 },
        content: x.content.append(tag(i, c[j])),
        m: x.m.put({ ...at, p: at.p + j + 1 }, x.content.append(tag(i, c[j]))),
      })
  pure def replayStep(r: Replay, i: int, a: int): Replay =
    val b = segEnd(i, a)
    val c = cmds(i, a, b)
    if (c.length() == 0) r
    else if (openerSlot(i, a, b) >= 0)
      val at = advance(r.pos, i, a, b)
      { pos: at, content: foldK(List(), i, c), m: r.m.put(at, foldK(List(), i, c)) }
    else runPointsFrom(r, i, c, { g: runG(r.pos), r: runR(r.pos), p: runP(r.pos) })
  pure val Points: Pos -> List[int] =
    Chain.foldl({ pos: Origin, content: List(), m: Map(Origin -> List()) }, (r, x) => replayStep(r, x._1, x._2)).m

  pure def posLeq(x: Pos, y: Pos): bool = x.g < y.g or (x.g == y.g and x.p <= y.p)

  // Holds (§8.3): whether the position holds the first j commands of the write of [a, b). The first-run-as-opener mutant
  // judges a key's first run as an opener.
  pure def holds(pos: Pos, i: int, a: int, b: int, j: int): bool =
    val t = tokenOf(i, a)
    if (j == 0) true
    else if (openerSlot(i, a, b) >= 0 or (Mutation == "first-run-as-opener" and t.g == 0)) pos.g >= t.g + 1
    else pos.g > runG(t) or (pos.g == runG(t) and pos.p >= runP(t) + j)
  // The position after the first j commands of the write of [a, b), as the chain gives it.
  pure def chainPoint(i: int, a: int, b: int, j: int): Pos =
    val t = tokenOf(i, a)
    if (openerSlot(i, a, b) >= 0) { g: t.g + 1, r: baseRows(i, a, b), p: 0 } else { g: runG(t), r: runR(t), p: runP(t) + j }
  // The index, from 1, of a row's command in its write; a row the opener supersedes is held with the opener.
  pure def indexIn(i: int, a: int, b: int, p: int): int =
    val o = openerSlot(i, a, b)
    if (p < o) 1
    else cmds(i, a, b).foldl({ k: 0, at: 0 }, (r, q) => if (q == p) { k: r.k + 1, at: r.k + 1 } else { k: r.k + 1, at: r.at }).at

  // Checkpoint names. The checkpoint-overwrite mutant names one checkpoint a log, which each opener overwrites.
  pure def bkey(g: int): int = if (Mutation == "checkpoint-overwrite") 0 else g
  // A generation's name without its zero padding, as base-3 digits, so generation 3 ("10") sorts below 2 ("2").
  pure def digits3(g: int): List[int] = if (g < 3) [g] else [g / 3, g % 3]
  pure def lexLess(x: List[int], y: List[int]): bool =
    range(0, 3).foldl({ done: false, less: false }, (r, k) =>
      if (r.done) r
      else if (k >= x.length() and k >= y.length()) { done: true, less: false }
      else if (k >= x.length()) { done: true, less: true }
      else if (k >= y.length()) { done: true, less: false }
      else if (x[k] < y[k]) { done: true, less: true }
      else if (x[k] > y[k]) { done: true, less: false }
      else r).less

  pure val absentWal: Wal = { gen: 0, rows: 0, len: 0, opened: false, appends: List() }
  pure val idleDrainer: Drainer = { phase: Idle, ledger: 1, start: 0, end: 0, unit: 0 }
  pure val idleSweeper: Sweeper = { phase: SweepIdle, listed: Set(), current: 0 }
  pure val idleLoader: Loader = { phase: LoadIdle, listed: Set(), pos: Origin }
  // The phases which issue a request, which may land after its drainer crashed.
  pure def requests(phase: Phase): bool =
    phase == Create or phase == Unit or phase == Base or phase == WriteAhead or phase == Mark

  var s: State

  def closed(i: int): bool = i == 1 or s.closed2
  def decided(i: int): int = if (i == 1) recordedT(1) else s.decided2
  def position: Pos = { g: s.wal.gen, r: s.wal.rows, p: s.wal.len }
  def content: List[int] =
    (if (s.wal.opened and s.baseExists.contains(bkey(s.wal.gen))) s.bases.get(bkey(s.wal.gen)) else List()).concat(s.wal.appends)

  // The key's decided rows, in tag order, and their fold; through a cut of Ledger2.
  def decidedBelow(cut2: int): List[(int, int)] =
    range(0, decided(1)).select(p => isK(entries(1)[p])).foldl(List(), (l, p) => l.append((1, p)))
      .concat(range(0, cut2).select(p => isK(entries(2)[p])).foldl(List(), (l, p) => l.append((2, p))))
  def decidedK: List[(int, int)] = decidedBelow(decided(2))
  def truth: List[int] = decidedK.foldl(List(), (c, x) => applyK(c, x._1, x._2))

  // Whether a decided row of a listed ledger is held, by a reader which sees its ledger below cut: its write, as the
  // reader computes it from the rows of its segment below the cut, holds its command.
  def heldRowAt(pos: Pos, i: int, p: int, cut: int): bool =
    val a = segStart(i, p)
    val b = if (segEnd(i, a) < cut) segEnd(i, a) else cut
    holds(pos, i, a, b, indexIn(i, a, b, p))

  // A reader's view: the log, then every decided row of a listed ledger it does not hold, in tag order.
  def viewOf(pos: Pos, c: List[int], listed: Set[int]): List[int] =
    decidedK.foldl(c, (x, r) => if (listed.contains(r._1) and not(heldRowAt(pos, r._1, r._2, decided(r._1)))) applyK(x, r._1, r._2) else x)

  // The highest marker's end, from which a drain resumes; a drain passes the segments of markers alone after it.
  def resume(i: int): int =
    val top = s.marks.get(i).fold((-1, 0), (m, x) => if (x._1 > m._1) x else m)
    if (top._1 < 0) 0 else top._2
  def resumable(i: int, a: int): bool =
    a == resume(i) or (starts(i).contains(a) and a > resume(i) and not(hasRows(i, resume(i), a)))
  def marked(i: int, a: int): bool = s.marks.get(i).exists(x => x._1 == a)
  // LOG-I3: the drain answers Complete once its ledger is closed and its resume point, the highest marker's end, passes
  // every segment which holds rows; the complete-early mutant answers it at the last such segment's start.
  def lastRowSegment(i: int): int = realSegs(i).foldl(-1, (m, a) => if (hasRows(i, a, segEnd(i, a))) a else m)
  def complete(i: int): bool = closed(i) and or {
    resume(i) >= recordedT(i),
    not(hasRows(i, resume(i), recordedT(i))),
    Mutation == "complete-early" and resume(i) == lastRowSegment(i),
  }

  def setDrainer(d: int, x: Drainer): State = { ...s, drainers: s.drainers.set(d, x) }

  action init = s' = {
    wal: absentWal,
    bases: 0.to(MaxGen).mapBy(_ => List()),
    baseExists: Set(),
    landed: Set(),
    marks: Ledgers.mapBy(_ => Set()),
    listed: Ledgers,
    closed2: false,
    decided2: if (recordedT(2) > 0) 1 else 0,
    drainers: 1.to(Drainers).mapBy(_ => idleDrainer),
    strays: Set(),
    sweeper: idleSweeper,
    loader: idleLoader,
    badLoad: false,
    badRead: false,
    blocked: false,
  }

  // ---------------------------------------------------------------------------
  // Drains (§8.1). A drain of ledger i runs only while no older ledger is listed, starts at the highest marker's end,
  // and takes one whole segment closed below the decided prefix. A worker goes on to its ledger's next segments without
  // reading the map again, so it may start one of a ledger another drain completed and dropped meanwhile. The
  // segments-out-of-order mutant starts at any unmarked segment; the partial-segment-drain mutant takes a part of a
  // segment; the drain-past-tail mutant takes a segment not yet closed by a decided start; the drain-ignores-lineage
  // mutant drains Ledger2 while Ledger1 is listed.
  // ---------------------------------------------------------------------------
  // A drain resumes at the highest marker's end, or at an end its worker reached before, which may lie below it once
  // another drain went further: a complete segment, whose writes it finds held.
  def startOk(i: int, a: int): bool =
    if (Mutation == "segments-out-of-order") starts(i).contains(a) and a >= resume(i) and not(marked(i, a))
    else resumable(i, a) or (starts(i).contains(a) and a < resume(i))

  def endOk(i: int, a: int, b: int): bool =
    if (Mutation == "partial-segment-drain") a < b and b <= segEnd(i, segStart(i, a)) and b <= decided(i)
    else if (Mutation == "drain-past-tail") b == segEnd(i, a)
    else b == segEnd(i, a) and (b < decided(i) or (closed(i) and b == recordedT(i)))

  def firstPhase(i: int, a: int, b: int): Phase =
    if (cmds(i, a, b).length() == 0) Mark
    else if (openerSlot(i, a, b) >= 0) (if (Mutation == "wal-before-checkpoint") WriteAhead else Base)
    else if (tokenOf(i, a).g == 0) Create
    else Unit

  action drainStart(d: int, i: int, a: int, b: int): bool = all {
    s.drainers.get(d).phase == Idle,
    i == 1 or not(s.listed.contains(1)) or Mutation == "drain-ignores-lineage",
    a < recordedT(i),
    startOk(i, a),
    endOk(i, a, b),
    s' = setDrainer(d, { phase: firstPhase(i, a, b), ledger: i, start: a, end: b, unit: 0 }),
  }

  // Each request's effect on the log, which a drainer's step and a crashed drainer's straggler share: a first run creates
  // generation 1 only when the log is absent; an Append Block lands only while the log is at the run's generation and
  // offset; an opener's checkpoint is created only when absent; its write-ahead object only while the log is at the
  // generation it starts from; and a marker only once the segment holds rows. The append-without-*-fence mutants drop a
  // condition of the Append Block; the checkpoint-overwrite mutant overwrites; the replace-over-newer mutant drops the
  // write-ahead object's condition; the delete-removes-log mutant deletes the log for a Delete.
  def createLands(x: Drainer): bool = s.wal.gen == 0
  def unitLands(x: Drainer): bool =
    val t = tokenOf(x.ledger, x.start)
    and {
      s.wal.gen > 0,
      Mutation == "append-without-generation-fence" or s.wal.gen == runG(t),
      Mutation == "append-without-offset-fence" or s.wal.len == runP(t) + x.unit,
    }
  def baseOf(x: Drainer): List[int] = foldK(List(), x.ledger, cmds(x.ledger, x.start, x.end))
  def baseLands(x: Drainer): bool = not(s.baseExists.contains(bkey(tokenOf(x.ledger, x.start).g + 1))) or Mutation == "checkpoint-overwrite"
  def walLands(x: Drainer): bool = Mutation == "replace-over-newer" or s.wal.gen == tokenOf(x.ledger, x.start).g

  def landRequest(x: Drainer): State =
    val t = tokenOf(x.ledger, x.start)
    val c = cmds(x.ledger, x.start, x.end)
    if (x.phase == Create)
      if (createLands(x)) { ...s, wal: { ...absentWal, gen: 1 } } else s
    else if (x.phase == Unit)
      if (unitLands(x))
        { ...s, wal: { ...s.wal, len: s.wal.len + 1, appends: s.wal.appends.append(tag(x.ledger, c[x.unit])) }, landed: s.landed.union(Set(tag(x.ledger, c[x.unit]))) }
      else s
    else if (x.phase == Base)
      if (baseLands(x))
        {
          ...s,
          bases: s.bases.set(bkey(t.g + 1), baseOf(x)),
          baseExists: s.baseExists.union(Set(bkey(t.g + 1))),
          landed: s.landed.union(c.foldl(Set(), (l, p) => l.union(Set(tag(x.ledger, p))))),
        }
      else s
    else if (x.phase == WriteAhead)
      if (walLands(x))
        if (Mutation == "delete-removes-log" and entries(x.ledger)[openerSlot(x.ledger, x.start, x.end)] == "E") { ...s, wal: absentWal }
        else { ...s, wal: { gen: t.g + 1, rows: baseRows(x.ledger, x.start, x.end), len: 0, opened: true, appends: List() } }
      else s
    else if (x.phase == Mark)
      if (hasRows(x.ledger, x.start, x.end)) { ...s, marks: s.marks.set(x.ledger, s.marks.get(x.ledger).union(Set((x.start, x.end)))) } else s
    else s

  // A first run's Create; an existing log goes to the append, whose own condition decides.
  action createStep(d: int): bool =
    val x = s.drainers.get(d)
    all { x.phase == Create, s' = { ...landRequest(x), drainers: s.drainers.set(d, { ...x, phase: Unit }) } }

  // One Append Block. The 404-as-held mutant takes a failed condition as held, without the properties read.
  action unitStep(d: int): bool =
    val x = s.drainers.get(d)
    val n = cmds(x.ledger, x.start, x.end).length()
    all {
      x.phase == Unit,
      if (unitLands(x)) s' = { ...landRequest(x), drainers: s.drainers.set(d, if (x.unit + 1 == n) { ...x, phase: Mark } else { ...x, unit: x.unit + 1 }) }
      else s' = setDrainer(d, { ...x, phase: if (Mutation == "404-as-held-without-properties-read") Mark else ReadRun }),
    }

  // §8.3's run table: Held, Resume(u), or, for an absent or older log, a predecessor missing or a contradiction, which
  // block the drain.
  action readRun(d: int): bool =
    val x = s.drainers.get(d)
    val t = tokenOf(x.ledger, x.start)
    val n = cmds(x.ledger, x.start, x.end).length()
    val g = runG(t)
    val p0 = runP(t)
    all {
      x.phase == ReadRun,
      if (s.wal.gen == 0 or (t.g == 0 and s.wal.gen == 1 and s.wal.opened)) s' = { ...setDrainer(d, idleDrainer), blocked: true }
      else if (s.wal.gen > g or (s.wal.gen == g and s.wal.len >= p0 + n)) s' = setDrainer(d, { ...x, phase: Mark })
      else if (s.wal.gen == g and s.wal.len >= p0) s' = setDrainer(d, { ...x, phase: Unit, unit: s.wal.len - p0 })
      else s' = { ...setDrainer(d, idleDrainer), blocked: true },
    }

  // An opener's checkpoint; one which exists must hold the same base, or the log contradicts the write.
  action baseStep(d: int): bool =
    val x = s.drainers.get(d)
    val following = if (Mutation == "wal-before-checkpoint") Mark else WriteAhead
    all {
      x.phase == Base,
      if (baseLands(x)) s' = { ...landRequest(x), drainers: s.drainers.set(d, { ...x, phase: following }) }
      else if (s.bases.get(bkey(tokenOf(x.ledger, x.start).g + 1)) == baseOf(x)) s' = setDrainer(d, { ...x, phase: following })
      else s' = { ...setDrainer(d, idleDrainer), blocked: true },
    }

  // An opener's new generation.
  action walStep(d: int): bool =
    val x = s.drainers.get(d)
    val following = if (Mutation == "wal-before-checkpoint") Base else Mark
    all {
      x.phase == WriteAhead,
      if (walLands(x)) s' = { ...landRequest(x), drainers: s.drainers.set(d, { ...x, phase: following }) }
      else s' = setDrainer(d, { ...x, phase: if (Mutation == "404-as-held-without-properties-read") Mark else ReadOpen }),
    }

  // §8.3's opener table: Held, Retry at the opener's own start, or a block.
  action readOpen(d: int): bool =
    val x = s.drainers.get(d)
    val t = tokenOf(x.ledger, x.start)
    all {
      x.phase == ReadOpen,
      if (s.wal.gen >= t.g + 1) s' = setDrainer(d, { ...x, phase: Mark })
      else if (s.wal.gen == t.g and s.wal.gen > 0 and s.wal.len == t.p) s' = setDrainer(d, { ...x, phase: WriteAhead })
      else s' = { ...setDrainer(d, idleDrainer), blocked: true },
    }

  // The segment's marker, once the key's write is held; a segment of markers alone gets none.
  action markStep(d: int): bool =
    val x = s.drainers.get(d)
    all { x.phase == Mark, s' = { ...landRequest(x), drainers: s.drainers.set(d, idleDrainer) } }

  // A crash forgets the drain; a request in flight may land later, once, beside the restarted worker.
  action crash(d: int): bool =
    val x = s.drainers.get(d)
    all {
      x.phase != Idle,
      s' = { ...setDrainer(d, idleDrainer), strays: if (Stragglers and requests(x.phase) and s.strays.size() == 0) s.strays.union(Set(x)) else s.strays },
    }

  action strayLands(x: Drainer): bool = all {
    s.strays.contains(x),
    s' = { ...landRequest(x), strays: s.strays.exclude(Set(x)) },
  }

  action strayLost(x: Drainer): bool = all { s.strays.contains(x), s' = { ...s, strays: s.strays.exclude(Set(x)) } }

  // ---------------------------------------------------------------------------
  // Ledger2's batches are decided a segment at a time and it closes once every batch is; a complete ledger drops,
  // Ledger1 first (LG-I10). The wait-as-held mutant takes a drain of Ledger2 which waits on Ledger1 as Ledger1 held.
  // ---------------------------------------------------------------------------
  action decideStart: bool =
    val nextStart = segEnd(2, s.decided2 - 1)
    all {
      s.decided2 < recordedT(2),
      s' = { ...s, decided2: if (nextStart < recordedT(2)) nextStart + 1 else recordedT(2) },
    }

  action close2: bool = all { not(s.closed2), s.decided2 == recordedT(2), s' = { ...s, closed2: true } }

  action drop(i: int): bool = all {
    s.listed.contains(i),
    i == 1 or not(s.listed.contains(1)),
    complete(i) or (Mutation == "wait-as-held" and i == 1),
    s' = { ...s, listed: s.listed.exclude(Set(i)) },
  }

  // ---------------------------------------------------------------------------
  // The sweeper (§8.5): list the checkpoints, read the log's properties, delete the listed ones below the current
  // generation; and drop the markers of dropped ledgers. The gc-at-or-above-current mutant deletes at or above it; the
  // referenced-missing mutant takes the highest listed checkpoint as current, without the read; the no-zero-pad mutant
  // compares generations as their unpadded names.
  // ---------------------------------------------------------------------------
  action sweepList: bool = all {
    Sweeping,
    s.sweeper.phase == SweepIdle,
    s' = { ...s, sweeper: { phase: SweepListed, listed: s.baseExists, current: 0 } },
  }

  action sweepRead: bool =
    val highest = s.sweeper.listed.fold(0, (m, g) => if (g > m) g else m)
    all {
      s.sweeper.phase == SweepListed,
      s' = { ...s, sweeper: { ...s.sweeper, phase: SweepRead, current: if (Mutation == "referenced-missing") highest else s.wal.gen } },
    }

  def deletable(g: int, current: int): bool =
    if (Mutation == "gc-at-or-above-current") current > 0 and g >= current
    else if (Mutation == "no-zero-pad") current > 0 and lexLess(digits3(g), digits3(current))
    else g < current

  action sweepDelete(g: int): bool = all {
    s.sweeper.phase == SweepRead,
    s.sweeper.listed.contains(g),
    s.baseExists.contains(g),
    deletable(g, s.sweeper.current),
    s' = { ...s, baseExists: s.baseExists.exclude(Set(g)) },
  }

  action sweepDone: bool = all { s.sweeper.phase == SweepRead, s' = { ...s, sweeper: idleSweeper } }

  action sweepMarkers(i: int): bool = all {
    Sweeping,
    not(s.listed.contains(i)),
    s.marks.get(i) != Set(),
    s' = { ...s, marks: s.marks.set(i, Set()) },
  }

  // ---------------------------------------------------------------------------
  // A reader (archive.md §8.1): it reads the map, then the log, and scans each listed ledger from slot 0, Ledger2 below a
  // tail it learned, which may lag the decided prefix. It merges by Holds, each segment's write formed from the rows it
  // saw, and reads again when the prefix lies past the last write it met which the prefix holds whole: Plan of that write
  // is not the prefix's position. Otherwise a read whose tail is the decided prefix, a linearizable read, answers the
  // key's state at its cut exactly; a read whose tail lags answers a real prefix of the history at least through its
  // cut, and possibly past it, when a drain from a later cut is already held (archive.md §2.3). The
  // lagging-read-as-linearizable mutant holds a lagging read to its cut, as one which reported itself linearizable.
  // ---------------------------------------------------------------------------
  def cutOf(i: int, seen: int): int = if (i == 1) decided(1) else seen
  def readerWrites(listed: Set[int], seen: int): List[(int, int, int)] =
    Chain.foldl([], (l, x) =>
      val cut = cutOf(x._1, seen)
      val b = if (segEnd(x._1, x._2) < cut) segEnd(x._1, x._2) else cut
      if (listed.contains(x._1) and x._2 < cut and cmds(x._1, x._2, b).length() > 0) l.append((x._1, x._2, b)) else l)
  def pastEnd(listed: Set[int], seen: int): bool =
    val writes = readerWrites(listed, seen)
    val last = writes.foldl({ held: false, plan: Origin }, (r, w) =>
      if (holds(position, w._1, w._2, w._3, cmds(w._1, w._2, w._3).length())) { held: true, plan: advance(tokenOf(w._1, w._2), w._1, w._2, w._3) }
      else { held: false, plan: Origin })
    last.held and last.plan != position
  def answer(listed: Set[int], seen: int): List[int] =
    decidedBelow(seen).foldl(content, (x, r) =>
      if (listed.contains(r._1) and not(heldRowAt(position, r._1, r._2, cutOf(r._1, seen)))) applyK(x, r._1, r._2) else x)
  def truthAt(seen: int): List[int] = decidedBelow(seen).foldl(List(), (c, x) => applyK(c, x._1, x._2))
  def answersItsCut(listed: Set[int], seen: int): bool =
    val a = answer(listed, seen)
    if (seen == s.decided2 or Mutation == "lagging-read-as-linearizable") a == truthAt(seen) else seen.to(s.decided2).exists(c => a == truthAt(c))

  action read(seen: int): bool = all {
    seen <= s.decided2,
    s' = { ...s, badRead: s.badRead or (not(pastEnd(s.listed, seen)) and not(answersItsCut(s.listed, seen))) },
  }

  // ---------------------------------------------------------------------------
  // Journals' loader (journals.md §8.3): it reads the map, then the log, and takes the version of its last live row, or,
  // with none, rebases to the log's position. The rebase-from-map-after-archive mutant reads the log first; the
  // rebase-with-live-rows mutant rebases over live rows; the rebase-on-stale-token mutant rebases to the token the key's
  // last written row stores, the start of its last write, rather than the log's position.
  // ---------------------------------------------------------------------------
  def readMap(x: Loader): Loader = { ...x, listed: s.listed }
  def readLog(x: Loader): Loader = { ...x, pos: position }

  action loadFirst: bool = all {
    Loading,
    s.loader.phase == LoadIdle,
    s' = { ...s, loader: { ...(if (Mutation == "rebase-from-map-after-archive") readLog(idleLoader) else readMap(idleLoader)), phase: LoadFirst } },
  }

  action loadSecond: bool = all {
    s.loader.phase == LoadFirst,
    s' = { ...s, loader: { ...(if (Mutation == "rebase-from-map-after-archive") readMap(s.loader) else readLog(s.loader)), phase: LoadSecond } },
  }

  def lastWriteToken(listed: Set[int]): Pos =
    Chain.foldl(Origin, (t, x) => if (not(listed.contains(x._1)) and cmds(x._1, x._2, segEnd(x._1, x._2)).length() > 0) tokenOf(x._1, x._2) else t)

  // The state a version names: a row's version the fold of the key's rows through that row; a rebase's, (VersionBase,
  // 0), the content the chain's replay leaves at the position its base encodes (§7.2), and none at a position the chain
  // never passes.
  def namedState(live: List[(int, int)], rebaseTo: Pos): List[int] =
    if (live.length() > 0 and Mutation != "rebase-with-live-rows")
      val last = tag(live[live.length() - 1]._1, live[live.length() - 1]._2)
      decidedK.foldl(List(), (c, r) => if (tag(r._1, r._2) <= last) applyK(c, r._1, r._2) else c)
    else if (Points.keys().contains(rebaseTo)) Points.get(rebaseTo)
    else [-1]

  action loadFinish: bool =
    val x = s.loader
    val live = decidedK.select(r => x.listed.contains(r._1))
    val rebaseTo = if (Mutation == "rebase-on-stale-token") lastWriteToken(x.listed) else x.pos
    all {
      x.phase == LoadSecond,
      s' = { ...s, loader: idleLoader, badLoad: s.badLoad or namedState(live, rebaseTo) != truth },
    }

  // Each action draws only the parameters it takes, so no successor is generated once for each value of another.
  action drainerStep =
    nondet d = 1.to(Drainers).oneOf()
    any { createStep(d), unitStep(d), readRun(d), baseStep(d), walStep(d), readOpen(d), markStep(d), crash(d) }

  action startStep =
    nondet d = 1.to(Drainers).oneOf()
    nondet i = Ledgers.oneOf()
    nondet a = 0.to(MaxT).oneOf()
    nondet b = 0.to(MaxT).oneOf()
    drainStart(d, i, a, b)

  action strayStep = all {
    s.strays != Set(),
    nondet x = s.strays.oneOf()
    any { strayLands(x), strayLost(x) },
  }

  action ledgerStep =
    nondet i = Ledgers.oneOf()
    any { drop(i), sweepMarkers(i) }

  action deleteStep =
    nondet g = 0.to(MaxGen).oneOf()
    sweepDelete(g)

  action readStep =
    nondet seen = 0.to(MaxT).oneOf()
    read(seen)

  action step = any {
    drainerStep, startStep, strayStep, ledgerStep, deleteStep, readStep,
    decideStart, close2, sweepList, sweepRead, sweepDone, loadFirst, loadSecond, loadFinish,
  }

  // ---------------------------------------------------------------------------
  // Invariants
  // ---------------------------------------------------------------------------
  // HI-I1, LOG-I1 to LOG-I3: the log followed by the listed ledgers' decided rows it does not hold folds to the key's
  // decided history, so no command is lost and none applies twice.
  val NoLostCommand = viewOf(position, content, s.listed) == truth

  // HI-I1 on a stale map: a reader whose map still lists ledgers since dropped, any superset of the listed ones, scans them
  // too, and the merge by Holds marks their rows held, so its view is the same history.
  val StaleReaders = Ledgers.powerset().forall(stale => not(s.listed.subseteq(stale)) or viewOf(position, content, stale) == truth)

  // HI-I1: a linearizable read answers the key's state at its cut; a read whose tail lags, a real prefix through at least its cut.
  val ReadsAtTheirCut = not(s.badRead)

  // The log holds only decided commands: a drain takes a segment only once a decided start closes it.
  val NoUndecidedCommand = s.landed.forall(t => t % 10 < decided(t / 10))

  // LOG-I1, LOG-I2: the log's position is one the chain passes, with the content the chain's replay gives there, and
  // Holds claims of no listed write a command the position has not reached.
  val HoldsSound = and {
    s.wal.gen == 0 or (Points.keys().contains(position) and Points.get(position) == content),
    s.listed.forall(i => realSegs(i).foldl(true, (ok, a) =>
      val b = segEnd(i, a)
      ok and range(1, cmds(i, a, b).length() + 1).foldl(true, (ok2, j) =>
        ok2 and (not(holds(position, i, a, b, j)) or posLeq(chainPoint(i, a, b, j), position))))),
  }

  // LOG-I2: the write-ahead object names a checkpoint only once it exists, and the sweeper never deletes it.
  val ReferencedExist = not(s.wal.gen > 0 and s.wal.opened) or s.baseExists.contains(bkey(s.wal.gen))

  // HI-I4, JS-I10: every version a load takes names the key's state when it takes it, so no version names two states.
  val VersionsUnique = not(s.badLoad)

  // The lineage rule makes a missing predecessor mean corruption: no conforming run blocks a drain.
  val NoFalseCorruption = not(s.blocked)
}
