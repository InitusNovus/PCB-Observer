module PcbObserver.ServerTests

open System
open System.IO
open System.Net.Http
open PcbObserver.Server
open PcbObserver.Tests
open Xunit

type FixtureHolder() =
    let changed = Event<unit>()

    interface IStateHolder with
        override _.GetState() =
            { source_last_event = "source present · stable"
              latest_captured_snapshot = Some { sequence = 4; content_hash = "deadbeef"; captured_at = "2026-09-26T12:00:00Z"; capture_status = "stable" }
              latest_completed_render = Some { sequence = 4; content_hash = "deadbeef"; created_at = "2026-09-26T12:00:02Z"; status = "complete" }
              last_error = None
              sidecars = { drc = false; erc = false } }

        override _.GetSnapshotRows() =
            [ { sequence = 4
                captured_at = "2026-09-26T12:00:00Z"
                content_hash = "deadbeef"
                capture_status = "stable"
                render_status = "complete" } ]

        override _.Changed = changed.Publish :> IObservable<unit>

        override _.Dispose() = ()

    member _.Trigger() = changed.Trigger()

[<Fact>]
let ``server serves state, snapshots, viewer, and assets; blocks traversal; streams SSE`` () =
    let root = tempDir ()

    try
        let rendersRoot = Path.Combine(root, "renders")
        let bundle = Path.Combine(rendersRoot, "4")
        Directory.CreateDirectory bundle |> ignore
        File.WriteAllText(Path.Combine(bundle, "F.Cu.svg"), "<svg>ok</svg>")

        let viewer = Path.Combine(root, "viewer.html")
        File.WriteAllText(viewer, "<html>viewer-marker</html>")

        use holder = new FixtureHolder()
        let server = Server.start viewer rendersRoot holder 0

        try
            use client = new HttpClient() // loopback only
            client.Timeout <- TimeSpan.FromSeconds 10.0

            // /api/state with pinned §19 field names (C7).
            let state = client.GetStringAsync($"{server.BaseUrl}/api/state").Result
            for field in [ "source_last_event"; "latest_captured_snapshot"; "latest_completed_render"; "last_error" ] do
                Assert.Contains($"\"{field}\"", state)

            // /api/snapshots with pinned row fields.
            let rows = client.GetStringAsync($"{server.BaseUrl}/api/snapshots").Result
            for field in [ "sequence"; "captured_at"; "content_hash"; "capture_status"; "render_status" ] do
                Assert.Contains($"\"{field}\"", rows)

            // GET / serves the viewer.
            let html =
                client.GetAsync($"{server.BaseUrl}/").Result.Content.ReadAsStringAsync().Result

            Assert.Contains("viewer-marker", html)

            // Bundle asset with svg MIME.
            use svg = client.GetAsync($"{server.BaseUrl}/renders/4/F.Cu.svg").Result
            Assert.Equal(System.Net.HttpStatusCode.OK, svg.StatusCode)
            Assert.Equal("image/svg+xml", svg.Content.Headers.ContentType.MediaType)

            // §31: path traversal is refused.
            let traversalUrl = server.BaseUrl + "/renders/4/..%2F..%2Fviewer.html"
            use traversal = client.GetAsync(traversalUrl).Result
            Assert.Equal(System.Net.HttpStatusCode.NotFound, traversal.StatusCode)

            // A9: SSE's first event is the full state.
            use stream = client.GetStreamAsync($"{server.BaseUrl}/api/events").Result

            use reader = new StreamReader(stream)
            let firstLine = reader.ReadLine()

            Assert.Equal("event: state", firstLine)
        finally
            server.Stop ()
    finally
        Directory.Delete(root, true)
