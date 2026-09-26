module PcbObserver.Program

open System
open System.IO
open System.Text.Json
open PcbObserver.Capture
open PcbObserver.Render

let private defaultOutput () =
    Path.Combine(
        Environment.GetFolderPath Environment.SpecialFolder.LocalApplicationData,
        "PCBObserver",
        "phase0"
    )

let private defaultCli = @"C:\Program Files\KiCad\10.0\bin\kicad-cli.exe"

type private Options = { Pcb: string; Output: string; Cli: string }

let private usage = "usage: PcbObserver <board.kicad_pcb> [--output DIR] [--cli KICAD_CLI]"

let private parse (argv: string[]) : Result<Options, string> =
    let rec go xs pcb output cli =
        match xs with
        | [] ->
            match pcb with
            | None -> Error usage
            | Some p ->
                Ok
                    { Pcb = p
                      Output = defaultArg output (defaultOutput ())
                      Cli = defaultArg cli defaultCli }
        | "--output" :: value :: rest -> go rest pcb (Some value) cli
        | "--cli" :: value :: rest -> go rest pcb output (Some value)
        | flag :: _ when flag.StartsWith "-" -> Error $"Unknown option: {flag}"
        | path :: rest ->
            if pcb.IsSome then Error "Multiple PCB paths given"
            else go rest (Some path) output cli

    go (List.ofArray argv) None None None

[<EntryPoint>]
let main argv =
    let fail message =
        eprintfn "%s" message
        2

    match parse argv with
    | Error message -> fail message
    | Ok options ->
        let source = Path.GetFullPath options.Pcb

        if String.Equals(Path.GetExtension source, ".kicad_pcb", StringComparison.OrdinalIgnoreCase) |> not then
            fail "Expected a .kicad_pcb file"
        elif not (File.Exists source) then
            fail $"PCB not found: {source}"
        elif not (File.Exists options.Cli) then
            fail $"KiCad CLI not found: {options.Cli}"
        else
            let output = Path.GetFullPath options.Output
            let sourceDir = Path.GetDirectoryName source

            let insideSource =
                String.Equals(output, sourceDir, StringComparison.OrdinalIgnoreCase)
                || output.StartsWith(
                    sourceDir + string Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase
                )

            if insideSource then
                fail "Output must be outside the source project"
            else
                let snapshot = captureBoard source (Path.Combine(output, "snapshots"))
                let renderDir = Path.Combine(output, "renders", Path.GetFileNameWithoutExtension snapshot)
                renderLayers runKiCad options.Cli snapshot renderDir layers

                File.Copy(Path.Combine(AppContext.BaseDirectory, "viewer.html"), Path.Combine(renderDir, "index.html"), true)

                let manifest =
                    {| snapshot_sha256 = Path.GetFileNameWithoutExtension snapshot
                       layers = layers |}

                File.WriteAllText(
                    Path.Combine(renderDir, "manifest.json"),
                    JsonSerializer.Serialize(manifest, JsonSerializerOptions(WriteIndented = true))
                )

                let viewerPath = Path.Combine(renderDir, "index.html")
                let viewerUrl = "file:///" + viewerPath.Replace('\\', '/')
                printfn $"Source unchanged: {source}"
                printfn $"Snapshot: {snapshot}"
                printfn $"Viewer: {viewerPath}"
                printfn $"Open in browser: {viewerUrl}"
                0
