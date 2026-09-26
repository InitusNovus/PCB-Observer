module PcbObserver.SchTests

open System
open System.IO
open PcbObserver.Sch
open PcbObserver.SchPipeline
open PcbObserver.Tests
open Xunit

// From tests/bin/Debug/net10.0 up to the repo root (cwd differs under VSTest).
let repoFixtures =
    let mutable dir = AppContext.BaseDirectory

    while not (Directory.Exists(Path.Combine(dir, "fixtures", "sch")))
          && not (isNull (Directory.GetParent dir)) do
        dir <- Directory.GetParent(dir).FullName

    Path.Combine(dir, "fixtures", "sch")

let fixtureDir (case: string) : string =
    Path.GetFullPath(Path.Combine(repoFixtures, case))

/// Fresh temp copy of a fixture case (mutations allowed).
let copyCase (case: string) : string =
    let dest = tempDir ()
    for file in Directory.GetFiles(fixtureDir case) do
        File.Copy(file, Path.Combine(dest, Path.GetFileName file))

    dest

[<Fact>]
let ``case A flat: single file, no edges, single root page`` () =
    let disc = discover (Path.Combine(fixtureDir "case_a_flat", "root.kicad_sch"))

    Assert.Single(disc.files) |> ignore
    Assert.Empty(disc.edges)
    Assert.Empty(disc.missing)

    let pages, anomalies = mapPages disc
    Assert.Single(pages) |> ignore
    Assert.Equal<string>([], pages[0].chain)
    Assert.Equal("root.svg", pages[0].file)
    Assert.Empty(anomalies)

[<Fact>]
let ``case B basic: two children discovered with pages`` () =
    let disc = discover (Path.Combine(fixtureDir "case_b_basic", "root.kicad_sch"))

    Assert.Equal(3, disc.files.Length)
    Assert.Equal(2, disc.edges.Length)
    Assert.Empty(disc.missing)

    let pages, _ = mapPages disc
    Assert.Equal(3, pages.Length) // 1 root + 2 children
    Assert.Equal<string>(
        [ "root-MCU.svg"; "root-Power.svg"; "root.svg" ],
        pages |> List.map (fun p -> p.file) |> List.sort
    )

[<Fact>]
let ``case C nested: chain file names root-Drive-Phase-Gate`` () =
    let disc = discover (Path.Combine(fixtureDir "case_c_nested", "root.kicad_sch"))

    Assert.Equal(4, disc.files.Length)

    let pages, anomalies = mapPages disc
    Assert.Empty(anomalies)
    Assert.True(pages |> List.exists (fun p -> p.file = "root-Drive-Phase-Gate.svg"), "deep chain name")
    Assert.Equal<string>([ "Drive"; "Phase"; "Gate" ], (pages |> List.find (fun p -> p.file = "root-Drive-Phase-Gate.svg")).chain)

[<Fact>]
let ``case D shared: three logical instances of one physical file, spaces kept`` () =
    let disc = discover (Path.Combine(fixtureDir "case_d_shared", "root.kicad_sch"))

    Assert.Equal(2, disc.files.Length) // root + channel only (SCH-FR-004)
    Assert.Equal(3, disc.edges.Length) // but three logical instances

    let pages, anomalies = mapPages disc
    Assert.Empty(anomalies)
    Assert.Equal<string>(
        [ "root-Channel A.svg"; "root-Channel B.svg"; "root-Channel C.svg"; "root.svg" ],
        pages |> List.map (fun p -> p.file) |> List.sort
    )

    let channelFile = Path.Combine(fixtureDir "case_d_shared", "channel.kicad_sch") |> Path.GetFullPath

    Assert.Equal(
        3,
        pages |> List.filter (fun p -> p.sheetFile = channelFile) |> List.length
    )

[<Fact>]
let ``missing child is reported, never thrown (SCH-FR-016)`` () =
    let dir = copyCase "case_b_basic"
    File.Delete(Path.Combine(dir, "mcu.kicad_sch"))

    let disc = discover (Path.Combine(dir, "root.kicad_sch"))

    Assert.Equal(2, disc.files.Length)
    Assert.Single(disc.missing) |> ignore
    Assert.Equal(Path.Combine(dir, "mcu.kicad_sch"), disc.missing[0])

[<Fact>]
let ``duplicate Sheetname chain flagged as anomaly instead of silent overwrite`` () =
    let dir = copyCase "case_b_basic"
    let rootPath = Path.Combine(dir, "root.kicad_sch")
    let text = File.ReadAllText rootPath
    File.WriteAllText(rootPath, text.Replace("\"Power\"", "\"MCU\""))

    let disc = discover rootPath
    let _, anomalies = mapPages disc
    Assert.Single(anomalies) |> ignore
    Assert.Contains("root-MCU.svg", anomalies[0])

[<Fact>]
let ``scanner ignores parentheses and escapes inside property strings`` () =
    let text =
        """(kicad_sch (version 1) (weird "(sheet fake) \" x")
	(sheet
		(uuid "u1")
		(property "Sheetname" "A (tricky) \"name\""
			(at 1 2))
		(property "Sheetfile" "child one.kicad_sch"
			(at 3 4))
	)
	(sheet_instances (path "/" (page "1")))
)"""

    let refs = sheetReferences text
    Assert.Single(refs) |> ignore
    Assert.Equal("A (tricky) \"name\"", fst refs[0])
    Assert.Equal("child one.kicad_sch", snd refs[0])

[<Fact>]
let ``missing-child page flags missing even when an empty frame is emitted`` () =
    let dir = copyCase "case_b_basic"
    File.Delete(Path.Combine(dir, "mcu.kicad_sch"))
    let disc = discover (Path.Combine(dir, "root.kicad_sch"))
    let pages, _ = mapPages disc

    // kicad-cli emits an empty-frame SVG for missing children (findings
    // SS49-7) — simulate that: every expected file "exists".
    let staging = Path.Combine(dir, "out")
    Directory.CreateDirectory staging |> ignore

    for p in pages do
        File.WriteAllText(Path.Combine(staging, p.file), "<svg/>")

    let rows, anomalies = PcbObserver.SchPipeline.analyzePages disc pages staging
    let mcu = rows |> Array.find (fun r -> r.chain = [|"MCU"|])
    Assert.Equal("missing", mcu.status)
    Assert.Equal("rendered", (rows |> Array.find (fun r -> r.chain = [||])).status)
    Assert.Equal("rendered", (rows |> Array.find (fun r -> r.chain = [|"Power"|])).status)
    Assert.Empty(anomalies)
    Assert.True(PcbObserver.SchPipeline.bundleIsPublishable rows)

[<Fact>]
let ``cyclic sheet reference terminates as anomaly, not infinite loop`` () =
    let dir = copyCase "case_b_basic"
    // power.kicad_sch now points back at root: root -> power -> root -> ...
    let power = Path.Combine(dir, "power.kicad_sch")
    let text = File.ReadAllText power
    // Insert the cycle sheet just before the root form's final close paren.
    let sheetBlock =
        "\t(sheet\n\t\t(property \"Sheetname\" \"Back\"\n\t\t\t(at 1 2))\n\t\t(property \"Sheetfile\" \"root.kicad_sch\"\n\t\t\t(at 3 4))\n\t\t(uuid \"00000000-0000-0000-0000-00004b3a9999\")\n\t)\n"

    let cyclic = text.Insert(text.LastIndexOf(')'), sheetBlock)
    File.WriteAllText(power, cyclic)

    let disc = discover (Path.Combine(dir, "root.kicad_sch"))
    let pages, anomalies = mapPages disc
    Assert.Contains(anomalies, fun a -> a.Contains "cyclic sheet reference")
    // Terminates: finite page list (root, Power, MCU, Back + one Back child page).
    Assert.True(pages.Length < 10, $"expected finite page set, got {pages.Length}")

[<Fact>]
let ``not-emitted page makes the bundle unpublishable`` () =
    // Simulate a CLI that skips a page file entirely (no empty frame).
    let dir = copyCase "case_a_flat"
    let disc = discover (Path.Combine(dir, "root.kicad_sch"))
    let pages, _ = mapPages disc
    let staging = Path.Combine(dir, "out")
    Directory.CreateDirectory staging |> ignore
    // Emit nothing: the single root page is then "not-emitted".
    let rows, anomalies = PcbObserver.SchPipeline.analyzePages disc pages staging
    Assert.Equal("not-emitted", rows[0].status)
    Assert.Single(anomalies) |> ignore
    Assert.False(PcbObserver.SchPipeline.bundleIsPublishable rows)
