module PcbObserver.Tests

open System
open System.IO
open PcbObserver.Capture
open PcbObserver.Render
open Xunit

let private tempDir () =
    let root = Path.Combine(Path.GetTempPath(), $"pcbobs-{Guid.NewGuid():N}")
    Directory.CreateDirectory root |> ignore
    root

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
