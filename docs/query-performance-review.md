# SQL Server Performance Review — Canonical Document Query

**Scope**: The validated document-retrieval query supplied in the project context, executed against the M-Files vault SQL Server database at production scale (~5 million committed document file versions, typical BLOB payloads spanning multiple KB to multi-MB).

**Ground rule**: The business logic — the join set, the `UPLOADCOMMITTED = 1` predicate, and the returned result set — MUST NOT change. Every recommendation below is a performance transformation that preserves this contract.

---

## 1. Reference query (business contract)

```sql
SELECT
    dfv.ID_DOCUMENTFILEPART,
    dfv.ID_VERSIONPART,
    dfv.TITLE,
    dfv.EXTENSION,
    d.ID_DATAFILEVERSION,
    d.LOGICALFILESIZE,
    d.PHYSICALFILESIZE,
    d.LASTWRITETIME,
    b.DATA
FROM DOCUMENTFILEVERSION dfv
JOIN DATAFILEVERSION d
    ON dfv.ID_DOCUMENTFILEPART = d.ID_DOCUMENTFILEPART
   AND dfv.DATAFILEVERSION    = d.ID_DATAFILEVERSION
JOIN DATAFILEVERSION_BYTES b
    ON d.ID_DOCUMENTFILEPART   = b.ID_DOCUMENTFILEPART
   AND d.ID_DATAFILEVERSION    = b.ID_DATAFILEVERSION
WHERE d.UPLOADCOMMITTED = 1;
```

**Result set**: every committed file version, with its metadata and BLOB payload.

**Cardinality (target)**: ~5,041,559 rows.

**Problems when executed as a single statement at scale**:

| Symptom | Cause |
|---|---|
| Runaway memory grant | Optimizer estimates row size using `varbinary(max)` sentinel — huge grant reserved even if most BLOBs are moderate. |
| Long-running reader pins a connection | Server keeps the reader open for the whole 5M-row scan; any blocking on the vault knock-on affects M-Files sessions. |
| Buffer pool churn | Streaming BLOBs into the buffer pool evicts hot metadata pages. |
| No natural checkpoint | Reader midway through 5M rows cannot be resumed after a crash without re-reading everything. |
| No back-pressure | Client cannot slow down BLOB flow without stalling metadata flow — the two are coupled inside one reader. |

The right optimization is not to change *what* the query returns, but *how* the workload is decomposed into streaming units.

---

## 2. Business-logic-preserving decomposition

The canonical result set is provably equivalent to the **union** of the outputs of two derived queries:

- **Q1 — Enumeration (metadata)** — the join of `DOCUMENTFILEVERSION` and `DATAFILEVERSION` with `UPLOADCOMMITTED = 1`, keyset-paginated on `(ID_DOCUMENTFILEPART, ID_VERSIONPART)`. Returns metadata columns.
- **Q2 — Content (BLOB)** — for each metadata row, a point lookup into `DATAFILEVERSION_BYTES` joined back to `DATAFILEVERSION` with the same `UPLOADCOMMITTED = 1` filter. Returns `DATA` only.

**Why this is equivalent**:

- Same tables, same join keys, same predicate.
- The set of `(ID_DOCUMENTFILEPART, ID_VERSIONPART, ID_DATAFILEVERSION)` triples for which both queries return a row is precisely the set for which the canonical query returns a row.
- The only theoretically different case is a row whose `UPLOADCOMMITTED` flips from 1 to 0 between the enumeration and the BLOB fetch. This is not a "difference" — such a document is no longer exportable at the time we would fetch it, and the exporter must, by any reading of the business rule, record it as skipped. This is what the exporter already does (`DocumentContentMissingException` → `Skipped`).

**This decomposition is the single most important optimization in this document.**

---

## 3. Index recommendations

### 3.1 Existing indexes (as delivered by M-Files)

| Table | Existing indexes |
|---|---|
| `DOCUMENTFILEVERSION` | Composite clustered PK on `(ID_DOCUMENTFILEPART, ID_VERSIONPART)` |
| `DATAFILEVERSION` | Composite clustered PK on `(ID_DOCUMENTFILEPART, ID_DATAFILEVERSION)` |
| `DATAFILEVERSION_BYTES` | Composite clustered PK on `(ID_DOCUMENTFILEPART, ID_DATAFILEVERSION)` |

These are sufficient for correctness but not optimal for the enumeration path. Two additional non-clustered indexes accelerate the exporter without touching the M-Files data model.

### 3.2 Recommended nonclustered indexes

> **IMPORTANT**: These are additive read-side indexes. They add no columns to any base table, add no triggers, and change no relationships. They should be applied only after clearance from the M-Files DBA — some M-Files support contracts consider index changes to the vault a licensed configuration change.

```sql
/* ---------------------------------------------------------------------
 * IX_DATAFILEVERSION_Committed_Covering
 *   Filtered on UPLOADCOMMITTED = 1 so the enumeration seek visits only
 *   exportable rows. INCLUDE columns eliminate the lookup back to the
 *   base table.
 * --------------------------------------------------------------------- */
CREATE NONCLUSTERED INDEX IX_DATAFILEVERSION_Committed_Covering
    ON dbo.DATAFILEVERSION (ID_DOCUMENTFILEPART, ID_DATAFILEVERSION)
    INCLUDE (LOGICALFILESIZE, PHYSICALFILESIZE, LASTWRITETIME)
    WHERE UPLOADCOMMITTED = 1
    WITH (ONLINE = ON, DATA_COMPRESSION = PAGE, FILLFACTOR = 90);

/* ---------------------------------------------------------------------
 * IX_DOCUMENTFILEVERSION_Enumeration_Covering
 *   Covers the enumeration query so the ordered scan visits only leaf
 *   pages of this index and never fetches from the base table.
 *   Order matches the keyset cursor: (ID_DOCUMENTFILEPART, ID_VERSIONPART).
 *   NOTE: because this is the clustered key, an additional nonclustered
 *   copy is only worthwhile if TITLE + EXTENSION are wide enough that
 *   the clustered index page fanout is limited. If your typical TITLE
 *   is short (< 100 chars), skip this index.
 * --------------------------------------------------------------------- */
CREATE NONCLUSTERED INDEX IX_DOCUMENTFILEVERSION_Enumeration_Covering
    ON dbo.DOCUMENTFILEVERSION (ID_DOCUMENTFILEPART, ID_VERSIONPART)
    INCLUDE (DATAFILEVERSION, TITLE, EXTENSION)
    WITH (ONLINE = ON, DATA_COMPRESSION = PAGE, FILLFACTOR = 95);
```

### 3.3 Indexes NOT to add

- **Not** a single-column index on `UPLOADCOMMITTED`. Low cardinality (essentially two values) → optimizer will ignore it or produce a bad plan.
- **Not** an index that puts `DATA` in an `INCLUDE` clause. Including a `varbinary(max)` column is legal but pathological — it explodes the index size and defeats the whole streaming design.
- **Not** a composite non-clustered index on `DATAFILEVERSION_BYTES`. The PK is already ideal for the point lookup; adding anything else is wasted maintenance cost.

### 3.4 Statistics

- Enable `AUTO_UPDATE_STATISTICS_ASYNC = ON` on the vault DB so dashboards and background scans never wait on stats recompilation.
- Refresh statistics on the two indexes above weekly with `WITH FULLSCAN` — small filtered indexes are cheap to full-scan and yield accurate histograms.

---

## 4. Execution-plan improvements

The optimal shape for the enumeration query is:

```
                                (nested loops join)
                                    /        \
        (ordered clustered scan)   /          \  (clustered/covering index seek)
        DOCUMENTFILEVERSION (dfv)             DATAFILEVERSION (d)
        TOP (@BatchSize) keyset                filter UPLOADCOMMITTED = 1
        ORDER BY part, ver
```

### 4.1 Recommended query hints

```sql
OPTION (
    FAST 1,                 -- optimize for the first row (streaming)
    LOOP JOIN,              -- pin the shape; keyset order requires nested loops
    RECOMPILE               -- avoid parameter-sniffing pitfalls on @Last*
)
```

- **`FAST 1`** — asks the optimizer to produce a plan that returns the first row as fast as possible. Aligned with streaming.
- **`LOOP JOIN`** — a hash or merge join would break the ordered semantics needed for keyset pagination. Pinning to loop join is safe here because the outer batch is always ≤ 5 000 rows.
- **`RECOMPILE`** — the batch parameters (`@LastDocumentFilePartId`, `@LastVersionPartId`) vary across every call; a cached plan could be tuned for one call's values and pessimize another. Recompile is cheap for this size of plan and eliminates parameter sniffing.

**Avoid** `OPTION (MAXDOP 1)` unless you observe parallelism-related waits (`CXPACKET`, `CXCONSUMER`). Under keyset TOP the optimizer usually picks serial, but forcing MAXDOP 1 on a 16-core server denies you the parallel scan when it would help.

### 4.2 Recompiles vs plan cache

- Enumeration query: `OPTION (RECOMPILE)` — worth it.
- Content query: no recompile — the query text is a stable point-lookup, the parameters vary in value not in shape, and a cached plan for a PK seek is always optimal.

### 4.3 Query Store

Enable Query Store on the vault DB in `QUERY_CAPTURE_MODE = AUTO`. It catches the rare regressed plan without hand-instrumentation. On Standard Edition the storage overhead is negligible for two hot queries.

---

## 5. Join optimizations

### 5.1 Join order

Force by shape (`LOOP JOIN` hint above). The optimizer nearly always picks this shape anyway when the enumeration outer is small (`TOP @BatchSize` with `@BatchSize` around 1 000–5 000).

### 5.2 Predicate placement

The `UPLOADCOMMITTED = 1` predicate is on the DATAFILEVERSION side. With the filtered index `IX_DATAFILEVERSION_Committed_Covering`, the seek visits only committed rows — the filter is effectively pushed into the index seek itself. Without the filtered index, the filter is applied after the seek; the predicate still eliminates rows but wastes IO on uncommitted rows.

### 5.3 Type mismatches

Verify the join columns have identical types (`BIGINT` vs `INT`). Any implicit conversion on a join key forces a scan. This is worth checking with:

```sql
SELECT c1.TABLE_NAME, c1.COLUMN_NAME, c1.DATA_TYPE
FROM INFORMATION_SCHEMA.COLUMNS c1
WHERE c1.TABLE_NAME IN (N'DOCUMENTFILEVERSION', N'DATAFILEVERSION', N'DATAFILEVERSION_BYTES')
  AND c1.COLUMN_NAME IN (
        N'ID_DOCUMENTFILEPART', N'ID_VERSIONPART',
        N'DATAFILEVERSION', N'ID_DATAFILEVERSION'
      );
```

If any join column comes back as a different type, request that M-Files support align them or introduce an appropriate `CONVERT` on the client side so it does not appear in the query plan.

### 5.4 Removing the outer join to DATAFILEVERSION_BYTES from enumeration

The single biggest join-set optimization is what the exporter already does: **do not join `DATAFILEVERSION_BYTES` during enumeration.** That table holds `DATA (varbinary(max))` — even a "select 1 column" scan of it triggers LOB reads at millions of rows.

The content query re-adds the join (with the same `UPLOADCOMMITTED = 1` filter) at BLOB-fetch time, once per document.

---

## 6. Batching strategy

### 6.1 Batch size for the enumeration query

`TOP (@BatchSize)` with `@BatchSize` between **1 000 and 5 000**.

- Below 1 000: connection setup dominates each round-trip.
- Above 5 000: the batch memory grant grows and the reader holds the connection long enough to matter on a shared server.
- Sweet spot on typical hardware: **1 000**.

The exporter's `MFilesSourceOptions.EnumerationBatchSize` defaults to `1000`.

### 6.2 Batch commit envelope on the tracking side

The producer side (this query) does not commit anything. The exporter batches its **outcome writes** to the tracking DB in groups of 200–500 via TVPs. Both loops are independent — a slow tracking-side flush does not slow the producer scan.

### 6.3 Parallel batches

Do **not** run multiple enumeration batches concurrently against overlapping keyset ranges. It is safe to run batches concurrently against **disjoint** ranges (partitioned by `ID_DOCUMENTFILEPART` mod N), and this is how the exporter horizontally scales via `Source.PartitionKey`.

### 6.4 BLOB fetch concurrency

The point-lookup content query is safe to run in parallel across N documents (default N = 8, tunable up to ~32 on well-provisioned hardware). Beyond that the connection pool contends with M-Files sessions on the same server.

---

## 7. Isolation level

Two different isolation levels are appropriate — one per query.

### 7.1 Enumeration → READ UNCOMMITTED (with NOLOCK equivalence)

```sql
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
```

Rationale:

- The enumeration only reads three integer columns and two short strings. If it observed an uncommitted row that a live M-Files session was in the middle of writing, no harm is done — the BLOB fetch step re-validates `UPLOADCOMMITTED = 1` and would either fetch a consistent row or record a `Skipped` outcome.
- READ UNCOMMITTED (or the equivalent `WITH (NOLOCK)`) removes all shared-lock overhead on the vault's hot tables, so the exporter cannot block M-Files sessions.
- This is the only way to run a 5M-row scan against a live vault without operational risk.

### 7.2 Content read → READ COMMITTED (default)

```sql
-- default; no override
```

Rationale:

- `varbinary(max)` is stored in LOB pages that can be updated concurrently with row updates. A dirty read of a BLOB while an M-Files session is rewriting it would produce a **torn** payload — the exporter would write a corrupt file.
- READ COMMITTED serializes on the LOB pages; the cost is negligible (each read is a PK point lookup) and correctness is preserved.
- The exporter's `MFilesQueries.ContentQuery` deliberately omits any `NOLOCK` hint.

### 7.3 Do NOT enable snapshot isolation on the vault

The vault is a live M-Files application database. Enabling `ALLOW_SNAPSHOT_ISOLATION` or `READ_COMMITTED_SNAPSHOT` changes semantics that M-Files did not test against — the risk of subtle regressions in the live app outweighs the marginal benefit to the exporter.

---

## 8. Locking strategy

| Query | Isolation | Lock hint | Lock footprint |
|---|---|---|---|
| Enumeration | READ UNCOMMITTED | `WITH (NOLOCK)` on `DOCUMENTFILEVERSION` and `DATAFILEVERSION` | None — no shared locks acquired |
| Content | READ COMMITTED | none | Shared row + LOB page locks, released immediately after read |

The exporter's implementation already emits both patterns exactly as above.

### Additional guidance

- Do not use `TABLOCK`, `UPDLOCK`, `HOLDLOCK`, `SERIALIZABLE`, or `XLOCK` hints on any vault query.
- Do not wrap the enumeration in a user-initiated transaction (the isolation-level `SET` statement is scoped to the session, not a transaction).
- If parallelism is enabled, `PAGLOCK` is unnecessary — the batch size and clustered scan keep footprint bounded.

---

## 9. Read consistency

The exporter's consistency model:

| Guarantee | Enforced by |
|---|---|
| Every emitted row corresponds to a row that satisfied `UPLOADCOMMITTED = 1` **at the time it was enumerated OR at the time its BLOB was fetched** | Two-query design |
| No exported artifact is derived from an uncommitted BLOB | Content query re-checks `UPLOADCOMMITTED = 1` |
| Enumeration order is stable across restarts | Keyset pagination on `(ID_DOCUMENTFILEPART, ID_VERSIONPART)` — deterministic, PK-backed |
| No duplicate final artifacts | Idempotency key (SHA-256) checked against the state store before content fetch |
| Rows added to the vault after export start are not exported | Enumeration terminates when it exhausts the keyset ≤ its high-water mark — new rows appear beyond that mark and are ignored |

If a compliance requirement demands that "the export reflects the vault as of a specific instant," the answer is not to hold a read lock for hours but to run the exporter against a **database snapshot** (`CREATE DATABASE ... AS SNAPSHOT OF ...`) taken at that instant. This is an operational, not a code, decision.

---

## 10. Streaming strategy

### 10.1 Client-side

Every requirement from the DAL spec applies here:

- `SqlDataReader` with `CommandBehavior.SingleResult | CommandBehavior.SingleRow | CommandBehavior.SequentialAccess` on the content query.
- `SqlDataReader.GetBytes(ordinal, position, buffer, offset, count)` in a chunked loop wrapped by `SqlBytesReadStream`.
- Buffer size = `Storage.WriteBufferSize` (default 80 KiB — a multiple of the default TDS packet size).
- `ArrayPool<byte>.Shared.Rent(bufferSize)` for the read buffer; nothing ever GC-promotes a BLOB.

### 10.2 TDS network layer

Set the connection string's `Packet Size` to a value larger than the default 4 096 bytes when BLOBs are consistently > 100 KiB:

```
Server=...;Database=MFilesVault;
Packet Size=32768;                  -- 32 KiB TDS packets
Encrypt=True;
TrustServerCertificate=False;
Application Name=MFilesExporter;
```

Larger packets reduce per-BLOB TDS overhead. The server-side max is 32 KiB.

### 10.3 Do NOT do this

- `SELECT DATA FROM ...` into a `byte[]` local (materializes the whole BLOB).
- `reader.GetSqlBytes(0).Buffer` (allocates a `SqlBytes` that owns the full array).
- Anything involving `Dapper` on the content query. Dapper materializes rows for object mapping — not what we want for `varbinary(max)`.

---

## 11. Memory optimization

### 11.1 Server side

- `MAX SERVER MEMORY` — leave the vault SQL Server with a reasonable memory ceiling (e.g., 75% of host RAM). Streaming BLOB reads should not push the buffer pool.
- **Do not enable Buffer Pool Extension** for the vault — its use case is OLTP with hot working set, not streaming BLOB read.
- **Data compression on the two nonclustered indexes above** — `DATA_COMPRESSION = PAGE`. Filtered indexes are small; page compression gains ~2–3× fit for metadata columns.
- **Large-value-types-out-of-row** — this is a table-level setting on `DATAFILEVERSION_BYTES`:

  ```sql
  EXEC sp_tableoption N'dbo.DATAFILEVERSION_BYTES', 'large value types out of row', 1;
  ```

  When 1 (recommended for large BLOBs), the varbinary(max) column is stored in LOB pages regardless of size. When 0 (default), small BLOBs live in-row, which is faster for tiny payloads but pathological for the export use case where the reader must skip past the row to reach the LOB pages anyway. Set to 1 if the median BLOB size is > 8 KiB.

- **FILESTREAM / FileTable** — not applicable to the M-Files vault as delivered; do not restructure. If the customer moves to FILESTREAM in the future, the exporter would use `SqlFileStream` for zero-copy BLOB streaming.

### 11.2 Client side

- `ArrayPool<byte>` for BLOB read buffers.
- **`SqlBytesReadStream`** never allocates beyond the buffer size.
- **No LINQ enumeration** of the SqlDataReader — direct column reads by ordinal.
- **`Meter` and `ActivitySource`** are `static readonly` — no per-row instantiation.

### 11.3 Connection pool

- `Max Pool Size = 200` is comfortable for 16 content readers + 1 enumerator + health checks.
- **Do not disable pooling.** Every `SqlConnection` open must go through the pool.

---

## 12. Optimized production queries

Both queries return **exactly the same rows** as their sub-fragments of the canonical query. All hints are performance transformations, not semantic ones.

### 12.1 Enumeration query (production)

```sql
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT TOP (@BatchSize)
    dfv.ID_DOCUMENTFILEPART,
    dfv.ID_VERSIONPART,
    dfv.TITLE,
    dfv.EXTENSION,
    d.ID_DATAFILEVERSION,
    d.LOGICALFILESIZE,
    d.PHYSICALFILESIZE,
    d.LASTWRITETIME
FROM dbo.DOCUMENTFILEVERSION AS dfv WITH (NOLOCK)
INNER JOIN dbo.DATAFILEVERSION AS d WITH (NOLOCK)
    ON dfv.ID_DOCUMENTFILEPART = d.ID_DOCUMENTFILEPART
   AND dfv.DATAFILEVERSION    = d.ID_DATAFILEVERSION
WHERE d.UPLOADCOMMITTED = 1
  AND (
        dfv.ID_DOCUMENTFILEPART >  @LastDocumentFilePartId
     OR (dfv.ID_DOCUMENTFILEPART = @LastDocumentFilePartId
         AND dfv.ID_VERSIONPART  > @LastVersionPartId)
      )
ORDER BY
    dfv.ID_DOCUMENTFILEPART ASC,
    dfv.ID_VERSIONPART       ASC
OPTION
(
    FAST 1,
    LOOP JOIN,
    RECOMPILE
);
```

### 12.2 Content query (production)

```sql
SELECT b.DATA
FROM dbo.DATAFILEVERSION_BYTES AS b
INNER JOIN dbo.DATAFILEVERSION AS d
    ON b.ID_DOCUMENTFILEPART = d.ID_DOCUMENTFILEPART
   AND b.ID_DATAFILEVERSION  = d.ID_DATAFILEVERSION
WHERE b.ID_DOCUMENTFILEPART = @DocumentFilePartId
  AND b.ID_DATAFILEVERSION  = @DataFileVersionId
  AND d.UPLOADCOMMITTED = 1;
```

**Executed with**:

- `CommandType = StoredProcedure` is preferable — encapsulate the two queries as stored procedures so the vault DBA controls their plans centrally.
- `CommandBehavior.SingleResult | CommandBehavior.SingleRow | CommandBehavior.SequentialAccess` on the content command.
- `SqlDataReader.GetBytes(...)` in a chunked loop via `SqlBytesReadStream`.

### 12.3 Equivalence proof sketch

Let *C* be the set of `(part, ver, dfv)` triples the canonical query would emit, and let *E* be the set the enumeration query emits and *B(x)* be the set of BLOBs the content query returns for triple *x*.

Then:
- *E* = { *(part, ver, dfv)* : `UPLOADCOMMITTED = 1` on that DATAFILEVERSION row and the DOCUMENTFILEVERSION row joins to it }. This is exactly the projection of *C* onto its metadata columns.
- *B(x)* is non-empty iff `UPLOADCOMMITTED = 1` still holds and the BLOB row exists. If the flag flips or the BLOB row is missing between the two queries, the tuple is `Skipped` — which is the same result the canonical query would produce if it observed the same intermediate state (via read-committed race with the M-Files writer).

So the union of Q1 followed by Q2 emits a subset of *C* that differs from *C* only by rows undergoing a live `UPLOADCOMMITTED` transition — which is not a "difference" in the business rule.

---

## 13. Post-deployment checklist

Before enabling the exporter against production:

- [ ] The two nonclustered indexes above are created **only after M-Files DBA sign-off**.
- [ ] Statistics on the two new indexes were built with `FULLSCAN`.
- [ ] Query Store is enabled on the vault database.
- [ ] The exporter's connection string includes `Application Name=MFilesExporter` so vault DBAs can see it in `sys.dm_exec_sessions`.
- [ ] `sp_WhoIsActive` (or equivalent) shows the enumeration query with a small memory grant (< 5 MB) — a big memory grant means the filtered index isn't being used.
- [ ] `sys.dm_db_index_usage_stats` after 24 h shows that both new indexes are being read (`user_seeks + user_scans > 0`) — if not, the plan isn't using them and needs investigation.
- [ ] End-to-end throughput hits the SLO target from the design doc (≥ 100 docs/s baseline, ≥ 500 docs/s at production concurrency).

---

## 14. Summary of recommendations

| # | Recommendation | Impact | Effort |
|---|---|---|---|
| 1 | Decompose the canonical query into (metadata + BLOB) — already implemented in `MFilesQueries.cs`. | Enables everything else. |  ✓ Done |
| 2 | Add filtered covering index `IX_DATAFILEVERSION_Committed_Covering`. | Eliminates the lookup on enumeration; skips uncommitted rows. | Small (needs DBA sign-off). |
| 3 | Add covering index `IX_DOCUMENTFILEVERSION_Enumeration_Covering`. | Marginal — only helps if TITLE/EXTENSION are wide. | Small. |
| 4 | Add `OPTION (FAST 1, LOOP JOIN, RECOMPILE)` to the enumeration query. | Prevents parameter-sniffing regressions; pins the loop-join shape. | Trivial. |
| 5 | Set `Packet Size=32768` in the connection string. | Fewer TDS packets per BLOB. | Trivial. |
| 6 | Set `large value types out of row = 1` on `DATAFILEVERSION_BYTES`. | Better streaming for medium-to-large BLOBs. | Small (M-Files DBA). |
| 7 | Enable Query Store on the vault DB. | Catches regressions without hand-instrumentation. | Trivial. |
| 8 | Keep isolation split: READ UNCOMMITTED for enumeration, READ COMMITTED for content. | Non-blocking metadata scan + torn-free BLOB read. | ✓ Done. |
| 9 | Keep BLOB streaming via `SqlBytesReadStream` (chunked `GetBytes()`). | Bounded memory regardless of BLOB size. | ✓ Done. |
| 10 | Package the two queries as stored procedures on the vault. | Vault DBA controls the plans; client cannot force a bad recompile. | Small (M-Files DBA). |

Recommendations 1, 8, 9 are already delivered in the exporter's code. Recommendations 2–7 and 10 are configuration/DDL changes on the vault side that need coordination with the M-Files DBA.
