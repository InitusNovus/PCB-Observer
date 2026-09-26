module PcbObserver.QueueTests

open System
open System.Threading
open PcbObserver.Capture
open PcbObserver.Queue
open PcbObserver.Tests
open Xunit

let snap (seq: int) : Snapshot =
    { sequence = seq
      sha256 = $"hash-{seq}"
      path = $"C:/snap/{seq}.kicad_pcb"
      capturedAt = DateTime.UtcNow
      captureStatus = "stable" }

[<Fact>]
let ``latest-wins: burst of three renders only the first and the newest`` () =
    let gate = new ManualResetEventSlim(false)
    let rendered = ResizeArray<int>()
    let completed = ResizeArray<int>()

    let run (s: Snapshot) =
        rendered.Add s.sequence
        gate.Wait() // slow first render blocks the lane

    let onComplete s = lock completed (fun () -> completed.Add s.sequence)

    let queue = RenderQueue(run, onComplete, fun (_, _) -> ())

    queue.Post(snap 1)
    Thread.Sleep 100 // ensure #1 started rendering
    queue.Post(snap 2)
    queue.Post(snap 3) // replaces 2 in the pending slot (§15.1/§15.2)

    gate.Set()

    Assert.True(
        pollUntil 8000 (fun () -> lock completed (fun () -> completed.Count = 2)),
        "first + latest should complete"
    )

    Assert.Equal<int>([ 1; 3 ], lock completed (fun () -> Seq.toList completed))
    Assert.Equal<int>([ 1; 3 ], Seq.toList rendered)

[<Fact>]
let ``runner exception is recorded and the queue survives (A2, NFR-008)`` () =
    let errors = ResizeArray<int>()
    let completed = ResizeArray<int>()

    let run (s: Snapshot) =
        if s.sequence = 1 then failwith "kicad-cli exploded"

    let onComplete s = lock completed (fun () -> completed.Add s.sequence)

    let onError (s: Snapshot, _) = lock errors (fun () -> errors.Add s.sequence)

    let queue = RenderQueue(run, onComplete, onError)

    queue.Post(snap 1) // fails

    Assert.True(pollUntil 8000 (fun () -> lock errors (fun () -> errors.Contains 1)), "failure should be recorded")

    queue.Post(snap 2) // the loop must still accept work

    Assert.True(
        pollUntil 8000 (fun () -> lock completed (fun () -> completed.Contains 2)),
        "queue must keep processing after an exception"
    )
