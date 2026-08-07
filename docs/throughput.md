# Throughput

> _Numbers below are dev-box baseline measurements — floor estimates that real production hardware should meet or exceed._

## How to reproduce

```bash
dotnet test --filter "Category=Performance" \
    --logger "console;verbosity=detailed"
```

Two benchmarks live in `tests/MFilesExporter.IntegrationTests/EndToEnd/ThroughputBenchmarkTests.cs`:

- `Throughput_5k_Corpus_MeetsFloorTarget` — single 8-worker run against 5 000 documents (~40 KiB average). Asserts ≥ 25 docs/sec so a regression fails CI.
- `Throughput_5k_Corpus_ScalingCurve` — the same corpus run at 2, 4, 8, 16 workers so the scaling shape is visible.

Both seed a fresh vault schema with a mix of small/medium/large payloads (60 % small, 30 % medium, 10 % large — see `VaultSeeder.SeedAsync`) and record: elapsed, docs/sec, MiB/sec, memory delta, and an extrapolation to a 5 M document run.

## Reference results

**Hardware**: Docker Desktop on Apple Silicon macOS, 8 GB memory budget for the SQL container. SQL Server 2022 in a Linux container on the **same host** as the exporter — CPU/disk contention is real. Real production deployments with dedicated SQL Server + SSD-backed storage should exceed these numbers substantially.

**Corpus**: 5 000 documents, 196.3 MiB total, average document 40.2 KiB.

| Workers | docs/sec | MiB/sec | Elapsed | Scaling vs 2-worker |
|--------:|---------:|--------:|--------:|--------------------:|
| 2       |    110.7 |    4.35 |  45.17s | 1.00× (baseline)    |
| 4       |    259.3 |   10.18 |  19.28s | 2.34×               |
| 8       |    342.6 |   13.45 |  14.59s | 3.10×               |
| 16      |    389.5 |   15.29 |  12.84s | 3.52×               |

**Extrapolation to 5 M documents** (at these dev-box rates):

- 2 workers → ~12.5 hours
- 4 workers → ~5.4 hours
- 8 workers → **~4.1 hours** ← current default
- 16 workers → ~3.6 hours

**Memory footprint**: working-set delta bounced between +19 MiB and –145 MiB across runs — no sustained growth as the corpus scales. The GC heap grew ~19 MiB and held steady.

**Repeatability**: three consecutive 8-worker runs produced 333, 352, 345 docs/sec. Coefficient of variation ~3 %.

## Interpretation

The curve shows near-linear scaling from 2 → 4 workers (2.34×), diminishing at 8 (3.10×), and mostly saturated at 16 (3.52×). The bottleneck in this test setup is **SQL Server contention** — because the container shares CPU and disk with the exporter, more workers don't get proportionally more SQL throughput.

On a real deployment the ratios will shift:

- Dedicated SQL Server on separate hardware removes the CPU-share bottleneck.
- Local SSD (rather than Docker Desktop's virtualised disk) improves BLOB read latency.
- 32 GB+ RAM on the SQL host lets the buffer pool cache the enumeration query.

Rule of thumb from experience with similar pipelines: expect **2–3× the dev-box numbers** on a properly-provisioned production host, i.e. ~700–1 000 docs/sec at 8 workers, and 1 000–1 500 docs/sec at 16 workers. That puts a full 5 M-document run around **1–2 hours** — well within a normal maintenance window.

## Tuning notes

Configuration knobs most likely to matter, in decreasing order of impact:

1. **`Exporter:ParallelProcessing:WorkerCount`** — start with `min(cpu_cores, 16)`. Higher only helps if the target SQL Server saturates its CPU last.
2. **`Exporter:Pipeline:ContentReaderConcurrency`** and **`SinkConcurrency`** — the two channel-consumer counts. Keep them equal to `WorkerCount` unless disk write is the bottleneck (then lower `SinkConcurrency`) or SQL BLOB reads are (then raise `ContentReaderConcurrency`).
3. **`Exporter:SqlStreaming:FetchSize`** — 1 000 is a good default. Larger means fewer round-trips but more memory per page; smaller means finer-grained producer→consumer flow.
4. **`Exporter:Source:EnumerationBatchSize`** — the SQL top-N page size. Matches `FetchSize` in practice.
5. **`Exporter:Pipeline:OutcomeBatchSize`** — 200 is a good default. Larger reduces tracking-DB round-trips at the cost of longer at-risk window on cancel.
6. **`Exporter:FileExport:FsyncOnWrite`** — off in dev, **on in prod**. This is the durability guarantee; leaving it off is a data-loss risk.

## When to remeasure

Run the benchmark:

- After changing any pipeline stage's concurrency knobs.
- Before deploying to a new class of hardware (moving from bare-metal to VM, or between cloud VM sizes).
- After a .NET / Microsoft.Data.SqlClient major-version bump.
- Whenever you suspect a regression — the 25 docs/sec floor assertion is deliberately loose because we don't want the CI to flake on a slow shared runner. If you see the number drop below 200 on the dev box, something has broken.
