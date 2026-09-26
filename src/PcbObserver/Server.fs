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
      last_error: ErrorView option }

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
            Func<HttpContext, IResult>(fun _ -> Results.File(viewerPath, "text/html; charset=utf-8"))
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
            Func<string, string, IResult>(fun seq file ->
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

    // Port fallback (R8): preferred port first, then an ephemeral one.
    let mutable builder, app = buildApp preferredPort

    try
        app.RunAsync() |> ignore // non-blocking
    with _ ->
        let b2, app2 = buildApp 0
        builder <- b2
        app <- app2
        app.RunAsync() |> ignore

    // Kestrel binds asynchronously; poll until the addresses are known.
    let deadline = DateTime.UtcNow.AddSeconds 10.0

    let mutable addresses =
        [ for u in app.Urls do
              if u.StartsWith "http" then
                  u ]

    while addresses.IsEmpty && DateTime.UtcNow < deadline do
        Threading.Thread.Sleep 50

        addresses <-
            [ for u in app.Urls do
                  if u.StartsWith "http" then
                      u ]

    let baseUrl =
        match addresses with
        | url :: _ -> url
        | [] -> "http://127.0.0.1:0"

    let port = Uri(baseUrl).Port

    { Port = port
      BaseUrl = baseUrl
      Stop = fun () -> app.StopAsync().Wait() }
