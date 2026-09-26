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

        store.PublishBundle 1
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
