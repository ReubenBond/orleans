# SlateDB Archive (`clog-archive`) Model

`slateArchive.qnt` formally specifies Roux's concrete SlateDB-backed `clog-archive` (`docs/design/storage.md` §3, `C1`, `ST2`, `ST3`, and `docs/design/clog/invariants.md` `HI-I1`–`HI-I4`).

## Checked properties (`safety`)

| Invariant | Guarantee |
| --- | --- |
| `singleWriterFenced` | SlateDB object-store writer fencing prevents any commit from a stale writer epoch (`C1`, `ST2`). |
| `watermarksMonotone` | SlateDB watermarks never regress across snapshots and `MetaStore` (`CLLM`) `archive_watermark` never leads durable SlateDB watermarks (`HI-I1`). |
| `archiveMatchesData` | Every slot below a SlateDB snapshot's watermark is durably present in that snapshot's rows (atomic `WriteBatch`). |
| `dropsAfterDrain` | Live ledger trims and closed-ledger drops never discard an entry not yet durable in SlateDB (`HI-I3`). |
| `readsComplete` | Every read using `DbReader` manifest refresh (`subscribe()` / cache-sharing reopen) + map-before-archive retry returns all committed entries present when the read began (`HI-I2`, `ST3`). |

## Mutants (`../check-mutants.sh`)

| Mutant | Disabled guard | Expected violation |
| --- | --- | --- |
| `mutantStaleDbReaderNoRefresh` | Reader serves from a lagging `DbReader` snapshot without waiting to cover `CLLM` watermarks | `readsComplete` |
| `mutantPublishCllmBeforeSlateCommit` | `CLLM` watermark published before durable SlateDB commit | `watermarksMonotone` |
| `mutantNonAtomicWatermarkBatch` | Watermark updated in SlateDB separately from data rows | `archiveMatchesData` |
| `mutantUnfencedStaleWriter` | Stale writer epoch commits after failover | `singleWriterFenced` |
| `mutantDropBeforeDrain` | Closed ledger dropped before watermark reaches tail | `dropsAfterDrain` |

## Reproduce

```bash
cd specs/archive
quint typecheck slateArchive.qnt
quint test slateArchive.qnt --main=slateArchiveSim --match=Test
quint run slateArchive.qnt --main=slateArchiveSim --backend=rust --invariant=safety --max-steps=40 --max-samples=20000
../check-mutants.sh slateArchive.qnt slateArchiveSim safety --backend=rust --max-steps=40 --max-samples=20000 --seed=7
```
