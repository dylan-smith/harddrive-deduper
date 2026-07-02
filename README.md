# DupeHunter

A Windows C# CLI that scans every file on every (or selected) hard drive and records each
file's metadata plus a content fingerprint into a local **SQLite database file**. The content hash
makes it trivial to find duplicate files: any rows sharing a `ContentHash` are byte-for-byte
identical. SQLite is embedded — there is no server to install or run; the whole database is a single
`.db` file (default `dupehunter.db`).

## What it records

For every file it writes one row containing:

| Column            | Meaning                                              |
|-------------------|------------------------------------------------------|
| `FileName`        | File name only                                       |
| `FullPath`        | Absolute path                                        |
| `SizeBytes`       | Size in bytes                                         |
| `DateModifiedUtc` | Last write time (UTC)                                |
| `DateCreatedUtc`  | Creation time (UTC)                                  |
| `ContentHash`     | SHA-256 of the file contents (lower-case hex)        |
| `ScanError`       | Populated if the file couldn't be hashed (e.g. lock) |
| `ScanRunId`       | GUID identifying this scan run (one run per drive)   |
| `ScannedAtUtc`    | When the row was written                             |

The table is created automatically if it doesn't exist, as is the database file itself
(SQLite creates the `.db` file on first open). The file is opened in **WAL mode**, so the several
drives scanned in parallel can read concurrently; their writes are serialized through a single
process-wide lock so they never collide on the one shared file.

## Scans and analysis

Each drive is scanned as its own **scan run** with a unique `ScanRunId`, logged to the `Scans` table
(start/finish time, status, and which drive). Scanning `C,D` therefore produces two runs.

When analyzing (automatically after a scan, or via `--analyze`), the tool takes the **latest
completed scan of each drive** and combines them, so duplicates are found across drives. Pass
`--drives` to restrict the analysis to specific drives' latest scans. Scans that were canceled,
failed, or never finished are never analyzed, and a superseded older scan of a drive is ignored
once a newer one completes.

As part of every analysis the tool also writes a **YAML report** (auto-named
`duplicates-<UTC timestamp>.yml` by default, so repeated runs don't overwrite each other) listing
*every* duplicate file and folder set whose reclaimable (wasted) space reaches a threshold — **100 MB
by default** — with *all* of each set's locations, not just the handful shown on the console. This makes
the report directly actionable. Tune it with `--yaml-threshold-mb <n>` and `--yaml-out <path>`, or skip
it entirely with `--no-yaml`.

The **`dupehunter-gui` review tool works entirely off this YAML report**: open the `.yml` file, browse
the duplicate sets, and delete redundant copies. Each deletion updates the report file in place (a set
that's down to one copy drops out; deleting a folder tree also strips everything the report tracked
beneath it), so you can close the GUI and pick up where you left off later. The scan database is never
touched by the GUI — it's only re-read the next time the CLI scans or analyzes.

The report looks like:

```yaml
generatedUtc: "2026-06-22T16:50:00Z"
wastedSpaceThresholdBytes: 104857600
totalWastedBytes: 5368709120
scans:
  - drive: "C:\\"
    scanRunId: "a1b2c3d4-..."
    completedUtc: "2026-06-22T10:00:00Z"
duplicateFileSets:
  - name: "VacationVideo.mp4"
    namesDiffer: false
    contentHash: "9f86d081884c7d65..."
    sizeBytes: 1073741824
    copyCount: 3
    wastedBytes: 2147483648
    locations:
      - "C:\\Users\\me\\Videos\\VacationVideo.mp4"
      - "D:\\Backup\\VacationVideo.mp4"
      - "D:\\Backup2\\VacationVideo.mp4"
duplicateFolderSets: []
```

After a successful analysis (whether post-scan or a standalone `--analyze`), the tool automatically
runs a database **cleanup**, pruning everything but the latest completed scan of each drive (the same
runs analysis just used). Pass `--no-cleanup` to skip it. The `Scans` audit log is always
preserved, and `--dry-run` previews the cleanup without deleting anything.

## Requirements

- .NET 10 SDK

No database server is required — SQLite is embedded in the executable.

## Build

```
dotnet build -c Release
```

The executable is `dupehunter` (e.g. `bin/Release/net10.0/dupehunter.exe`).

## Usage

```
dupehunter [options]
```

| Option | Description |
|--------|-------------|
| `-d, --drives <list>` | Comma-separated drives to scan (`C,D` or `C:\,E:\`). Omit to scan **all fixed drives**. |
| `-c, --db, --database <path>` | SQLite database file. Created if it doesn't exist. Default: `dupehunter.db`. |
| `-t, --table <name>` | Destination table. Default `Files`. |
| `--no-hash` | Record metadata only; skip hashing (much faster). |
| `--max-hash-mb <n>` | Skip hashing files larger than `n` MB (metadata still recorded). |
| `--batch-size <n>` | Rows per insert transaction. Default 5000. |
| `--parallelism <n>` | Hashing threads. Default = processor count. |
| `--recreate` | Drop and recreate the table before scanning. |
| `--follow-links` | Follow directory symlinks/junctions (off by default to avoid loops). |
| `-h, --help` | Show help. |

### Examples

Scan all fixed drives into the default `dupehunter.db`:

```
dupehunter
```

Scan only C: and D::

```
dupehunter --drives C,D
```

Use a specific database file location:

```
dupehunter --db D:\index\dupehunter.db --drives C
```

Fast metadata-only inventory of C:, starting fresh:

```
dupehunter --drives C --no-hash --recreate
```

### Finding duplicates afterward

Open the `.db` file with any SQLite client (e.g. the `sqlite3` CLI):

```sql
SELECT ContentHash, COUNT(*) AS Copies, SUM(SizeBytes) AS TotalBytes
FROM Files
WHERE ContentHash IS NOT NULL
GROUP BY ContentHash
HAVING COUNT(*) > 1
ORDER BY SUM(SizeBytes) DESC;
```

## Notes

- Each drive is scanned in two passes. Pass one enumerates every file and writes its metadata
  (name, path, size, dates) with a NULL hash; pass two reads those rows back and fills in the
  content hashes. Committing the full inventory before hashing means an interrupted hashing pass
  still leaves a complete file listing behind. `--no-hash` runs pass one only.
- Drives are scanned **in parallel** — each gets its own database connection and scan run, and the
  live progress display shows every drive's passes at once (one block of lines per drive). With
  several drives hashing together, total hashing threads can reach `--parallelism` × drive count.
- Hashing streams the file (1 MB buffer) so memory stays flat even on very large files.
- Files open in other processes are read with shared access where possible; unreadable files are
  still recorded with their metadata and a populated `ScanError`.
- Inaccessible directories (permissions) are skipped and counted, not fatal.
- Reparse points (symlinks/junctions) are skipped by default to avoid cycles and scanning network
  targets; use `--follow-links` to include them.
- Long paths (>260 chars) are supported via the application manifest on Windows 10 1607+ (the
  `LongPathsEnabled` system setting must also be on).
- Press **Ctrl-C** to stop; buffered rows are flushed before exit.
- Run elevated (Administrator) to maximize the set of readable system files.
```
