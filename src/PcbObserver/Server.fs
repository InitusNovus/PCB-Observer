module PcbObserver.Server

open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http

/// §19 state components exposed by /api/state (pinned field names, C7).
type CapturedView = { sequence: int; content_hash: string; captured_at: string; capture_status: string }

type CompletedView = { sequence: int; content_hash: string; created_at: string; status: string }

type ErrorView = { sequence: int; reason: string }

type StateView =
    { source_last_event: string
      latest_captured_snapshot: CapturedView option
      latest_completed_render: CompletedView option
      last_error: ErrorView option
      sidecars: SidecarFlags }

and SidecarFlags = { drc: bool; erc: bool }

/// /api/snapshots rows (pinned field names, C7).
type SnapshotRowView =
    { sequence: int
      captured_at: string
      content_hash: string
      capture_status: string
      render_status: string }

/// Program supplies live state; tests supply a fixture.
type IStateHolder =
    inherit IDisposable
    abstract GetState: unit -> StateView
    abstract GetSnapshotRows: unit -> SnapshotRowView list
    /// Fires whenever a bundle publishes or status changes (SSE fan-out).
    abstract Changed: IObservable<unit>

type RunningServer = { Port: int; BaseUrl: string; Stop: unit -> unit }

let private serialize (value: obj) : string =
    JsonSerializer.Serialize(value, JsonSerializerOptions(WriteIndented = true))

let private mimeFor (file: string) : string =
    if file.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) then "image/svg+xml"
    elif file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) then "application/json"
    else "application/octet-stream"

/// Loopback-only local server (§28/§29/§31). Serves the viewer, state, bounded
/// snapshot rows, observer-owned render assets only, and an SSE stream whose
/// payloads carry identifiers only — never bulk SVG bytes.
let start (viewerPath: string) (rendersRoot: string) (state: IStateHolder) (preferredPort: int) : RunningServer =
    let buildApp (port: int) =
        let builder = WebApplication.CreateBuilder()
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}") |> ignore

        let app = builder.Build()

        app.MapGet(
            "/",
            Func<HttpContext, IResult>(fun ctx ->
                // Viewer HTML must never be heuristically cached: rebuilds
                // change the JS contract (run-2: stale tabs bounced history
                // clicks back to live).
                ctx.Response.Headers.CacheControl <- "no-store"
                Results.File(viewerPath, "text/html; charset=utf-8"))
        )
        |> ignore

        app.MapGet(
            "/api/state",
            Func<HttpContext, IResult>(fun _ -> Results.Text(serialize (state.GetState ()), "application/json"))
        )
        |> ignore

        app.MapGet(
            "/api/snapshots",
            Func<HttpContext, IResult>(fun _ ->
                Results.Text(serialize (state.GetSnapshotRows ()), "application/json"))
        )
        |> ignore

        app.MapGet(
            "/renders/{seq}/{file}",
            Func<string, string, HttpContext, IResult>(fun seq file ctx ->
                // §31: only well-formed bundle paths inside rendersRoot are served.
                let validSeq = fst (Int32.TryParse seq)

                let validName =
                    file <> ""
                    && not (file.Contains '/')
                    && not (file.Contains '\\')
                    && not (file.Contains "..")

                if validSeq && validName then
                    let path = Path.Combine(rendersRoot, seq, file)

                    if File.Exists path then
                        // Sidecar results (drc/erc.json) can be deleted by
                        // quota pruning; a stale browser-cache hit would
                        // serve a result that no longer exists on disk.
                        ctx.Response.Headers.CacheControl <- "no-store"
                        Results.File(path, mimeFor file)
                    else
                        Results.NotFound()
                else
                    Results.NotFound())
        )
        |> ignore

        app.MapGet(
            "/api/events",
            Func<HttpContext, Task>(fun ctx ->
                task {
                    ctx.Response.ContentType <- "text/event-stream"
                    ctx.Response.Headers.CacheControl <- "no-cache"
                    do! ctx.Response.StartAsync(ctx.RequestAborted)

                    let write (eventType: string) (payload: string) =
                        let bytes = Encoding.UTF8.GetBytes($"event: {eventType}\ndata: {payload}\n\n")
                        ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length, ctx.RequestAborted)

                    // A9 viewer contract: the first event on the stream is the
                    // current state, so a subscriber that connects before
                    // fetching /api/state cannot miss a publish.
                    do! write "state" (serialize (state.GetState ()))

                    let signal = new Threading.ManualResetEventSlim(false)

                    let subscription =
                        state.Changed.Subscribe(fun () -> signal.Set())

                    try
                        let mutable keepRunning = true

                        while keepRunning do
                            if signal.Wait(15_000) then
                                signal.Reset()

                                do!
                                    write
                                        "bundle"
                                        (serialize
                                            {| latest_completed_render = (state.GetState ()).latest_completed_render |})
                            else
                                // ~15s keepalive comment (R4).
                                let comment = Encoding.UTF8.GetBytes(": keepalive\n\n")
                                do! ctx.Response.Body.WriteAsync(comment, 0, comment.Length, ctx.RequestAborted)

                            if ctx.RequestAborted.IsCancellationRequested then
                                keepRunning <- false
                    finally
                        subscription.Dispose()
                        signal.Dispose()
                })
        )
        |> ignore

        (builder, app)

    // Port fallback (R8): bind synchronously via StartAsync so an occupied
    // port raises here (AddressInUseException) instead of faulting a
    // discarded RunAsync task and crashing later (QA defect D-1).
    let tryStart (port: int) : WebApplication option =
        let _, candidate = buildApp port

        try
            candidate.StartAsync().Wait(10_000) |> ignore
            Some candidate
        with _ ->
            try
                candidate.StopAsync().Wait(5_000) |> ignore
            with _ ->
                ()

            None

    let app =
        match tryStart preferredPort with
        | Some started -> started
        | None ->
            match tryStart 0 with
            | Some started -> started
            | None -> failwith $"could not bind any loopback port (preferred {preferredPort})"

    let baseUrl =
        match [ for u in app.Urls do
                    if u.StartsWith "http" then
                        u ] with
        | url :: _ -> url
        | [] -> "http://127.0.0.1:0"

    let port = Uri(baseUrl).Port

    { Port = port
      BaseUrl = baseUrl
      Stop = fun () ->
          app.StopAsync().Wait(10_000) |> ignore
          (app :> IDisposable).Dispose() }
