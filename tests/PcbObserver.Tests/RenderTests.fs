module PcbObserver.RenderTests

open System.IO
open PcbObserver.Render
open PcbObserver.Store
open PcbObserver.Capture
open PcbObserver.Tests
open Xunit

let private writeSvg (dir: string) (name: string) (viewBox: string) =
    let path = Path.Combine(dir, name)
    File.WriteAllText(path, $"""<?xml version="1.0"?><svg xmlns="http://www.w3.org/2000/svg" width="10mm" height="8mm" viewBox="{viewBox}"><g/></svg>""")
    path

[<Fact>]
let ``viewBoxOf reads the root svg viewBox`` () =
    let dir = tempDir ()

    try
        let svg = writeSvg dir "F.Cu.svg" "0 0 116.4336 105.0798"
        Assert.Equal(Some "0 0 116.4336 105.0798", viewBoxOf svg)

        // No viewBox attribute in the head.
        let none = Path.Combine(dir, "none.svg")
        File.WriteAllText(none, """<svg xmlns="http://www.w3.org/2000/svg" width="10mm"><g/></svg>""")
        Assert.Equal(None, viewBoxOf none)
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``commonViewBox passes aligned layers and rejects divergence or gaps`` () =
    let a = ("F.Cu", Some "0 0 100 80")
    let b = ("B.Cu", Some "0 0 100 80")
    let c = ("Edge.Cuts", Some "0 0 100 80")
    Assert.Equal(Some "0 0 100 80", commonViewBox [ a; b; c ])

    // One divergent frame breaks exact composition.
    let d = ("F.Mask", Some "0 0 99 80")
    Assert.Equal(None, commonViewBox [ a; b; c; d ])

    // A layer without a viewBox also breaks it (missing evidence).
    Assert.Equal(None, commonViewBox [ a; b; ("F.Silkscreen", None) ])

    Assert.Equal(None, commonViewBox [])

[<Fact>]
let ``manifest records per-layer viewBoxes with a consistency flag`` () =
    let root = tempDir ()

    try
        let store = Store(Path.Combine(root, "project"))
        let staging = store.StagingFor 1
        let svg = writeSvg staging "F.Cu.svg" "0 0 100 80"

        for layer in [ "B.Cu"; "Edge.Cuts" ] do
            writeSvg staging $"{layer}.svg" "0 0 100 80" |> ignore

        let snap = { sequence = 1; sha256 = "hash0000000001"; path = svg; capturedAt = System.DateTime.UtcNow; captureStatus = "stable" }

        store.WriteManifestWithViewBoxes(
            staging,
            snap,
            "C:/src/board.kicad_pcb",
            "kicad-cli",
            "test",
            [| "F.Cu"; "B.Cu"; "Edge.Cuts" |],
            [ ("F.Cu", "0 0 100 80"); ("B.Cu", "0 0 100 80"); ("Edge.Cuts", "0 0 100 80") ]
        )

        let json = File.ReadAllText(Path.Combine(staging, "manifest.json"))
        use doc = System.Text.Json.JsonDocument.Parse(json)
        Assert.True(doc.RootElement.GetProperty("view_box_consistent").GetBoolean())
        Assert.Equal(3, doc.RootElement.GetProperty("layer_view_boxes").GetArrayLength())

        // Completeness stays manifest-driven with the new fields present.
        Assert.True(store.PublishBundle 1)
        Assert.Equal<int>([ 1 ], store.CompleteBundles())

        // Divergent layers are recorded as inconsistent but still publishable
        // (explicit state, not a hard failure).
        let staging2 = store.StagingFor 2
        let svg2 = writeSvg staging2 "F.Cu.svg" "0 0 100 80"
        writeSvg staging2 "B.Cu.svg" "0 0 101 80" |> ignore

        let snap2 = { sequence = 2; sha256 = "hash0000000002"; path = svg2; capturedAt = System.DateTime.UtcNow; captureStatus = "stable" }

        store.WriteManifestWithViewBoxes(
            staging2,
            snap2,
            "C:/src/board.kicad_pcb",
            "kicad-cli",
            "test",
            [| "F.Cu"; "B.Cu" |],
            [ ("F.Cu", "0 0 100 80"); ("B.Cu", "0 0 101 80") ]
        )

        let json2 = File.ReadAllText(Path.Combine(staging2, "manifest.json"))

        use doc2 = System.Text.Json.JsonDocument.Parse(json2)
        Assert.False(doc2.RootElement.GetProperty("view_box_consistent").GetBoolean())
        Assert.True(store.PublishBundle 2)
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``legacy manifest shape is unchanged without viewBoxes`` () =
    let root = tempDir ()

    try
        let store = Store(Path.Combine(root, "project"))
        let staging = store.StagingFor 1
        let svg = writeSvg staging "F.Cu.svg" "0 0 100 80"

        let snap = { sequence = 1; sha256 = "hash0000000001"; path = svg; capturedAt = System.DateTime.UtcNow; captureStatus = "stable" }

        store.WriteManifest(staging, snap, "C:/src/board.kicad_pcb", "kicad-cli", "test", [| "F.Cu" |])

        let json = File.ReadAllText(Path.Combine(staging, "manifest.json"))
        Assert.False(json.Contains "view_box")
    finally
        Directory.Delete(root, true)
