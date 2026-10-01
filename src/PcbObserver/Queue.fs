module PcbObserver.Queue

open System
open PcbObserver.Capture

type private Message =
    | Enqueue of Snapshot
    | RenderDone of Snapshot
    | RenderFailed of Snapshot * exn

/// §15 render queue: one render running + one pending slot (latest wins),
/// in-flight renders are never cancelled, and the actor loop survives
/// exceptions (A2 — a dead queue means observation silently stops while the
/// watcher keeps capturing, NFR-002).
type RenderQueue(runRender: Snapshot -> unit, onComplete: Snapshot -> unit, onError: Snapshot * exn -> unit) =

    let mutable agent: MailboxProcessor<Message> = Unchecked.defaultof<_>
    // D4: the hash most recently posted (running or pending); None until a
    // post, cleared when the queue drains back to idle.
    let mutable busyHash: string option = None

    let startRender (snap: Snapshot) =
        Async.Start
            (async {
                try
                    runRender snap
                    agent.Post(RenderDone snap)
                with e ->
                    agent.Post(RenderFailed(snap, e))
             })

    do
        agent <-
            MailboxProcessor.Start(fun inbox ->
                let rec loop (running: bool) (pending: Snapshot option) = async {
                    let! msg = inbox.Receive()

                    try
                        match msg with
                        | Enqueue snap ->
                            if running then
                                // §15.1/§15.2: replace the pending slot — never queue depth.
                                return! loop running (Some snap)
                            else
                                startRender snap
                                return! loop true pending
                        | RenderDone snap ->
                            try
                                onComplete snap
                            with e ->
                                onError (snap, e)

                            match pending with
                            | Some next ->
                                startRender next
                                return! loop true None
                            | None ->
                                lock agent (fun () -> busyHash <- None)
                                return! loop false None
                        | RenderFailed(snap, e) ->
                            // A3: record + keep the last good bundle; the next
                            // source-save event re-renders. The loop survives (A2).
                            try
                                onError (snap, e)
                            with _ ->
                                ()

                            match pending with
                            | Some next ->
                                startRender next
                                return! loop true None
                            | None ->
                                lock agent (fun () -> busyHash <- None)
                                return! loop false None
                    with _ ->
                        // Belt and braces: nothing may kill the loop (A2).
                        return! loop running pending
                }

                loop false None)

    member _.Post(snapshot: Snapshot) =
        lock agent (fun () -> busyHash <- Some snapshot.sha256)
        agent.Post(Enqueue snapshot)

    /// D4 skip gate: the hash most recently posted (running or pending);
    /// None when the queue has drained back to idle.
    member _.BusyHash: string option = lock agent (fun () -> busyHash)