module PcbObserver.SessionTests

open System
open System.IO
open PcbObserver.Capture
open PcbObserver.ObserverSession
open PcbObserver.Store
open PcbObserver.Tests
open Xunit

/// Materialize a complete staged bundle for `seq` so PublishBundle succeeds.
let private stageCompleteBundle (store: Store) (seq: int) (sha: string) =
    let staging = store.StagingFor seq

    for layer in [ "F.Cu"; "B.Cu"; "Edge.Cuts" ] do
        File.WriteAllText(Path.Combine(staging, $"{layer}.svg"), "<svg/>")

    let snap = { sequence = seq; sha256 = sha; path = $"C:/snap/{seq}"; capturedAt = DateTime.UtcNow; captureStatus = "stable" }
    store.WriteManifest(staging, snap, "C:/src/board.kicad_pcb", "kicad-cli", "test", [| "F.Cu"; "B.Cu"; "Edge.Cuts" |])
    snap

[<Fact>]
let ``failure is superseded by a later publish and clears the badge error`` () =
    let root = tempDir ()

    try
        let store = Store(Path.Combine(root, "project"))
        store.AppendSnapshot({ sequence = 2; sha256 = "hash0000000002"; path = "C:/s/2"; capturedAt = DateTime.UtcNow; captureStatus = "stable" }, "C:/src") |> ignore
        let state = LiveState()
        let session = ObserverSession(store, state)

        // Render of #2 fails.
        session.Failed({ sequence = 2; sha256 = "hash0000000002"; path = "C:/s/2"; capturedAt = DateTime.UtcNow; captureStatus = "stable" }, exn "kicad-cli missing")

        Assert.Equal(2, state.View().last_error.Value.sequence)
        Assert.Contains(2, state.FailedSeqs)

        // A successful publish at a later sequence supersedes the badge
        // error, while the failed sequence itself stays recorded for its
        // /api/snapshots row (render_status "failed").
        let snap = stageCompleteBundle store 3 "hash0000000003"
        session.Complete(snap, "bundle")

        Assert.Null(state.View().last_error)
        Assert.Equal<int>([ 2 ], state.FailedSeqs)
        Assert.Equal(3, state.View().latest_completed_render.Value.sequence)
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``rows project complete failed and skipped statuses`` () =
    let root = tempDir ()

    try
        let store = Store(Path.Combine(root, "project"))

        let append seq sha =
            store.AppendSnapshot({ sequence = seq; sha256 = sha; path = $"C:/s/{seq}"; capturedAt = DateTime.UtcNow; captureStatus = "stable" }, "C:/src") |> ignore

        append 1 "hash0000000001"
        append 2 "hash0000000002"
        append 3 "hash0000000003"

        let state = LiveState()
        let session = ObserverSession(store, state)

        session.Complete(stageCompleteBundle store 1 "hash0000000001", "bundle")


        session.Failed({ sequence = 2; sha256 = "hash0000000002"; path = "C:/s/2"; capturedAt = DateTime.UtcNow; captureStatus = "stable" }, exn "boom")

        let rows = session.Rows ()
        let bySeq = rows |> List.map (fun r -> r.sequence, r.render_status) |> dict

        Assert.Equal("complete", bySeq[1])
        Assert.Equal("failed", bySeq[2])
        Assert.Equal("skipped", bySeq[3])
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``hydrate restores the newest published bundle into state`` () =
    let root = tempDir ()

    try
        let store = Store(Path.Combine(root, "project"))
        let snap = stageCompleteBundle store 5 "hash0000000005"
        Assert.True(store.PublishBundle 5)
        store.AppendSnapshot(snap, "C:/src")

        let state = LiveState()
        let session = ObserverSession(store, state)
        session.Hydrate "bundle"

        let view = state.View ()
        Assert.Equal("resumed from latest published bundle", view.source_last_event)
        Assert.Equal(5, view.latest_completed_render.Value.sequence)
        Assert.Equal("hash0000000005", view.latest_completed_render.Value.content_hash)

        // lastPublished advanced: a lower sequence never re-publishes.
        session.Complete(snap, "bundle")
        Assert.Equal(5, state.View().latest_completed_render.Value.sequence)
        Assert.Equal<int>([ 5 ], store.CompleteBundles())
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``hydrate with a corrupt manifest still advances lastPublished without throwing`` () =
    let root = tempDir ()

    try
        let store = Store(Path.Combine(root, "project"))
        stageCompleteBundle store 4 "hash0000000004" |> ignore
        Assert.True(store.PublishBundle 4)
        File.WriteAllText(Path.Combine(store.BundlePath 4, "manifest.json"), "{ not json")

        let state = LiveState()
        let session = ObserverSession(store, state)

        session.Hydrate "bundle"

        // No crash, no phantom render state…
        Assert.Null(state.View().latest_completed_render)

        // …and the next save publishes 5, not a re-publish of 4.
        let snap = stageCompleteBundle store 5 "hash0000000005"
        session.Complete(snap, "bundle")

        Assert.Equal(5, state.View().latest_completed_render.Value.sequence)
        Assert.Equal<int>([ 5 ], store.CompleteBundles())
    finally
        Directory.Delete(root, true)
