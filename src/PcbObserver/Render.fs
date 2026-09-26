module PcbObserver.Render

open System
open System.Diagnostics
open System.IO

let layers = [| "F.Cu"; "B.Cu"; "Edge.Cuts"; "F.Silkscreen"; "B.Silkscreen" |]

/// Run kicad-cli, fail on non-zero exit or 120s timeout (mirrors check=True + timeout).
let runKiCad (cli: string) (args: string[]) : unit =
    let psi = ProcessStartInfo(cli)
    for arg in args do
        psi.ArgumentList.Add arg

    use proc =
        match Process.Start psi with
        | null -> failwith $"Failed to start kicad-cli: {cli}"
        | started -> started

    if not (proc.WaitForExit 120_000) then
        proc.Kill(entireProcessTree = true)
        failwith "kicad-cli timed out after 120s"

    if proc.ExitCode <> 0 then
        failwith $"kicad-cli exited with {proc.ExitCode}"

/// Export one SVG per layer from an observer-owned snapshot into outputDir.
let renderLayers
    (run: string -> string[] -> unit)
    (cli: string)
    (snapshot: string)
    (outputDir: string)
    (layerNames: string seq)
    : unit =
    Directory.CreateDirectory(outputDir) |> ignore

    for layer in layerNames do
        run cli [|
            "pcb"
            "export"
            "svg"
            "--mode-single"
            "--layers"
            layer
            "--page-size-mode"
            "2"
            "--exclude-drawing-sheet"
            "--output"
            Path.Combine(outputDir, $"{layer}.svg")
            snapshot
        |]
