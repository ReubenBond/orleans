// -*- mode: Bluespec; -*-
/**
 * The ledger map and per-key history
 * (test/Durable.Storage/docs/spec/components/ledgers.md §5, §8; archive-lsm.md §2.5, §8.3).
 *
 * Keys are KeyBits-bit integers, and every ledger owns a dyadic range: the
 * keys whose top `depth` bits equal `prefix`. One conditional ledger-map write
 * performs each transition: a takeover (or handoff, replica change, or roll)
 * closes a ledger and opens its successor over the same range, a split closes
 * a ledger and opens its two children, and a merge closes two siblings and
  * opens their parent. Each write closes ledgers a recovery sealed, takes the new
 * ledgers' IDs from next_ledger_id, and closes every open ledger the new
 * ones overlap. Ledgers carry no predecessor links.
 *
 * An append lands atomically at its ledger's next slot; the seal and recovery
 * which make landing and closing exclusive are checked by test/Durable.Storage/specs/ledger.
 * A ledger's primary admits an entry for key k iff its JSN is one past the
 * fold of k's history over the ledgers whose IDs are at most its own. A
  * ledger's primary may outlive its closing; only the seal stops its appends.
 *
 * Each batch holds one row, except a marker, which holds none. A ledger's
 * batches form segments (ledgers §2.2, §7, §8.2): its first batch, or a batch
 * the writer flags, starts one, as a marker does once the open segment holds
 * rows, so a segment may hold only its marker. The writer's dictionary of the
 * open segment's keys makes a key's first row in a segment its defining row
 * and the later ones referencing rows. Each defining row carries the key's
 * token for its segment:
 * the empty Origin for the key's first segment, and otherwise Plan of the
 * key's previous segment's token and its last write there, which a token
 * models as the set of those writes' JSNs (JS-I10). A trim falls at a segment
 * start at or below the watermark, so a stored segment is whole. A batch the
 * writer creates and disposes unproposed abandons the writer (LG-I12), and a
 * write a sealed ledger fenced lands nowhere; both only stop appends, which
 * the model's nondeterminism already includes, so only their mutants, which go
 * on writing or remember the fenced write, model them.
 *
 * The Archive is reduced to drains: a drain copies a ledger's entries in
 * [watermark, b) into the Archive and raises the watermark, the live ledgers are trimmed below
 * the watermark, and a closed ledger whose watermark reached its recorded T
 * leaves the map once no listed ledger with a lower ID overlaps it, so each
 * key's ledgers leave oldest first. A reader snapshots the ledger map, then
 * the manifest, then reads the live ledgers, and retries when a ledger it needs was
 * dropped or trimmed past its snapshot.
 */
module LedgerMap {
  const KeyBits: int
  const MaxLedgers: int
  const MaxJsn: int
  // The markers a run writes, across every ledger, which bounds the
  // interleavings their slots add.
  const MaxMarkers: int
  const Mutation: str

  type Status = Open | Closed
  // A ledger's tag is its ID, which its rows carry; it equals the ledger's
  // index in ledgers except under reuse-id.
  type Ledger = { live: bool, tag: int, prefix: int, depth: int, status: Status, sealed: bool, tail: int }
  // A row's segment is its segment's start slot, and its token the key's token
  // for that segment, which only the defining row carries in the format.
  type Row = { k: int, tag: int, slot: int, jsn: int, seg: int, defining: bool, token: Set[int] }
  type Reader = {
    phase: int,
    hasMap: bool,
    mapSnap: Set[int],
    wSnap: int -> int,
    archiveSnap: Set[Row],
    before: Set[Row],
  }
  type State = {
    ledgers: int -> Ledger,
    next: int,
    // The live ledgers: each ledger's stored rows, its next slot, its trimmed floor, and
    // whether its replicas were dropped.
    rows: int -> Set[Row],
    len: int -> int,
    floor: int -> int,
    gone: int -> bool,
    // Each ledger's segments: their start slots, the open one's, and the
    // writer's dictionary of the keys it defined; the markers written; and,
    // under the token-includes-fenced mutant, the writes sealed ledgers fenced.
    starts: int -> Set[int],
    segStart: int -> int,
    dict: int -> Set[int],
    markers: int,
    fenced: Set[{ k: int, jsn: int }],
    // The Archive: each ledger's watermark and the drained rows.
    watermark: int -> int,
    archive: Set[Row],
    acked: Set[Row],
    reader: Reader,
    staleRead: bool,
  }

  pure def pow2(n: int): int = 1.to(n).fold(1, (p, _) => p * 2)
  pure val Keys = 0.to(pow2(KeyBits) - 1)
  pure val Ids = 1.to(MaxLedgers)
  pure val Slots = 1.to(MaxJsn * pow2(KeyBits) + MaxMarkers)
  pure val noLedger: Ledger = { live: false, tag: 0, prefix: 0, depth: 0, status: Closed, sealed: true, tail: 0 }
  pure val idleReader: Reader = {
    phase: 0, hasMap: false, mapSnap: Set(), wSnap: Ids.mapBy(_ => 0), archiveSnap: Set(), before: Set(),
  }

  var s: State

  action init = s' = {
    ledgers: Ids.mapBy(i => if (i == 1) { ...noLedger, live: true, tag: 1, status: Open, sealed: false } else noLedger),
    next: 2,
    rows: Ids.mapBy(_ => Set()),
    len: Ids.mapBy(_ => 0),
    floor: Ids.mapBy(_ => 0),
    gone: Ids.mapBy(_ => false),
    starts: Ids.mapBy(_ => Set(0)),
    segStart: Ids.mapBy(_ => 0),
    dict: Ids.mapBy(_ => Set()),
    markers: 0,
    fenced: Set(),
    watermark: Ids.mapBy(_ => 0),
    archive: Set(),
    acked: Set(),
    reader: idleReader,
    staleRead: false,
  }

  pure def covers(g: Ledger, k: int): bool = k / pow2(KeyBits - g.depth) == g.prefix

  pure def overlaps(a: Ledger, b: Ledger): bool = Keys.exists(k => covers(a, k) and covers(b, k))

  def liveIds(t: State): Set[int] = Ids.filter(i => t.ledgers.get(i).live)

  // History(k): the drained rows, and each live ledger's rows at or above its
  // watermark.
  def history(t: State, k: int): Set[Row] =
    t.archive.union(liveIds(t).map(i => t.rows.get(i).filter(r => r.slot >= t.watermark.get(i))).flatten())
      .filter(r => r.k == k)

  pure def maxJsn(rs: Set[Row]): int = rs.fold(0, (m, r) => if (r.jsn > m) r.jsn else m)

  // ---------------------------------------------------------------------------
  // Appends. The primary folds the history of every ledger up to its own; the
  // own-ledger-only mutant folds only its own ledger, skipping the catch-up.
  // A primary appends only to a ledger the map lists: the transition's map
  // write installed it (ledgers §8.1, §8.5). The write-before-install mutant
  // appends to the ID the next transition will take, next_ledger_id, before
  // any map write installs it, as a primary which acts on a predicted ID, or on
  // an install whose outcome is unknown, does.
  // ---------------------------------------------------------------------------
  def ledger(i: int): Ledger =
    if (Mutation == "write-before-install" and i == s.next) opened(i, 0, 0) else s.ledgers.get(i)

  def known(i: int, k: int): Set[Row] =
    if (Mutation == "own-ledger-only") s.rows.get(i).filter(r => r.k == k)
    else history(s, k).filter(r => r.tag <= ledger(i).tag)

  // The token a defining row of k carries: Plan of the token of the key's
  // previous segment and its last write there, the key's latest row, or Origin
  // for its first segment. The token-from-wrong-parent mutant extends the
  // latest row of the writer's own ledger alone, and the token-includes-fenced
  // mutant adds the key's fenced writes.
  def definedToken(i: int, k: int): Set[int] =
    val rows = if (Mutation == "token-from-wrong-parent") known(i, k).filter(r => r.tag == ledger(i).tag) else known(i, k)
    val planned = rows.filter(r => r.jsn == maxJsn(rows)).fold(Set(), (t, r) => r.token.union(Set(r.jsn)))
    if (Mutation == "token-includes-fenced") planned.union(s.fenced.filter(f => f.k == k).map(f => f.jsn)) else planned

  // A referencing row's token is its segment's, which its defining row carries.
  def latestToken(i: int, k: int): Set[int] =
    known(i, k).filter(r => r.jsn == maxJsn(known(i, k))).fold(Set(), (t, r) => r.token)

  // A write's batch continues the open segment, or, flagged, starts one once
  // the open segment holds rows.
  action write(i: int, k: int, flagged: bool): bool =
    val g = ledger(i)
    val slot = s.len.get(i)
    val seg = if (flagged) slot else s.segStart.get(i)
    val defines = flagged or not(s.dict.get(i).contains(k))
    val row = {
      k: k, tag: g.tag, slot: slot, jsn: maxJsn(known(i, k)) + 1, seg: seg, defining: defines,
      token: if (defines) definedToken(i, k) else latestToken(i, k),
    }
    all {
      g.live, not(g.sealed), covers(g, k),
      row.jsn <= MaxJsn,
      not(flagged) or s.dict.get(i) != Set(),
      s' = {
        ...s,
        rows: s.rows.set(i, s.rows.get(i).union(Set(row))),
        len: s.len.set(i, slot + 1),
        acked: s.acked.union(Set(row)),
        starts: s.starts.set(i, s.starts.get(i).union(Set(seg))),
        segStart: s.segStart.set(i, seg),
        dict: s.dict.set(i, (if (flagged) Set() else s.dict.get(i)).union(Set(k))),
      },
    }

  // A marker, a batch of no rows, takes the next slot and starts a segment once
  // the open segment holds rows, as an aged segment's end or EndSegmentAsync
  // does (ledgers §2.4, §8.2); a segment of only its marker ends at no trigger.
  action marker(i: int): bool =
    val g = ledger(i)
    val slot = s.len.get(i)
    all {
      g.live, not(g.sealed), s.dict.get(i) != Set(), s.markers < MaxMarkers,
      s' = {
        ...s,
        markers: s.markers + 1,
        len: s.len.set(i, slot + 1),
        starts: s.starts.set(i, s.starts.get(i).union(Set(slot))),
        segStart: s.segStart.set(i, slot),
        dict: s.dict.set(i, Set()),
      },
    }

  // The withdrawn-defining-batch mutant: a batch defining k is created and
  // disposed unproposed, and its writer, rather than being abandoned (LG-I12),
  // keeps writing with k in its dictionary, as the batch's BeginRow left it.
  action withdraw(i: int, k: int): bool =
    val g = ledger(i)
    all {
      Mutation == "withdrawn-defining-batch",
      g.live, not(g.sealed), covers(g, k), not(s.dict.get(i).contains(k)),
      s' = { ...s, dict: s.dict.set(i, s.dict.get(i).union(Set(k))) },
    }

  // The token-includes-fenced mutant: a sealed ledger's former primary admits
  // k's next write, which is fenced and lands nowhere, and remembers it.
  action fencedWrite(i: int, k: int): bool =
    val g = s.ledgers.get(i)
    val fenced = { k: k, jsn: maxJsn(known(i, k)) + 1 }
    all {
      Mutation == "token-includes-fenced",
      g.live, g.sealed, covers(g, k), fenced.jsn <= MaxJsn, not(s.fenced.contains(fenced)),
      s' = { ...s, fenced: s.fenced.union(Set(fenced)) },
    }

  // ---------------------------------------------------------------------------
    // Transitions: seal and recover, then one conditional ledger-map write. The
  // no-seal mutant closes a ledger without sealing it.
  // ---------------------------------------------------------------------------
  def closing(i: int): Ledger =
    { ...s.ledgers.get(i), status: Closed, sealed: Mutation != "no-seal", tail: s.len.get(i) }

  pure def opened(tag: int, prefix: int, depth: int): Ledger =
    { live: true, tag: tag, prefix: prefix, depth: depth, status: Open, sealed: false, tail: 0 }

  // The reuse-id mutant gives the successor its predecessor's ID, and the
  // skip-id mutant skips one ID.
  action takeover(i: int): bool =
    val g = s.ledgers.get(i)
    val n = if (Mutation == "skip-id") s.next + 1 else s.next
    val tag = if (Mutation == "reuse-id") g.tag else n
    all {
      g.live, g.status == Open, n <= MaxLedgers,
      s' = { ...s, ledgers: s.ledgers.set(i, closing(i)).set(n, opened(tag, g.prefix, g.depth)), next: n + 1 },
    }

  // The no-close-overlap mutant leaves the parent open beside its children.
  action split(i: int): bool =
    val g = s.ledgers.get(i)
    val n = s.next
    val parent = if (Mutation == "no-close-overlap") g else closing(i)
    all {
      g.live, g.status == Open, g.depth < KeyBits, n + 1 <= MaxLedgers,
      s' = {
        ...s,
        ledgers: s.ledgers.set(i, parent)
          .set(n, opened(n, 2 * g.prefix, g.depth + 1))
          .set(n + 1, opened(n + 1, 2 * g.prefix + 1, g.depth + 1)),
        next: n + 2,
      },
    }

  action merge(i: int, j: int): bool =
    val a = s.ledgers.get(i)
    val b = s.ledgers.get(j)
    val n = s.next
    all {
      a.live, b.live, a.status == Open, b.status == Open,
      a.depth == b.depth, a.depth >= 1, a.prefix % 2 == 0, b.prefix == a.prefix + 1,
      n <= MaxLedgers,
      s' = {
        ...s,
        ledgers: s.ledgers.set(i, closing(i)).set(j, closing(j)).set(n, opened(n, a.prefix / 2, a.depth - 1)),
        next: n + 1,
      },
    }

  // ---------------------------------------------------------------------------
  // Drain, trim, and drop. A closed ledger is dropped once the Archive covers it and
  // no listed ledger with a lower ID overlaps it. The drop-undrained mutant
  // drops it before the Archive covers it, and the drop-out-of-order mutant while an
  // older overlapping ledger is still listed.
  // ---------------------------------------------------------------------------
  action drain(i: int, b: int): bool =
    val w = s.watermark.get(i)
    all {
      s.ledgers.get(i).live, w < b, b <= s.len.get(i),
      s' = {
        ...s,
        archive: s.archive.union(s.rows.get(i).filter(r => r.slot >= w and r.slot < b)),
        watermark: s.watermark.set(i, b),
      },
    }

  // A trim falls at the last segment start at or below the watermark; the
  // trim-mid-segment mutant trims at the watermark itself.
  action trim(i: int): bool =
    val w = s.watermark.get(i)
    val f = if (Mutation == "trim-mid-segment") w else s.starts.get(i).filter(x => x <= w).fold(0, (m, x) => if (x > m) x else m)
    all {
      s.ledgers.get(i).live, s.floor.get(i) < f,
      s' = { ...s, floor: s.floor.set(i, f), rows: s.rows.set(i, s.rows.get(i).filter(r => r.slot >= f)) },
    }

  action drop(i: int): bool =
    val g = s.ledgers.get(i)
    all {
      g.live, g.status == Closed,
      Mutation == "drop-undrained" or s.watermark.get(i) == g.tail,
      Mutation == "drop-out-of-order"
        or not(liveIds(s).exists(j => s.ledgers.get(j).tag < g.tag and overlaps(s.ledgers.get(j), g))),
      s' = {
        ...s,
        ledgers: s.ledgers.set(i, { ...g, live: false }),
        rows: s.rows.set(i, Set()),
        gone: s.gone.set(i, true),
      },
    }

  // ---------------------------------------------------------------------------
  // Reads: the ledger map, then the manifest, then the live ledgers. The
  // manifest-before-map mutant takes the snapshots in the other order, and the
  // ignore-trimmed mutant reads what remains instead of retrying.
  // ---------------------------------------------------------------------------
  def withMap(r: Reader): Reader = { ...r, hasMap: true, mapSnap: liveIds(s) }
  def withManifest(r: Reader): Reader = { ...r, wSnap: s.watermark, archiveSnap: s.archive }

  action beginRead: bool =
    val r = { ...idleReader, phase: 1, before: s.acked }
    all {
      s.reader.phase == 0,
      s' = { ...s, reader: if (Mutation == "manifest-before-map") withManifest(r) else withMap(r) },
    }

  action secondSnapshot: bool =
    val r = s.reader
    val both = if (r.hasMap) withManifest(r) else withMap(r)
    all {
      r.phase == 1,
      s' = { ...s, reader: { ...both, phase: 2 } },
    }

  action finishRead: bool =
    val r = s.reader
    val unreadable = r.mapSnap.exists(i => s.gone.get(i) or s.floor.get(i) > r.wSnap.get(i))
    val result = r.archiveSnap.union(r.mapSnap.map(i => s.rows.get(i).filter(x => x.slot >= r.wSnap.get(i))).flatten())
    all {
      r.phase == 2,
      if (unreadable and Mutation != "ignore-trimmed") s' = { ...s, reader: idleReader }
      else s' = { ...s, reader: idleReader, staleRead: s.staleRead or not(r.before.subseteq(result)) },
    }

  action step = any {
    nondet i = Ids.oneOf()
    nondet j = Ids.oneOf()
    nondet k = Keys.oneOf()
    nondet b = Slots.oneOf()
    nondet flagged = Bool.oneOf()
    any {
      write(i, k, flagged), marker(i), withdraw(i, k), fencedWrite(i, k),
      takeover(i), split(i), merge(i, j), drain(i, b), trim(i), drop(i),
    },
    beginRead, secondSnapshot, finishRead,
  }

  // ---------------------------------------------------------------------------
  // Invariants
  // ---------------------------------------------------------------------------
  // The open ledgers partition the keyspace: one open owner per key.
  val OneOwner = Keys.forall(k => liveIds(s).filter(i =>
    s.ledgers.get(i).status == Open and covers(s.ledgers.get(i), k)).size() == 1)

  // The installed ledgers, listed or dropped, carry the IDs 1, ..., next - 1,
  // one each.
  val IdsDense =
    val installed = Ids.filter(i => s.ledgers.get(i).live or s.gone.get(i))
    and {
      installed.size() == s.next - 1,
      installed.map(i => s.ledgers.get(i).tag) == 1.to(s.next - 1),
    }

  // (LedgerId, Slot) totally orders each key's history, and its JSNs are
  // 1, 2, ... in that order.
  pure def precedes(a: Row, b: Row): bool = a.tag < b.tag or (a.tag == b.tag and a.slot < b.slot)

  def ordered(k: int): bool =
    val h = history(s, k)
    and {
      h.map(r => r.jsn) == 1.to(h.size()),
      h.forall(a => h.forall(b => and {
        precedes(a, b) implies a.jsn < b.jsn,
        (a.tag == b.tag and a.slot == b.slot) implies a == b,
      })),
    }

  val HistoryOrdered = Keys.forall(k => ordered(k))

  // Every acknowledged entry stays in its key's history.
  val AckedInHistory = s.acked.forall(r => history(s, r.k).contains(r))

  // Each key's ledgers leave the map oldest first: every listed ledger which
  // overlaps a dropped one has a higher ID.
  val DropsInLineageOrder = Ids.forall(i => s.gone.get(i) implies liveIds(s).forall(j =>
    overlaps(s.ledgers.get(i), s.ledgers.get(j)) implies s.ledgers.get(i).tag < s.ledgers.get(j).tag))

  // A read returns every entry acknowledged before it began.
  val ReadsSeeAcked = not(s.staleRead)

  // LG-I12: every referencing row a listed ledger stores has its defining row
  // stored before it in its segment, so every stored row resolves.
  val ReferencesResolve = liveIds(s).forall(i => s.rows.get(i).forall(r => r.defining or s.rows.get(i).exists(d =>
    and { d.defining, d.k == r.k, d.seg == r.seg, d.slot < r.slot })))

  // LG-I12: each ledger is trimmed at a segment start, so it stores whole
  // segments.
  val TrimsAtSegmentStarts = Ids.forall(i => s.starts.get(i).contains(s.floor.get(i)))

  // JS-I10, HI-I4: each row's token is the replay of Plan over its key's
  // history: the last write of the key in each segment before the row's.
  def replay(k: int, r: Row): Set[int] =
    val h = history(s, k)
    h.filter(x => and {
      precedes(x, r),
      x.tag != r.tag or x.seg != r.seg,
      not(h.exists(y => and { y.tag == x.tag, y.seg == x.seg, y.jsn > x.jsn })),
    }).map(x => x.jsn)

  val TokensReplayPlan = Keys.forall(k => history(s, k).forall(r => r.token == replay(k, r)))

  // ---------------------------------------------------------------------------
  // Witnesses, each expected violated, so the properties above are not vacuous:
  // a listed ledger stores a referencing row, and a marker.
  // ---------------------------------------------------------------------------
  val NeverReferencingRow = liveIds(s).forall(i => s.rows.get(i).forall(r => r.defining))

  val NeverMarker = liveIds(s).forall(i =>
    s.floor.get(i).to(s.len.get(i) - 1).forall(x => s.rows.get(i).exists(r => r.slot == x)))
}
