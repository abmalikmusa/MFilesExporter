# Metadata Generation Framework

**Purpose.** Produce a portable, verifiable catalog of every exported
document alongside the binary artifacts. Three artifacts are emitted:

| File | Format | Role |
|---|---|---|
| `metadata.csv` | RFC 4180 CSV, UTF-8 + BOM | Spreadsheet-friendly, DB-import-friendly. |
| `metadata.json` | Well-formed JSON (envelope + array) | Programmatic consumption. |
| `manifest.json` | Small JSON document | Run-level summary + artifact index. Read this first. |

Everything is streaming — no artifact is ever fully in memory. At 5 M
records the CSV weighs ~1 GB and the JSON ~3 GB; both are written record
by record.

---

## 1. Field schema

Every record contains 13 required fields (source-neutral names) plus two
optional extensions for downstream EDMS migration.

| Field | Type | Meaning | EDMS-mapping notes |
|---|---|---|---|
| `DocumentPartId` | int64 | Source `ID_DOCUMENTFILEPART`. Stable across versions. | Map to the destination's document identifier or "external id" column. |
| `VersionPart` | int64 | Source `ID_VERSIONPART`. Combined with `DocumentPartId` uniquely identifies a version. | Map to the destination's "version" column. |
| `Title` | string | Original title verbatim. | Map to the primary display name. |
| `Extension` | string | File extension without leading dot; may be empty. | Map to file-type / MIME derivation. |
| `LogicalFileSize` | int64 (bytes) | Uncompressed size. | Map to "size" / "content-length". |
| `PhysicalFileSize` | int64 (bytes) | On-disk / compressed size. | Rarely used downstream; keep for audit. |
| `LastWriteTime` | ISO 8601 UTC | Source last-write timestamp. | Map to "modified date" — usually the destination's authoritative modification time. |
| `ExportPath` | string | Absolute path of the exported artifact. | The destination importer reads bytes from here. |
| `Checksum` | hex string | SHA-256 of the exported payload. | Store in the destination for later integrity verification. |
| `ExportStatus` | string | `Succeeded` / `Failed` / `Skipped`. | Filter destination import to `Succeeded` only. |
| `ExportDate` | ISO 8601 UTC | When the exporter observed the outcome. | Store on the destination's audit trail. |
| `WorkerId` | int64 | Which exporter worker produced the outcome. | Traceability only. |
| `RetryCount` | int32 | 1-based attempt that produced the outcome. | Traceability only. |
| **`IdempotencyKey`** (opt) | string (SHA-256 hex) | Stable global fingerprint of `(part, version, dataFileVersion)`. | Excellent primary key for the destination. |
| **`DataFileVersionId`** (opt) | int64 | Source `ID_DATAFILEVERSION`. | Rarely useful outside the vault. |

### Schema versioning

Every artifact carries `schemaVersion` + `schemaId`:

- `schemaVersion` — `"1.0.0"` — semver-style.
- `schemaId` — `"seamfix.mfiles-exporter.metadata/1.0"` — stable identifier
  for downstream tools that switch on format.

Rules:
- **Additive change** (new optional field): bump MINOR.
- **Breaking change** (rename/remove field): bump MAJOR, and downstream
  tools MUST refuse an unfamiliar MAJOR without opt-in.

---

## 2. Composition

```
                         ┌────────────────────────────┐
                         │  IMetadataGenerator        │
                         │  MetadataGenerator (facade)│
                         └───┬──────────────────┬─────┘
                             │                  │
                             ▼                  ▼
                ┌───────────────────┐  ┌──────────────────────┐
                │ IMetadataWriter   │  │ IMetadataWriter      │
                │ CsvMetadataWriter │  │ JsonMetadataWriter   │
                └────────┬──────────┘  └──────────┬───────────┘
                         │                        │
                         ▼                        ▼
                metadata.csv               metadata.json (envelope + array)

                         + (on FinalizeAsync)
                         ┌────────────────────┐
                         │ ManifestJsonWriter │
                         └──────────┬─────────┘
                                    ▼
                              manifest.json
```

Adding a new format (Parquet, Excel, YAML) is a single new
`IMetadataWriter` implementation registered in
`MetadataGenerator.InitializeAsync`.

---

## 3. File formats

### 3.1 `metadata.csv`

- **Encoding**: UTF-8 with a leading BOM (`0xEF 0xBB 0xBF`). Excel on
  Windows needs the BOM to open Unicode correctly.
- **Delimiter**: `,` by default; configurable via `CsvDelimiter` (`\t`
  for TSV).
- **Line endings**: CRLF (RFC 4180).
- **Header row**: emitted by default. The columns are exactly the field
  names above, in order, with extension fields last when
  `IncludeExtensionFields = true`.
- **Escaping**: RFC 4180. Fields containing the delimiter, a double
  quote, or a line break are wrapped in `"..."`; interior quotes are
  doubled to `""`.
- **Date format**: `yyyy-MM-ddTHH:mm:ss.fffZ` (ISO 8601 UTC).

Example:

```csv
DocumentPartId,VersionPart,Title,Extension,LogicalFileSize,PhysicalFileSize,LastWriteTime,ExportPath,Checksum,ExportStatus,ExportDate,WorkerId,RetryCount,IdempotencyKey,DataFileVersionId
1,2,Invoice,pdf,1024,900,2026-08-03T12:00:00.000Z,/data/documents/ab/12/Invoice.pdf,deadbeef,Succeeded,2026-08-03T13:00:00.000Z,100,1,abcdef012345,999
2,3,"he said ""hi""",docx,2048,1800,2026-08-03T12:05:00.000Z,/data/documents/cd/34/he_said_hi.docx,cafeb0ba,Succeeded,2026-08-03T13:01:00.000Z,100,1,fedcba543210,1000
```

### 3.2 `metadata.json`

Envelope + streaming array — well-formed JSON, but written record by
record via `Utf8JsonWriter` so it never materializes as an
in-memory object.

```json
{
  "schemaVersion": "1.0.0",
  "schemaId": "seamfix.mfiles-exporter.metadata/1.0",
  "generator": "MFilesExporter",
  "records": [
    {
      "documentPartId": 1,
      "versionPart": 2,
      "title": "Invoice",
      "extension": "pdf",
      "logicalFileSize": 1024,
      "physicalFileSize": 900,
      "lastWriteTime": "2026-08-03T12:00:00.000Z",
      "exportPath": "/data/documents/ab/12/Invoice.pdf",
      "checksum": "deadbeef",
      "exportStatus": "Succeeded",
      "exportDate": "2026-08-03T13:00:00.000Z",
      "workerId": 100,
      "retryCount": 1,
      "idempotencyKey": "abcdef012345",
      "dataFileVersionId": 999
    }
  ]
}
```

**Downstream reading**: use a streaming parser — `Utf8JsonReader`,
Newtonsoft's `JsonTextReader`, jq's `--stream`, Python's `ijson`. Do NOT
`JsonSerializer.Deserialize<Envelope>` a 3 GB file.

### 3.3 `manifest.json`

Small, one-shot document written at the end of the run. **Read this
first** — it announces the schema version and points at every artifact.

```json
{
  "schemaVersion": "1.0.0",
  "schemaId": "seamfix.mfiles-exporter.metadata/1.0",
  "generator": "MFilesExporter",
  "generatedAt": "2026-08-03T18:00:00.000Z",
  "job": {
    "id": 42,
    "name": "monthly-export",
    "partitionKey": "default",
    "sourceServer": "mfiles-sql-01",
    "sourceDatabase": "MFilesVault",
    "startedAtUtc": "2026-08-03T10:00:00.000Z",
    "completedAtUtc": "2026-08-03T18:00:00.000Z"
  },
  "totals": {
    "documentsExpected": 5041559,
    "documentsRecorded": 5041500,
    "succeeded": 5041000,
    "failed": 400,
    "skipped": 100,
    "totalBytesWritten": 10000000000
  },
  "artifacts": [
    { "relativePath": "metadata.csv",  "format": "csv",  "recordCount": 5041500 },
    { "relativePath": "metadata.json", "format": "json", "recordCount": 5041500 }
  ]
}
```

Written via **temp-file + atomic rename** so a partial manifest never
appears at the final path.

---

## 4. Configuration

```jsonc
{
  "Exporter": {
    "Metadata": {
      "OutputDirectory": "./export-output/metadata",
      "WriteCsv": true,
      "WriteJson": true,
      "WriteManifest": true,
      "CsvFileName": "metadata.csv",
      "JsonFileName": "metadata.json",
      "ManifestFileName": "manifest.json",
      "CsvDelimiter": ",",
      "CsvIncludeUtf8Bom": true,
      "CsvIncludeHeader": true,
      "JsonIndent": false,
      "IncludeExtensionFields": true,
      "FlushEveryNRecords": 500
    }
  }
}
```

---

## 5. Usage

```csharp
public sealed class Runner
{
    private readonly IMetadataGenerator _metadata;

    public async Task RunAsync(CancellationToken ct)
    {
        await _metadata.InitializeAsync(ct);

        // For every exported document ...
        await _metadata.AppendAsync(new MetadataRecord
        {
            DocumentPartId    = docPart,
            VersionPart       = verPart,
            Title             = title,
            Extension         = ext,
            LogicalFileSize   = logicalSize,
            PhysicalFileSize  = physicalSize,
            LastWriteTime     = lastWriteUtc,
            ExportPath        = outputPath,
            Checksum          = sha256Hex,
            ExportStatus      = "Succeeded",
            ExportDate        = DateTime.UtcNow,
            WorkerId          = workerId,
            RetryCount        = attempt,
            IdempotencyKey    = idempKey.ToHex(),
            DataFileVersionId = dfv,
        }, ct);

        var summary = new ManifestSummary { /* ... */ };
        var artifacts = await _metadata.FinalizeAsync(summary, ct);
        // artifacts[0].RelativePath == "metadata.csv"
        // artifacts[1].RelativePath == "metadata.json"
    }
}
```

---

## 6. EDMS migration guide

The metadata format is designed to be **destination-agnostic** — the
field names describe the concept, not the source-schema origin. Here are
sample mappings for common target systems.

### 6.1 SharePoint (Microsoft Graph / CSOM)

| Source field | SharePoint column |
|---|---|
| `Title` | `File.Name` (rename to `Title.Extension` at import) |
| `Extension` | derived from `File.Name` |
| `LogicalFileSize` | `File.Size` (informational) |
| `LastWriteTime` | `Item.FileSystemInfo.LastModifiedDateTime` |
| `Checksum` | Custom column, e.g. `Sha256` |
| `IdempotencyKey` | Custom column `ExternalId` (unique) |
| `DocumentPartId` + `VersionPart` | Custom columns `SourceDocId`, `SourceVersion` |
| `ExportPath` | Read the bytes; not stored |

Recommended importer: Microsoft Graph Data Connect for volumes > 100 k
files, direct `PUT /drive/items/.../content` for smaller runs.

### 6.2 Documentum

| Source field | Documentum attribute |
|---|---|
| `Title` | `object_name` |
| `LastWriteTime` | `r_creation_date` (or a custom `source_modified` date) |
| `Checksum` | `content_hash` |
| `IdempotencyKey` | `i_chronicle_id` alternative or a custom `external_id` |
| `Extension` | `a_content_type` derivation |
| `DocumentPartId` + `VersionPart` | Custom type attributes |

Use `dm_document` as the base type; use DFC batch imports.

### 6.3 Alfresco (Content Services)

| Source field | Alfresco property |
|---|---|
| `Title` | `cm:name` + `cm:title` |
| `Extension` | Encoded in `cm:name` |
| `LastWriteTime` | `cm:modified` |
| `Checksum` | `cm:contentPropertyName` custom aspect |
| `IdempotencyKey` | Custom aspect `mig:sourceIdentity` |
| Everything else | Custom aspect for source-of-truth tracking |

Use the Alfresco Bulk Import Tool (in-place mode) — point it at
`ExportPath` on a mount visible to the Alfresco server.

### 6.4 OpenText Content Server

| Source field | OpenText attribute |
|---|---|
| `Title` | `Name` |
| `LastWriteTime` | `LastModifiedDate` |
| `Checksum` | Category: "Migration" / attribute "Sha256" |
| `IdempotencyKey` | Category: "Migration" / attribute "ExternalId" |

Use the CSWS / REST API for programmatic imports. For volumes > 1 M,
prefer the Enterprise Connect bulk-import path.

### 6.5 Generic file-share destination

Some migrations do not target an EDMS at all — the destination is a file
share. In that case, `metadata.csv` is your only integration surface:

1. Read `manifest.json`.
2. For each row in `metadata.csv` where `ExportStatus = 'Succeeded'`:
   - Copy `ExportPath` to your destination path (perhaps preserving
     the folder strategy).
   - Verify SHA-256 matches `Checksum`.
   - Record `IdempotencyKey` in the destination's catalog for idempotency.

---

## 7. Streaming and memory

| Component | Peak memory |
|---|---|
| `CsvMetadataWriter` | one StringBuilder (~256 B) + FileStream 64 KB buffer |
| `JsonMetadataWriter` | Utf8JsonWriter internal buffer (~64 KB) + FileStream 64 KB buffer |
| `ManifestJsonWriter` | ~4 KB (whole manifest fits in one buffer) |
| `MetadataGenerator` | Two writers + a tiny List of them |

Total steady-state metadata overhead: **< 300 KB** regardless of run
size.

The two writers use `SemaphoreSlim` (a fast in-process mutex) so
concurrent `AppendAsync` calls from parallel batch workers serialize
safely. Interlocked counters track `RecordCount` outside the mutex.

**Flush cadence** is configurable via `FlushEveryNRecords` (default
500) — a balance between crash-safety (more frequent flush = less lost
data on abrupt termination) and syscall overhead (less frequent flush =
better throughput).

---

## 8. Testing

Under `tests/MFilesExporter.Tests/Export/Metadata/`:

- **`CsvMetadataWriterTests`** — header + BOM + row, escape rules
  (comma / quote / newline in title), Unicode preservation,
  no-header mode, record counting, concurrent-append safety.
- **`JsonMetadataWriterTests`** — envelope + records array, every
  required field present, extension fields conditional, ISO 8601 UTC
  date format.
- **`ManifestJsonWriterTests`** — full manifest shape, atomic
  temp-then-rename, nullable `CompletedAtUtc`.
- **`MetadataGeneratorTests`** — all three artifacts produced with
  matching record counts, correct artifact references in manifest,
  conditional format enabling.

---

## 9. What this framework does NOT do

- **Does not read the metadata back.** Downstream tools consume it —
  they need their own streaming JSON / CSV parsers.
- **Does not deduplicate records.** If the caller appends the same
  document twice, both rows appear. Deduplication is the pipeline's
  responsibility (the claim engine already provides at-most-once
  Succeeded outcomes).
- **Does not compress artifacts.** Compression is a wrap-and-tar
  step in the export finalization phase.
- **Does not sign the manifest.** For tamper-evident manifests, hash
  `manifest.json` after finalization and sign the hash separately
  (or emit an accompanying `manifest.json.sig`).
