module PcbObserver.Tests

open System
open System.IO
open System.Threading
open PcbObserver.Capture
open PcbObserver.Render
open Xunit

let tempDir () =
    let root = Path.Combine(Path.GetTempPath(), $"pcbobs-{Guid.NewGuid():N}")
    Directory.CreateDirectory root |> ignore
    root

/// C8: every async/real-event assertion polls within a budget — no fixed sleeps.
let pollUntil (budgetMs: int) (check: unit -> bool) : bool =
    let deadline = DateTime.UtcNow.AddMilliseconds(float budgetMs)

    let rec go () =
        if check () then true
        elif DateTime.UtcNow >= deadline then false
        else
            Thread.Sleep 50
            go ()

    go ()

let minimalPcb () =
    """(kicad_pcb (version 20250114) (generator "pcbnew") (layer "F.Cu" (type "copper") (pair "(weird)")))"""

[<Fact>]
let ``capture copies source without writing to it`` () =
    let root = tempDir ()

    try
        let source = Path.Combine(root, "board.kicad_pcb")
        File.WriteAllBytes(source, "(kicad_pcb test)"B)

        let snapshot = captureBoard source (Path.Combine(root, "cache"))

        Assert.Equal("(kicad_pcb test)", File.ReadAllText snapshot)
        Assert.Equal("(kicad_pcb test)", File.ReadAllText source)
        Assert.NotEqual<string>(Path.GetDirectoryName(Path.GetFullPath source), Path.GetDirectoryName(Path.GetFullPath snapshot))
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``render uses snapshot and produces named layers`` () =
    let root = tempDir ()

    try
        let snapshot = Path.Combine(root, "snapshot.kicad_pcb")
        File.WriteAllText(snapshot, "board")

        let calls = ResizeArray<string[]>()

        renderLayers (fun _ args -> calls.Add args) "kicad-cli" snapshot (Path.Combine(root, "renders")) [ "F.Cu"; "Edge.Cuts" ]

        Assert.Equal(2, calls.Count)

        let first = calls[0]
        Assert.Contains("--mode-single", first)
        let layerIndex = Array.IndexOf(first, "--layers")
        Assert.Equal("F.Cu", first[layerIndex + 1])
        Assert.Equal(snapshot, first[first.Length - 1])
        Assert.Contains(Path.Combine(root, "renders", "F.Cu.svg"), first)
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``structure check accepts valid pcb and ignores parens in strings`` () =
    Assert.True(looksLikeKicadPcb(minimalPcb ()))
    Assert.True(looksLikeKicadPcb "  (kicad_pcb)  ")

[<Fact>]
let ``structure check rejects broken pcbs`` () =
    Assert.False(looksLikeKicadPcb "(kicad_sch (test))" ) // wrong root symbol
    Assert.False(looksLikeKicadPcb "(kicad_pcb (layer" ) // unbalanced
    Assert.False(looksLikeKicadPcb "(kicad_pcb) garbage" ) // trailing garbage
    Assert.False(looksLikeKicadPcb "\"(kicad_pcb)" ) // root never opens

[<Fact>]
let ``readStable returns payload for valid file and None for structurally incomplete one`` () =
    let root = tempDir ()

    try
        let good = Path.Combine(root, "good.kicad_pcb")
        File.WriteAllText(good, minimalPcb ())
        let bad = Path.Combine(root, "bad.kicad_pcb")
        File.WriteAllText(bad, "(kicad_pcb (unterminated")

        let read = readStable good 3 (fun () -> ())

        Assert.True(Option.isSome read)
        Assert.Equal<byte[]>(System.Text.Encoding.UTF8.GetBytes(minimalPcb ()), read.Value)
        Assert.True(Option.isNone (readStable bad 3 (fun () -> ())))
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``captureSnapshot assigns sequence and dedupes identical content`` () =
    let root = tempDir ()

    try
        let source = Path.Combine(root, "board.kicad_pcb")
        File.WriteAllText(source, minimalPcb ())
        let cache = Path.Combine(root, "snapshots")

        let s1 = captureSnapshot source cache 1
        let s2 = captureSnapshot source cache 2

        Assert.Equal(1, s1.sequence)
        Assert.Equal(2, s2.sequence)
        Assert.Equal(s1.sha256, s2.sha256)
        Assert.Equal(s1.path, s2.path) // content-addressed dedup
        Assert.Equal("stable", s2.captureStatus)
    finally
        Directory.Delete(root, true)
