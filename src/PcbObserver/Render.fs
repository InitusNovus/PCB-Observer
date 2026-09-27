module PcbObserver.Render

open System
open System.Diagnostics
open System.IO

/// Default rendered layer set (user-requested extension: mask/paste/fab/comments
/// on top of the spec §11.1 first-class five). Override per run via watch --layers.
let layers =
    [| "F.Cu"
       "B.Cu"
       "Edge.Cuts"
       "F.Silkscreen"
       "B.Silkscreen"
       "F.Mask"
       "B.Mask"
       "F.Paste"
       "B.Paste"
       "F.Fab"
       "B.Fab"
       "Cmts.User" |]

/// Extract the root <svg viewBox="..."> attribute (§11.2 layer-composition
/// check). None when absent or the head is malformed.
let viewBoxOf (svgPath: string) : string option =
    try
        use fs = File.OpenRead(svgPath)
        let buffer = Array.zeroCreate<byte> 4096
        let read = fs.Read(buffer, 0, buffer.Length)
        let head = System.Text.Encoding.UTF8.GetString(buffer, 0, read)
        let m = System.Text.RegularExpressions.Regex.Match(head, "<svg[^>]*?viewBox=\"([^\"]+)\"")

        if m.Success then Some m.Groups[1].Value else None
    with _ ->
        None

/// §11.2: layers compose exactly when they share one viewBox. Returns the
/// common viewBox (Some) or None when any layer is missing or divergent.
let commonViewBox (layerSvgs: (string * string option) list) : string option =
    match layerSvgs with
    | [] -> None
    | xs when xs |> List.forall (snd >> Option.isSome) ->
        let distinct = xs |> List.map (Option.get << snd) |> List.distinct
        if distinct.Length = 1 then Some distinct.Head else None
    | _ -> None

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
