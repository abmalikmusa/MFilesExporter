# File Export Engine

**Purpose.** Write a document's binary payload to the filesystem, under a
path derived from the original **TITLE + "." + EXTENSION** and a
configurable folder strategy. Handles every filesystem edge case that
matters at 5M-document scale: illegal characters, reserved Windows names,
Unicode, duplicates, long paths, blank titles, missing extensions.

Complements — does not replace — the SHA-256-keyed `FileSystemDocumentSink`
used by the streaming pipeline. Callers pick whichever is appropriate:
`FileSystemDocumentSink` for de-duplicated content-addressable storage;
`FileExportEngine` for human-readable "original filename" exports.

---

## 1. Compositional shape

```
                  FileExportEngine
                        │
        ┌───────────────┼────────────────────┐
        ▼               ▼                    ▼
IFilenameSanitizer  IFolderStrategy   IDuplicateResolver
        │               │                    │
        ▼               ▼                    ▼
   sanitized       relative folder    collision-safe path
   filename        (may be empty)     (deterministic in prod)
        │               │                    │
        └───────┬───────┴────────────────────┘
                ▼
       root + relative + filename
                │
                ▼
      Temp write ▸ fsync ▸ atomic rename ▸ FileExportResult
```

Every dependency is a pure function or a stateless service — the engine
itself has no per-request mutable state and is safe to share across
threads.

---

## 2. Folder strategies

Each strategy is a `IFolderStrategy` — takes a `FileExportContext`,
returns a relative folder path. Kinds:

| Kind | Example | Best for |
|---|---|---|
| `Flat` | `Output\Invoice.pdf` | Small corpora (< 10 000). One directory. |
| `HashSharded` (depth = 2) | `Output\ab\12\Invoice.pdf` | **Recommended for 5M+.** Uniform SHA-256 distribution. |
| `NumericShard` (buckets = 512) | `Output\535\Invoice.pdf` | When part IDs are dense integers and you want ordered browsing. |
| `Date` (pattern `yyyy/MM`) | `Output\2026\08\Invoice.pdf` | Retention or chronological browsing. |
| `Category` | `Output\pdfs\Invoice.pdf` | Grouping by extension / operator-supplied label. |
| `ShardedByDate` | `Output\ab\12\2026\08\Invoice.pdf` | **Best hybrid** — uniform fan-out + temporal locality. |

Add a new strategy by implementing `IFolderStrategy` and registering it
through `FolderStrategyFactory`.

---

## 3. Filename sanitization

`FilenameSanitizer` handles these classes of input in order:

1. **Unicode normalization** to NFC — so `café` in composed form and
   `cafe\u0301` in combining form produce the same file.
2. **Illegal characters**: `< > : " / \ | ? *`, control chars (0x00–0x1F),
   plus everything from `Path.GetInvalidFileNameChars()`. Replaced with
   `_`; control chars stripped entirely.
3. **Trailing dots and spaces** — Windows silently strips them at file
   creation, which would cause `foo.` and `foo` to collide unexpectedly.
   Trimmed pre-write.
4. **Empty title** — falls back to `DefaultTitle` (default `untitled`).
5. **Reserved Windows names** (`CON`, `PRN`, `AUX`, `NUL`, `COM1-9`,
   `LPT1-9`) — prefixed with `_` so `CON.pdf` becomes `_CON.pdf`.
6. **Empty extension** — falls back to `DefaultExtension` (default `bin`,
   or empty to produce an extensionless file).
7. **Length ceiling** — truncated to `MaxFilenameLength` (default 200);
   reserves room for the extension so the total stays legal.

The sanitizer reports a `wasSanitized` flag so callers can log and audit
non-trivial transformations.

---

## 4. Duplicate handling

**Deterministic disambiguation is the recommended production mode.**

| Strategy | Behaviour | When to use |
|---|---|---|
| `IdempotencyKeySuffix` **(default)** | `Invoice.pdf` → `Invoice_ab12cd34.pdf` when the target exists. Suffix derived from the SHA-256 idempotency key — race-safe and reproducible. | 5M+ scale; concurrent workers. |
| `CounterSuffix` | `Invoice.pdf` → `Invoice (1).pdf`, `(2).pdf`, ... | < ~100 k documents; requires stat probing. |
| `Fail` | Throws `IOException`. | Strict-uniqueness policy. |
| `Overwrite` | Overwrites the existing file. | Sync scenarios where the source is authoritative. |

The engine additionally opens the write target with `FileMode.CreateNew`
(unless overwriting) so a race between two workers both landing on the
same resolved path fails atomically at the OS level rather than
producing corrupt output.

---

## 5. Long-path handling

Windows caps paths at 260 characters unless the process opts into long
paths. Rather than rely on that opt-in, the engine enforces a soft
ceiling: `MaxFullPathLength` (default 240) — when a desired path exceeds
it, the engine **falls back to a short hash-based filename** in the same
directory: `<16-hex-char-idempotency-prefix>.<ext>`. The folder strategy
is preserved; only the filename shortens.

The result's `RequiredLongPathPrefix = true` signals the fallback, so
downstream consumers can audit or alert.

---

## 6. Long-path across platforms

| Platform | Path limit | Behaviour |
|---|---|---|
| Windows (default) | 260 chars | Engine enforces `MaxFullPathLength = 240`; longer names fall back to hash-based short names. |
| Windows with LongPathsEnabled | 32 767 chars | Same fallback, but you can raise `MaxFullPathLength`. |
| Linux (ext4, XFS) | 255 char/component, 4 096 total | Practically unbounded; keep `MaxFullPathLength = 4000`. |
| macOS (APFS) | 1 024 chars | Set `MaxFullPathLength = 1000`. |

---

## 7. Configuration

```jsonc
{
  "Exporter": {
    "FileExport": {
      "RootPath": "./export-output/documents",
      "FolderStrategy": "ShardedByDate",       // Flat | HashSharded | NumericShard | Date | Category | ShardedByDate
      "ShardDepth": 2,
      "NumericBucketCount": 512,
      "DateFolderPattern": "yyyy/MM",
      "DuplicateResolution": "IdempotencyKeySuffix",
      "MaxFilenameLength": 200,
      "MaxFullPathLength": 240,
      "DefaultTitle": "untitled",
      "DefaultExtension": "bin",
      "WriteBufferSize": 81920,
      "FsyncOnWrite": true,
      "OverwriteOnCollision": false
    }
  }
}
```

---

## 8. Recommended strategy for 5 000 000+ documents

**Strategy**: `ShardedByDate` — hash-shard depth 2 + date pattern `yyyy/MM`.

**Layout example**:
```
Output/ab/12/2026/08/Invoice.pdf
Output/ab/12/2026/09/Contract.docx
Output/cd/34/2026/08/Report.pdf
...
```

**Configuration**:
```json
{
  "FolderStrategy":       "ShardedByDate",
  "ShardDepth":           2,
  "DateFolderPattern":    "yyyy/MM",
  "DuplicateResolution":  "IdempotencyKeySuffix",
  "MaxFilenameLength":    200,
  "MaxFullPathLength":    240,
  "DefaultTitle":         "untitled",
  "DefaultExtension":     "bin"
}
```

### Why this shape

**Uniform fan-out** — two hex characters give **256** first-level buckets;
two more give **65 536** leaf buckets. 5 M documents spread across 65 536
buckets is ~76 files per bucket — well under any filesystem's efficient
directory-scan threshold.

- **NTFS**: happy up to ~10 000 files per directory; 76 is trivial.
- **ext4** (with `dir_index`): efficient up to ~1 M per directory; 76 is
  trivial.
- **XFS**: no practical limit for lookup, but scan operations degrade
  linearly. 76 keeps `ls` and file managers responsive.

**Temporal partitioning** — the `yyyy/MM` suffix pins each document under
a month directory. Downstream operations that touch "last month's export"
can scope to those directories only. Retention (`rm -rf 2024/*`) is
trivial.

**Deterministic collision handling** — `IdempotencyKeySuffix` requires no
disk probing and is race-safe across workers. Two workers exporting the
SAME document write the same disambiguated name (which is fine, they
overwrite identical content). Two workers exporting DIFFERENT documents
that happen to share a title get distinct suffixes.

**Original-filename preservation** — the folder does the fan-out;
filenames stay the human-readable original TITLE + EXTENSION. Operators
can `grep` the manifest for a name and find the file directly.

### Why NOT flat

Flat = 5 M files in one directory. `ls` takes minutes. File-explorer
tools time out. Backup tools stall. Never do this at scale.

### Why NOT numeric shard alone

`NumericShard(buckets=1000)` = 5000 docs/bucket — acceptable but higher
than sharded-by-date. Also, part-ID distribution is not always uniform;
some vaults concentrate on small ID ranges. Hash-shard is safer.

### Why NOT date alone

`Date(yyyy/MM)` = potentially millions per month. If the vault has a
high-activity period, one month's directory grows unbounded. Combined
with shard prefix it stays bounded.

### Why NOT category alone

Extension-based grouping produces a small number of very large buckets:
`pdfs/` and `docxs/` might each hold millions of files. Not viable at
scale.

---

## 9. Failure modes

| Failure | Handling |
|---|---|
| Cancellation | Temp file deleted; `OperationCanceledException` bubbles. |
| Illegal title | Sanitizer replaces / strips; write proceeds. |
| Reserved name | Sanitizer prefixes with `_`. |
| Collision | Resolver picks a disambiguated name; write proceeds. |
| Overly-long path | Engine shortens filename to hash; `RequiredLongPathPrefix=true`. |
| Disk full | `IOException` bubbles from `WriteAsync`; temp deleted. |
| Two workers race on same resolved path | `FileMode.CreateNew` fails atomically for one; caller retries with a new claim. |

---

## 10. Test coverage

Unit tests under `tests/MFilesExporter.Tests/Export/Files/`:

- **`FilenameSanitizerTests`** — illegal chars, control chars, trailing
  dots/spaces, reserved names, empty title/extension defaults, Unicode
  NFC + preservation, length truncation, extension normalisation.
- **`FolderStrategyTests`** — one test per strategy kind, plus
  factory-materialization coverage.
- **`DuplicateResolverTests`** — no-collision passthrough, hash-suffix
  determinism, counter-suffix loop, fail-on-collision throw, overwrite
  passthrough.
- **`FileExportEngineTests`** — end-to-end integration against a real
  filesystem: original filename in flat mode, hash-sharded folder,
  date folder, collision disambiguation with hash suffix, illegal
  characters, reserved names, blank title fallback, missing extension
  fallback, Unicode preservation, long-path fallback to hash filename.

---

## 11. What the engine does NOT do

- **Does not compute checksums.** That's `BinaryObjectReader`'s job —
  compose them if you need both inline hashing and human-readable paths.
- **Does not record manifest entries.** That's the outcome collector.
- **Does not retry on transient failures.** Wrap the call site in a
  resilience pipeline if the destination filesystem is remote / flaky.
- **Does not stream from SQL.** Callers pass any `Stream`; combine with
  `ISqlStreamingEngine` for the M-Files pathway.
