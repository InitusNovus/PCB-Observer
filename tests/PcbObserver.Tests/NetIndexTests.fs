module PcbObserver.NetIndexTests

open System.IO
open PcbObserver.PcbNetIndex
open PcbObserver.Tests
open Xunit

let private writePcb (dir: string) (text: string) =
    let p = Path.Combine(dir, "board.kicad_pcb")
    File.WriteAllText(p, text)
    p

let private pcbWithNetDecl = """(kicad_pcb
  (net 1 "VCC")
  (net 2 "GND")
  (segment (start 10 20) (end 30 20) (width 0.8) (layer "F.Cu") (net 1))
  (via (at 30 20) (size 1.6) (net 2))
  (footprint "Lib:C1" (layer "F.Cu") (at 100 50 90)
    (pad "1" smd rect (at 2 0) (size 1.0 0.5) (net 1))
    (pad "2" smd rect (at -2 0) (size 1.0 0.5) (net 2))
  )
)"""

[<Fact>]
let ``parses net declarations segments vias and pads with rotation`` () =
    let root = tempDir ()

    try
        let idx = buildIndex (writePcb root pcbWithNetDecl) "0 0 100 200" 100 1000

        let vcc = idx.nets |> List.find (fun n -> n.name = "VCC")
        let gnd = idx.nets |> List.find (fun n -> n.name = "GND")

        // Segment (10,20)-(30,20) width .8 → fractions of 100x200.
        Assert.Equal<float list>([ 0.1; 0.1; 0.3; 0.1; 0.004 ], vcc.rows.Head)

        // Via at (30,20) dia 1.6 → r fraction = 0.8/100.
        Assert.Equal<float list>([ 0.3; 0.1; 0.008 ], gnd.rows.Head)

        // Footprint at (100,50) rot 90° (y-down): pad (2,0) → (100,48); pad (-2,0) → (100,52).
        let padVcc = vcc.rows.[1]
        let padGnd = gnd.rows.[1]
        Assert.Equal(1.0, padVcc.[0], 5)
        Assert.Equal(0.24, padVcc.[1], 5)
        Assert.Equal(1.0, padGnd.[0], 5)
        Assert.Equal(0.26, padGnd.[1], 5)
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``kiCad10 name references resolve without declarations`` () =
    let root = tempDir ()

    try
        let pcb = """(kicad_pcb
  (segment (start 1 1) (end 2 2) (width 0.4) (net "GND"))
  (via (at 2 2) (size 1.0) (net "GND"))
)"""

        let idx = buildIndex (writePcb root pcb) "0 0 10 10" 100 1000

        let gnd = idx.nets |> List.find (fun n -> n.name = "GND")
        Assert.Equal(2, gnd.rows.Length)
        Assert.Equal(2, idx.totalPrimitives)
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``per-net cap flags truncation explicitly`` () =
    let root = tempDir ()

    try
        let segs =
            [ for i in 1..5 -> $"  (segment (start {i} 0) (end {i} 1) (width 0.2) (net \"BUS\"))" ]
            |> String.concat "\n"

        let pcb = $"(kicad_pcb\n{segs}\n)"
        let idx = buildIndex (writePcb root pcb) "0 0 10 10" 3 1000

        let bus = idx.nets |> List.find (fun n -> n.name = "BUS")
        Assert.Equal(3, bus.rows.Length)
        Assert.True(bus.truncated)
        Assert.Equal(1, idx.truncatedNets)
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``nets json carries the display-only note and shape`` () =
    let root = tempDir ()

    try
        let pcb = "(kicad_pcb\n  (segment (start 0 0) (end 5 5) (width 1.0) (net \"GND\"))\n)"
        let idx = buildIndex (writePcb root pcb) "0 0 10 10" 100 1000
        let out = Path.Combine(root, "nets.json")
        writeNetsJson out idx

        let json = File.ReadAllText out
        Assert.Contains("display-only", json)
        Assert.Contains("not connectivity analysis", json)
        Assert.Contains("\"GND\"", json)
        Assert.Contains("\"viewBox\":\"0 0 10 10\"", json)
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``numeric net references resolve and unnamed nets get a fallback`` () =
    let root = tempDir ()

    try
        let pcb = """(kicad_pcb
  (net 7 "+VDC")
  (net 9 "")
  (segment (start 0 0) (end 1 1) (width 0.3) (net 7))
  (via (at 1 1) (size 1.0) (net 9))
)"""

        let idx = buildIndex (writePcb root pcb) "0 0 10 10" 100 1000

        Assert.True(idx.nets |> List.exists (fun n -> n.name = "+VDC"), $"+VDC missing: {idx.nets |> List.map (fun n -> n.name)}")
        Assert.True(idx.nets |> List.exists (fun n -> n.name = "<net 9>"))
    finally
        Directory.Delete(root, true)
[<Fact>]
let ``total cap sets explicit flags and counts`` () =
    let root = tempDir ()

    try
        // 3 nets x 2 prims = 6 extracted; cap 5 keeps 5, drops 1.
        let segs =
            [ for i in 1..2 do
                  for n in [ "A"; "B"; "C" ] ->
                      $"  (segment (start {i} 0) (end {i} 1) (width 0.2) (net \"{n}\"))" ]
            |> String.concat "\n"

        let pcb = $"(kicad_pcb\n{segs}\n)"
        let idx = buildIndex (writePcb root pcb) "0 0 10 10" 100 5

        Assert.Equal(6, idx.extractedPrimitives)
        Assert.True(idx.totalTruncated)
        Assert.Equal(5, idx.totalPrimitives)
    finally
        Directory.Delete(root, true)
