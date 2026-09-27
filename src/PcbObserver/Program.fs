module PcbObserver.Program

open System
open System.Diagnostics
open System.IO
open PcbObserver.Capture
open PcbObserver.Queue
open PcbObserver.Render
open PcbObserver.Server
open PcbObserver.Store
open PcbObserver.Watch
open PcbObserver.ObserverSession

let private defaultObserverRoot () =
    Path.Combine(
        Environment.GetFolderPath Environment.SpecialFolder.LocalApplicationData,
        "PCBObserver"
    )

let private defaultCli = @"C:\Program Files\KiCad\10.0\bin\kicad-cli.exe"

// ---------------------------------------------------------------------------
// Legacy one-shot Phase 0 CLI (unchanged behavior, including its --cli
// fail-fast — D3 only relaxes the watch subcommand).
// ---------------------------------------------------------------------------

let private usageOneshot = "usage: PcbObserver <board.kicad_pcb> [--output DIR] [--cli KICAD_CLI]"

let private parseFlags (argv: string list) : Result<string * string * string, string> =
    let rec go xs pcb output cli =
        match xs with
        | [] ->
            match pcb with
            | None -> Error usageOneshot
            | Some p ->
                Ok
                    (p,
                     defaultArg output (Path.Combine(defaultObserverRoot (), "phase0")),
                     defaultArg cli defaultCli)
        | "--output" :: value :: rest -> go rest pcb (Some value) cli
        | "--cli" :: value :: rest -> go rest pcb output (Some value)
        | flag :: _ when flag.StartsWith "-" -> Error $"Unknown option: {flag}"
        | path :: rest ->
            if pcb.IsSome then Error "Multiple PCB paths given"
            else go rest (Some path) output cli

    go argv None None None

let private runOneshot (argv: string list) : int =
    let fail message =
        eprintfn "%s" message
        2

    match parseFlags argv with
    | Error message -> fail message
    | Ok(pcbPath, outputDir, cliPath) ->
        let source = Path.GetFullPath pcbPath

        if String.Equals(Path.GetExtension source, ".kicad_pcb", StringComparison.OrdinalIgnoreCase)
           |> not then
            fail "Expected a .kicad_pcb file"
        elif not (File.Exists source) then
            fail $"PCB not found: {source}"
        elif not (File.Exists cliPath) then
            fail $"KiCad CLI not found: {cliPath}"
        else
            let output = Path.GetFullPath outputDir
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
                renderLayers runKiCad cliPath snapshot renderDir layers

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

// ---------------------------------------------------------------------------
// watch: live observer MVP (FR-001..018)
// ---------------------------------------------------------------------------


let private cliVersion (cli: string) : string =
    try
        let psi = ProcessStartInfo(cli)
        psi.ArgumentList.Add "version"
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        use p = Process.Start psi

        if p.WaitForExit 10_000 then
            p.StandardOutput.ReadToEnd().Trim()
        else
            "unknown"
    with _ ->
        "unknown"

let private runWatch (argv: string list) : int =
    let rec
        parse
            (xs: string list)
            (pcb: string option)
            (output: string option)
            (cli: string option)
            (port: int option)
            (debounce: int option)
            (layersOpt: string option)
            =
        match xs with
        | [] -> Ok(pcb, output, cli, port, debounce, layersOpt)
        | "--output" :: v :: rest -> parse rest pcb (Some v) cli port debounce layersOpt
        | "--cli" :: v :: rest -> parse rest pcb output (Some v) port debounce layersOpt
        | "--port" :: v :: rest ->
            match Int32.TryParse v with
            | true, p -> parse rest pcb output cli (Some p) debounce layersOpt
            | _ -> Error $"Invalid --port: {v}"
        | "--debounce-ms" :: v :: rest ->
            match Int32.TryParse v with
            | true, d when d > 0 -> parse rest pcb output cli port (Some d) layersOpt
            | _ -> Error $"Invalid --debounce-ms: {v}"
        | "--layers" :: v :: rest -> parse rest pcb output cli port debounce (Some v)
        | flag :: _ when flag.StartsWith "-" -> Error $"Unknown option: {flag}"
        | path :: rest ->
            if pcb.IsSome then Error "Multiple PCB paths given"
            else parse rest (Some path) output cli port debounce layersOpt

    let usage = "usage: PcbObserver watch <board.kicad_pcb> [--output DIR] [--cli PATH] [--port N] [--debounce-ms N] [--layers A,B,..]"

    match parse argv None None None (Some 8765) (Some 500) None with
    | Error message ->
        eprintfn "%s" message
        eprintfn "%s" usage
        2
    | Ok(None, _, _, _, _, _) ->
        eprintfn "%s" usage
        2
    | Ok(Some pcbPath, outputOpt, cliOpt, portOpt, debounceOpt, layersOpt) ->
        let source = Path.GetFullPath pcbPath

        if String.Equals(Path.GetExtension source, ".kicad_pcb", StringComparison.OrdinalIgnoreCase)
           |> not then
            eprintfn "Expected a .kicad_pcb file"
            2
        elif not (File.Exists source) then
            eprintfn $"PCB not found: {source}"
            2
        else
            // D3: watch does NOT fail-fast on a missing --cli. The failure
            // surfaces at first render as a recorded error state (AT-009 path).
            let cliPath = defaultArg cliOpt defaultCli
            let observerRoot = Path.GetFullPath(defaultArg outputOpt (defaultObserverRoot ()))
            let sourceDir = Path.GetDirectoryName source

            let insideSource =
                String.Equals(observerRoot, sourceDir, StringComparison.OrdinalIgnoreCase)
                || observerRoot.StartsWith(
                    sourceDir + string Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase
                )

            if insideSource then
                eprintfn "Output must be outside the source project"
                2
            else
                let projectId =
                    let hash =
                        sha256Hex (System.Text.Encoding.UTF8.GetBytes(source.ToLowerInvariant()))

                    $"{Path.GetFileNameWithoutExtension source}-{hash.Substring(0, 12)}"

                let projectRoot = Path.Combine(observerRoot, "projects", projectId)
                let store = Store projectRoot
                store.PurgeStaleStaging() // A7

                let rendererVersion = cliVersion cliPath
                let state = LiveState()
                let session = ObserverSession(store, state)
                session.Hydrate "bundle"

                // Rendered layer set for this run: --layers override or the
                // full default set. The manifest self-describes it.
                let layerSet =
                    match layersOpt with
                    | Some spec ->
                        let picked = spec.Split(',') |> Array.map (fun s -> s.Trim()) |> Array.filter (fun s -> s <> "")

                        if picked.Length = 0 then invalidArg "--layers" "empty layer list"
                        picked
                    | None -> Render.layers

                let runRender (snap: Snapshot) : unit =
                    let staging = store.StagingFor snap.sequence
                    renderLayers runKiCad cliPath snap.path staging layerSet
                    store.WriteManifest(staging, snap, source, "kicad-cli", rendererVersion, layerSet)

                let onComplete (snap: Snapshot) : unit = session.Complete(snap, "bundle")

                let onError (snap: Snapshot, ex: exn) : unit = session.Failed(snap, ex)

                let queue = RenderQueue(runRender, onComplete, onError)

                let captureNow () : unit =
                    let seq = lock state.Gate (fun () -> store.NextSequence())

                    try
                        let snap = captureSnapshot source store.SnapshotsDir seq
                        store.AppendSnapshot(snap, source)
                        state.SetLastEvent "source present · stable"
                        state.RecordCapture snap
                        printfn $"captured #{snap.sequence} · {snap.sha256.Substring(0, 12)}"
                        queue.Post snap
                    with e ->
                        state.SetLastEvent $"capture unstable: {e.Message}"
                        printfn $"capture unstable: {e.Message}"

                use watcher = new DirectoryWatcher(source, debounceMs = defaultArg debounceOpt 500)

                let subscription =
                    watcher.Events.Subscribe(function
                        | SourcePresent ->
                            state.SetLastEvent "source changed · stable read"
                            captureNow ()
                        | WaitingForSource ->
                            state.SetLastEvent "waiting for source"
                            printfn "Waiting for source (file name absent after debounce)")

                // D2: initial capture goes through the queue like any other.
                if store.CompleteBundles().IsEmpty then captureNow ()

                let holder =
                    { new IStateHolder with
                        override _.GetState() = state.View ()

                        override _.GetSnapshotRows() = session.Rows ()

                        override _.Changed = state.Changed

                        override _.Dispose() = (watcher :> IDisposable).Dispose() }

                let server = Server.start (Path.Combine(AppContext.BaseDirectory, "viewer.html")) store.RendersDir holder (defaultArg portOpt 8765)

                printfn $"Observer: {source}"
                printfn $"Store:    {projectRoot}"
                printfn $"Renderer: kicad-cli {rendererVersion} ({cliPath})"
                printfn $"Viewer:   {server.BaseUrl}/   (Ctrl+C stops the observer only — FR-018)"

                ObserverSession.waitForExit ()

                printfn "Observer stopping; the agent, KiCad, and the source project are untouched."
                server.Stop()
                holder.Dispose()
                0

// ---------------------------------------------------------------------------
// watch-sch: schematic hierarchy observer MVP (SCH-FR-001..016 subset)
// ---------------------------------------------------------------------------

let private runWatchSch (argv: string list) : int =
    let rec
        parse
            (xs: string list)
            (root: string option)
            (output: string option)
            (cli: string option)
            (port: int option)
            (debounce: int option)
            =
        match xs with
        | [] -> Ok(root, output, cli, port, debounce)
        | "--output" :: v :: rest -> parse rest root (Some v) cli port debounce
        | "--cli" :: v :: rest -> parse rest root output (Some v) port debounce
        | "--port" :: v :: rest ->
            match Int32.TryParse v with
            | true, p -> parse rest root output cli (Some p) debounce
            | _ -> Error $"Invalid --port: {v}"
        | "--debounce-ms" :: v :: rest ->
            match Int32.TryParse v with
            | true, d when d > 0 -> parse rest root output cli port (Some d)
            | _ -> Error $"Invalid --debounce-ms: {v}"
        | flag :: _ when flag.StartsWith "-" -> Error $"Unknown option: {flag}"
        | path :: rest ->
            if root.IsSome then Error "Multiple root paths given"
            else parse rest (Some path) output cli port debounce

    let usage = "usage: PcbObserver watch-sch <root.kicad_sch> [--output DIR] [--cli PATH] [--port N] [--debounce-ms N]"

    match parse argv None None None (Some 8765) (Some 500) with
    | Error message ->
        eprintfn "%s" message
        eprintfn "%s" usage
        2
    | Ok(None, _, _, _, _) ->
        eprintfn "%s" usage
        2
    | Ok(Some rootPath, outputOpt, cliOpt, portOpt, debounceOpt) ->
        let source = Path.GetFullPath rootPath

        if String.Equals(Path.GetExtension source, ".kicad_sch", StringComparison.OrdinalIgnoreCase)
           |> not then
            eprintfn "Expected a .kicad_sch root file"
            2
        elif not (File.Exists source) then
            eprintfn $"Root schematic not found: {source}"
            2
        else
            let cliPath = defaultArg cliOpt defaultCli
            let observerRoot = Path.GetFullPath(defaultArg outputOpt (defaultObserverRoot ()))
            let sourceDir = Path.GetDirectoryName source

            let insideSource =
                String.Equals(observerRoot, sourceDir, StringComparison.OrdinalIgnoreCase)
                || observerRoot.StartsWith(
                    sourceDir + string Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase
                )

            if insideSource then
                eprintfn "Output must be outside the source project"
                2
            else
                let projectId =
                    let hash =
                        sha256Hex (System.Text.Encoding.UTF8.GetBytes(source.ToLowerInvariant()))

                    $"sch-{Path.GetFileNameWithoutExtension source}-{hash.Substring(0, 12)}"

                let projectRoot = Path.Combine(observerRoot, "projects", projectId)
                let store = Store projectRoot
                store.PurgeStaleStaging()

                let rendererVersion = cliVersion cliPath
                let state = LiveState()
                let session = ObserverSession(store, state)
                session.Hydrate "sch bundle"

                // Dynamic dependency set (SCH-FR-005): refreshed after each
                // successful discovery so nested child directories are watched
                // by their full paths, including unresolved children.
                // Tolerant startup discovery (run-2 review): a momentarily
                // locked root must not crash watch-sch before the server
                // starts; first capture re-discovers loudly either way.
                let discovered =
                    try
                        Sch.discover source
                    with _ ->
                        { root = source; files = [ source ]; edges = []; missing = [] }

                let runRender (snap: Snapshot) : unit =
                    let staging = store.StagingFor snap.sequence

                    // Re-discover INSIDE the immutable snapshot tree: the same
                    // hierarchy, stable while kicad-cli reads it.
                    let snapRoot = Path.Combine(snap.path, Path.GetFileName source)
                    let disc = Sch.discover snapRoot
                    let pages, anomalies = Sch.mapPages disc

                    let schSnap =
                        SchPipeline.toSnapshotReconstruct snap (Path.GetFileName source)

                    SchPipeline.renderSch Render.runKiCad cliPath staging schSnap

                    let rows, renderAnomalies = SchPipeline.analyzePages disc pages staging

                    SchPipeline.writeSchManifest
                        staging
                        schSnap
                        disc
                        rows
                        (anomalies @ renderAnomalies)
                        rendererVersion

                    if not (SchPipeline.bundleIsPublishable rows) then
                        failwith "schematic bundle not publishable (pages not rendered and not missing)"

                let onComplete (snap: Snapshot) : unit = session.Complete(snap, "sch bundle")

                let onError (snap: Snapshot, ex: exn) : unit = session.Failed(snap, ex)

                let queue = RenderQueue(runRender, onComplete, onError)

                let watcher =
                    new Watch.DependencyWatcher(
                        source,
                        discovered.files,
                        discovered.missing,
                        debounceMs = defaultArg debounceOpt 500
                    )

                let captureNow () : unit =
                    let seq = lock state.Gate (fun () -> store.NextSequence())

                    try
                        let disc = Sch.discover source
                        // Do not replace the watch set until discovery succeeds.
                        // A transient read failure therefore keeps the last
                        // known dependency set alive for the next save.
                        watcher.Refresh(disc.files, disc.missing)

                        let snap = SchPipeline.captureSch disc store.SnapshotsDir seq

                        store.AppendSnapshot(SchPipeline.toSnapshot snap, source)

                        let missingNote =
                            if List.isEmpty disc.missing then
                                ""
                            else
                                $" · missing {disc.missing.Length}"

                        state.SetLastEvent(
                            if List.isEmpty disc.missing then
                                "source present · stable"
                            else
                                $"source present · missing {disc.missing.Length} child file(s)"
                        )

                        state.RecordCapture (SchPipeline.toSnapshot snap)

                        printfn
                            $"captured #{snap.sequence} · {snap.sha256.Substring(0, 12)} · {disc.files.Length} file(s){missingNote}"

                        queue.Post(SchPipeline.toSnapshot snap)
                    with e ->
                        state.SetLastEvent $"capture unstable: {e.Message}"
                        printfn $"capture unstable: {e.Message}"

                let watcherSubscription =
                    watcher.Events.Subscribe(function
                    | Watch.SourcePresent ->
                        state.SetLastEvent "source changed · stable read"
                        captureNow ()
                    | Watch.WaitingForSource ->
                        state.SetLastEvent "waiting for source"
                        printfn "Waiting for source (root schematic absent after debounce)")

                // D2 analog: initial capture through the queue when empty.
                if store.CompleteBundles().IsEmpty then captureNow ()

                let holder =
                    { new Server.IStateHolder with
                        override _.GetState() = state.View ()

                        override _.GetSnapshotRows() = session.Rows ()

                        override _.Changed = state.Changed

                        override _.Dispose() =
                            watcherSubscription.Dispose()
                            (watcher :> IDisposable).Dispose() }

                let viewerPath = Path.Combine(AppContext.BaseDirectory, "viewer-sch.html")

                let server = Server.start viewerPath store.RendersDir holder (defaultArg portOpt 8765)

                printfn $"Observer:    {source}"
                printfn $"Store:       {projectRoot}"
                printfn $"Renderer:    kicad-cli {rendererVersion} ({cliPath})"
                printfn $"Viewer:      {server.BaseUrl}/   (Ctrl+C stops the observer only — FR-018)"

                ObserverSession.waitForExit ()

                printfn "Observer stopping; the agent, KiCad, and the source project are untouched."
                server.Stop()
                holder.Dispose()
                0
[<EntryPoint>]
let main argv =
    match List.ofArray argv with
    | "watch" :: rest -> runWatch rest
    | "watch-sch" :: rest -> runWatchSch rest
    | argv -> runOneshot argv
