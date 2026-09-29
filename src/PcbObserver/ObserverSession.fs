module PcbObserver.ObserverSession

open System
open System.IO
open System.Text.Json
open PcbObserver.Capture
open PcbObserver.Server
open PcbObserver.Store

/// Live per-session mutable state (§19 views). Moved here from Program so the
/// shared session orchestration below owns it next to its only writers.
type LiveState() =
    let gate = obj ()
    let changed = Event<unit>()

    let mutable lastEvent = "starting"
    let mutable latestCaptured: CapturedView option = None
    let mutable latestRendered: CompletedView option = None
    let mutable lastError: ErrorView option = None
    let mutable failedSeqs: int list = []

    member _.Gate = gate
    member _.Changed = changed.Publish :> IObservable<unit>

    member _.Trigger() = changed.Trigger()

    member _.SetLastEvent value = lock gate (fun () -> lastEvent <- value)

    member _.RecordCapture (snap: Snapshot) =
        lock gate (fun () ->
            latestCaptured <-
                Some
                    { sequence = snap.sequence
                      content_hash = snap.sha256
                      captured_at = snap.capturedAt.ToString("o")
                      capture_status = snap.captureStatus }
        )

    member _.RecordRendered seq hash createdAt =
        lock gate (fun () ->
            latestRendered <- Some { sequence = seq; content_hash = hash; created_at = createdAt; status = "complete" }

            failedSeqs <- failedSeqs |> List.except [ seq ]

            // A successful publish at or past the failed sequence supersedes
            // the recorded failure (boundary review blocker: the §19 badge
            // must not claim "render failed" forever after recovery).
            lastError <-
                match lastError with
                | Some e when e.sequence >= seq -> lastError
                | _ -> None
        )

    member _.RecordFailure seq reason =
        lock gate (fun () ->
            lastError <- Some { sequence = seq; reason = reason }
            if seq > 0 then failedSeqs <- seq :: (failedSeqs |> List.except [ seq ])
        )

    member _.View () =
        lock gate (fun () ->
            { source_last_event = lastEvent
              latest_captured_snapshot = latestCaptured
              latest_completed_render = latestRendered
              last_error = lastError }
        )

    member _.FailedSeqs = lock gate (fun () -> failedSeqs)

/// Session orchestration shared by watch (PCB) and watch-sch (schematic):
/// hydration from the newest published bundle, publication with
/// last-published bookkeeping, failure recording, and history-row
/// projection. Domain differences (layer set vs page manifest) stay in the
/// callers; `bundleLabel` only names the console output.
type ObserverSession(store: Store, state: LiveState, ?quotaBytes: int64) =
    // Callers pass bytes; the default is 512 MiB.
    let quotaBytes = defaultArg quotaBytes (512L * 1024L * 1024L)
    let mutable lastPublished = 0

    /// Restart against a store with history must show the newest published
    /// bundle immediately instead of "connecting…" until the next save.
    /// `lastPublished` advances even when the manifest read fails, exactly
    /// like the pre-extraction code did.
    member _.Hydrate(bundleLabel: string) =
        match store.CompleteBundles() with
        | latest :: _ ->
            lastPublished <- latest

            let manifestPath = Path.Combine(store.BundlePath latest, "manifest.json")

            try
                use doc = JsonDocument.Parse(File.ReadAllText manifestPath)
                let root = doc.RootElement
                let mutable el = Unchecked.defaultof<JsonElement>
                let hash = if root.TryGetProperty("content_hash", &el) then el.GetString() else ""
                let created = if root.TryGetProperty("created_at", &el) then el.GetString() else ""

                state.RecordRendered latest hash (if isNull created || created = "" then DateTime.UtcNow.ToString("o") else created)
                state.SetLastEvent "resumed from latest published bundle"
                printfn $"Resumed: showing published {bundleLabel} #{latest}"
            with _ ->
                ()
        | [] -> ()

    /// RenderQueue completion callback: publish under the state gate, prune,
    /// log, and record the render. `bundleLabel` names the console line.
    member _.Complete(snap: Snapshot, bundleLabel: string) =
        let published =
            lock
                state.Gate
                (fun () ->
                    if snap.sequence > lastPublished && store.PublishBundle snap.sequence then
                        lastPublished <- snap.sequence
                        true
                    else
                        false)

        if published then
            store.PruneHistory lastPublished

            let pruned = store.PruneToQuota(lastPublished, quotaBytes)

            if not (List.isEmpty pruned) then
                let quotaMb = quotaBytes / (1024L * 1024L)
                let list = String.Join(", ", pruned)
                printfn $"history quota {quotaMb}MB: pruned bundles {list}"

            if List.isEmpty pruned && store.TotalStoreBytes() > quotaBytes then
                let quotaMb = quotaBytes / (1024L * 1024L)
                let sizeMb = store.TotalStoreBytes() / (1024L * 1024L)
                printfn $"history quota {quotaMb}MB: protected floor reached — store {sizeMb}MB retained (displayed + 2 most recent)"

            store.LogStoreSize snap.sequence
            state.RecordRendered snap.sequence snap.sha256 (DateTime.UtcNow.ToString("o"))

            printfn $"LIVE · #{snap.sequence} · {snap.sha256.Substring(0, 12)} · {bundleLabel} published"
        else
            printfn $"seq {snap.sequence} completed but not published (superseded or incomplete)"

        state.Trigger()

    /// RenderQueue failure callback: record the failure for the §19 badge.
    member _.Failed(snap: Snapshot, ex: exn) =
        let failedSeq = if obj.ReferenceEquals(snap, null) then 0 else snap.sequence

        lock state.Gate (fun () -> state.RecordFailure failedSeq ex.Message)

        printfn $"render failed (seq {failedSeq}): {ex.Message} · will update on next save"

        state.Trigger()

    /// /api/snapshots rows (C7 pinned fields): complete/failed/skipped from
    /// the published-bundle set and the recorded failure set.
    member _.Rows() : SnapshotRowView list =
        let complete = set (store.CompleteBundles ())
        let failed = set (state.FailedSeqs)

        [ for row in store.LoadSnapshots() ->
              { sequence = row.sequence
                captured_at = row.captured_at.ToString("o")
                content_hash = row.content_hash
                capture_status = row.capture_status
                render_status =
                    (if complete.Contains row.sequence then "complete"
                     elif failed.Contains row.sequence then "failed"
                     else "skipped") } ]

/// Ctrl+C gate shared by both watch loops: the observer stops, the agent,
/// KiCad, and the source project keep running (FR-018).
let waitForExit () =
    let exitGate = new Threading.ManualResetEventSlim(false)
    Console.CancelKeyPress.Add(fun e -> e.Cancel <- true; exitGate.Set())
    exitGate.Wait()
