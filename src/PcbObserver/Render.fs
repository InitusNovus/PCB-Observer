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

/// Copper layers declared by a board's (layers ...) table — F.Cu, B.Cu, and
/// any inner In<N>.Cu in file order. Used to size the default render set to
/// the board (2-layer boards stay 2 copper; 4+ layer boards include In*.Cu).
/// Empty when the table is missing/unreadable (caller falls back to the
/// fixed default set).
let boardCopperLayers (pcbPath: string) : string list =
    try
        let head = File.ReadAllText(pcbPath)

        let m =
            System.Text.RegularExpressions.Regex.Match(head, @"\n\t\(layers[\s\S]{0,4000}?\n\t\)")

        if not m.Success then
            []
        else
            [ for x in System.Text.RegularExpressions.Regex.Matches(m.Value, @"""([^""]*\.Cu)""") -> x.Groups[1].Value ]
    with _ ->
        []

/// Default layer set FOR a specific board: its declared copper layers (in
/// file order) plus the fixed overlay set. Falls back to the static default
/// when the board declares no copper layers.
let layersForBoard (pcbPath: string) : string[] =
    match boardCopperLayers pcbPath with
    | [] -> layers
    | coppers ->
        [| yield! coppers
           yield! layers |> Array.skip 2 |] // skip F.Cu/B.Cu; keep overlays

/// Extract the first viewBox-bearing <svg> tag's viewBox (§11.2 layer
/// composition check; kicad-cli writes it on the root tag). None when
/// absent or unreadable.
let viewBoxOf (svgPath: string) : string option =
    try
        use fs = File.OpenRead(svgPath)
        let buffer = Array.zeroCreate<byte> 4096
        let mutable total = 0

        // Read until the head buffer is full: a short Read or a multibyte
        // char split at the boundary would otherwise lose the attribute.
        while total < buffer.Length do
            let read = fs.Read(buffer, total, buffer.Length - total)

            if read <= 0 then
                total <- buffer.Length
            else
                total <- total + read

        let head = System.Text.Encoding.UTF8.GetString(buffer, 0, total)
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
