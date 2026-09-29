module PcbObserver.StoreTests

open System
open System.IO
open System.Text.Json
open PcbObserver.Capture
open PcbObserver.Render
open PcbObserver.Store
open PcbObserver.Tests
open Xunit

/// Writes the first `layersToWrite` SVGs plus a manifest listing the FULL
/// default set — mirrors a render that died partway (partial = incomplete).
let writeBundle (dir: string) (layersToWrite: int) =
    for l in layers |> Array.truncate layersToWrite do
        File.WriteAllText(Path.Combine(dir, $"{l}.svg"), "<svg/>")

    let snap =
        { sequence = Int32.Parse(Path.GetFileName dir)
          sha256 = $"hash-{Path.GetFileName dir}"
          path = $"C:/snap/{Path.GetFileName dir}.kicad_pcb"
          capturedAt = DateTime.UtcNow
          captureStatus = "stable" }

    Store(dir).WriteManifest(dir, snap, "C:/src/board.kicad_pcb", "kicad-cli", "test", layers)

/// Legacy-shape bundle: manifest lists exactly the layers it has.
let writeSelfDescribedBundle (dir: string) (layerSet: string[]) =
    for l in layerSet do
        File.WriteAllText(Path.Combine(dir, $"{l}.svg"), "<svg/>")

    let snap =
        { sequence = Int32.Parse(Path.GetFileName dir)
          sha256 = $"hash-{Path.GetFileName dir}"
          path = $"C:/snap/{Path.GetFileName dir}.kicad_pcb"
          capturedAt = DateTime.UtcNow
          captureStatus = "stable" }

    Store(dir).WriteManifest(dir, snap, "C:/src/board.kicad_pcb", "kicad-cli", "test", layerSet)

[<Fact>]
let ``partial bundle is not published and staging is cleaned (AT-005)`` () =
    let root = tempDir ()

    try
        let store = Store(Path.Combine(root, "project"))
        let staging = store.StagingFor 1
        writeBundle staging 3 // partial render: 3 of the default layers

        Assert.False(store.PublishBundle 1)
        Assert.False(Directory.Exists staging)
        Assert.Empty(store.CompleteBundles())
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``complete bundle publishes atomically via directory rename`` () =
    let root = tempDir ()

    try
        let store = Store(Path.Combine(root, "project"))
        let staging = store.StagingFor 7
        writeBundle staging layers.Length

        Assert.True(store.PublishBundle 7)
        Assert.False(Directory.Exists staging) // moved, not copied
        Assert.True(File.Exists(Path.Combine(store.BundlePath 7, "F.Cu.svg")))
        Assert.Equal<int>([ 7 ], store.CompleteBundles())
    finally
        Directory.Delete(root, true)
[<Fact>]
let ``legacy self-described bundle with smaller layer set stays complete`` () =
    let root = tempDir ()

    try
        let store = Store(Path.Combine(root, "project"))
        let legacy = [|"F.Cu"; "B.Cu"; "Edge.Cuts"; "F.Silkscreen"; "B.Silkscreen"|]
        let staging = store.StagingFor 9
        writeSelfDescribedBundle staging legacy

        Assert.True(store.PublishBundle 9)
        Assert.Equal<int>([ 9 ], store.CompleteBundles())
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``sequence recovery prefers renders scan over stale metadata (A1/D1)`` () =
    let root = tempDir ()

    try
        let store = Store(Path.Combine(root, "project"))

        // Metadata behind: claims seq 3; renders/ has 7 (crash after publish).
        store.AppendSnapshot(
            { sequence = 3
              sha256 = "h3"
              path = "C:/snap/3.kicad_pcb"
              capturedAt = DateTime.UtcNow
              captureStatus = "stable" },
            "C:/src/board.kicad_pcb"
        )

        let renders = Path.Combine(root, "project", "renders", "7")
        Directory.CreateDirectory renders |> ignore
        // Crash-after-publish state: renders/7 exists complete, metadata lags at 3.
        writeBundle renders layers.Length

        Assert.Equal(8, store.NextSequence())
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``snapshot metadata roundtrips with pinned field names (C7)`` () =
    let root = tempDir ()

    try
        let store = Store(Path.Combine(root, "project"))
        let captured = DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc)

        store.AppendSnapshot(
            { sequence = 1
              sha256 = "abc123"
              path = "C:/snap/abc123.kicad_pcb"
              capturedAt = captured
              captureStatus = "stable" },
            "C:/src/board.kicad_pcb"
        )

        let rows = store.LoadSnapshots()
        Assert.Single(rows) |> ignore
        let row = rows[0]
        Assert.Equal(1, row.sequence)
        Assert.Equal("abc123", row.content_hash)
        Assert.Equal("C:/src/board.kicad_pcb", row.source_path)
        Assert.Equal("stable", row.capture_status)
        Assert.Equal(captured, row.captured_at)

        // Pinned field names are checkable strings (§39).
        let json = File.ReadAllText store.MetadataPath
        for field in [ "sequence"; "captured_at"; "source_path"; "content_hash"; "files"; "capture_status" ] do
            Assert.Contains($"\"{field}\"", json)
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``history cap protects last published and recent complete bundles (A5)`` () =
    let root = tempDir ()

    try
        let store = Store(Path.Combine(root, "project"))

        for seq in 1 .. 55 do
            let staging = store.StagingFor seq
            writeBundle staging layers.Length
            Assert.True(store.PublishBundle seq, $"bundle {seq} should publish")

        Assert.Equal(55, store.CompleteBundles().Length)

        store.PruneHistory(55, cap = 50, protectRecent = 10)

        let remaining = store.CompleteBundles()
        Assert.Equal(50, remaining.Length)
        Assert.Contains(55, remaining) // last published is protected
        Assert.Contains(46, remaining) // recent 10 (55..46) protected
        Assert.DoesNotContain(1, remaining) // oldest pruned
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``store never writes into the source directory (AT-010)`` () =
    let root = tempDir ()

    try
        let sourceDir = Path.Combine(root, "source-project")
        Directory.CreateDirectory sourceDir |> ignore
        let source = Path.Combine(sourceDir, "board.kicad_pcb")
        File.WriteAllText(source, "(kicad_pcb)")

        let store = Store(Path.Combine(root, "observer-store"))
        let staging = store.StagingFor 1
        writeBundle staging layers.Length

        let before = Directory.GetFileSystemEntries(sourceDir, "*", SearchOption.AllDirectories)

        store.PublishBundle 1 |> ignore
        store.LogStoreSize 1
        store.AppendSnapshot(
            { sequence = 1
              sha256 = "h"
              path = staging
              capturedAt = DateTime.UtcNow
              captureStatus = "stable" },
            source
        )

        Assert.Equal<string[]>(before, Directory.GetFileSystemEntries(sourceDir, "*", SearchOption.AllDirectories))
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``size log gains one line per publish (C6)`` () =
    let root = tempDir ()

    try
        let store = Store(Path.Combine(root, "project"))
        store.LogStoreSize 1
        store.LogStoreSize 2

        let log = Path.Combine(root, "project", "logs", "size.log")
        Assert.True(File.Exists log)

        let lines = File.ReadAllLines log
        Assert.Equal(2, lines.Length)
        Assert.Contains("seq=1", lines[0])
        Assert.Contains("seq=2", lines[1])
    finally
        Directory.Delete(root, true)

/// Publish `count` bundles whose manifests carry distinct content hashes and
/// whose snapshot entries are padded files of `bytesPerBundle` bytes.
let private publishQuotaFixtures (store: Store) (count: int) (bytesPerBundle: int) =
    for seq in 1..count do
        let staging = store.StagingFor seq
        let snap = { sequence = seq; sha256 = $"hash-{seq}"; path = $"C:/snap/{seq}"; capturedAt = DateTime.UtcNow; captureStatus = "stable" }
        let layerSet = [| "F.Cu"; "B.Cu"; "Edge.Cuts" |]

        for l in layerSet do
            File.WriteAllText(Path.Combine(staging, $"{l}.svg"), "<svg/>")

        store.WriteManifest(staging, snap, "C:/src/b.kicad_pcb", "kicad-cli", "test", layerSet)
        Assert.True(store.PublishBundle seq)

        // Snapshot entry named by the content hash, padded to inflate size.
        let snapshotPath = Path.Combine(store.SnapshotsDir, $"hash-{seq}.kicad_pcb")
        File.WriteAllBytes(snapshotPath, Array.zeroCreate<byte> bytesPerBundle)

[<Fact>]
let ``quota prune drops oldest unprotected bundles until the store fits`` () =
    let root = tempDir ()

    try
        let store = Store(Path.Combine(root, "project"))
        // 6 bundles x ~1MB snapshots ≈ 6MB + renders.
        publishQuotaFixtures store 6 (1024 * 1024)

        let pruned = store.PruneToQuota(6, 3_500_000L, protectRecent = 1)

        // Protected: 6 (lastPublished + most recent). Snapshots 6 x ~1MB
        // dominate the store, so pruning stops at 3MB of snapshots.
        Assert.Equal<int list>([ 1; 2; 3 ], pruned)
        let remaining = store.CompleteBundles() |> List.sort
        Assert.Equal<int>([ 4; 5; 6 ], remaining)

        // Orphaned snapshots are gone; live ones (hash-4..6) survive.
        Assert.False(File.Exists(Path.Combine(store.SnapshotsDir, "hash-1.kicad_pcb")))
        Assert.True(File.Exists(Path.Combine(store.SnapshotsDir, "hash-4.kicad_pcb")))
        Assert.True(File.Exists(Path.Combine(store.SnapshotsDir, "hash-6.kicad_pcb")))
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``quota prune is a no-op when the store already fits or only protected bundles remain`` () =
    let root = tempDir ()

    try
        let store = Store(Path.Combine(root, "project"))
        publishQuotaFixtures store 3 (1024 * 1024)

        // Fits comfortably: nothing pruned.
        Assert.Empty(store.PruneToQuota(3, 10_000_000L, protectRecent = 2))

        // Quota smaller than the fully protected set: prune nothing.
        let pruned = store.PruneToQuota(3, 1L, protectRecent = 3)
        Assert.Empty(pruned)
        Assert.Equal(3, (store.CompleteBundles()).Length)
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``quota prune removes staging leftovers during snapshot gc`` () =
    let root = tempDir ()

    try
        let store = Store(Path.Combine(root, "project"))
        publishQuotaFixtures store 2 (1024 * 1024)

        let tmp = Path.Combine(store.SnapshotsDir, ".tmp-orphan")
        Directory.CreateDirectory tmp |> ignore
        File.WriteAllText(Path.Combine(tmp, "x"), "y")

        store.PruneToQuota(2, 10_000_000L) |> ignore

        Assert.False(Directory.Exists tmp)
    finally
        Directory.Delete(root, true)
