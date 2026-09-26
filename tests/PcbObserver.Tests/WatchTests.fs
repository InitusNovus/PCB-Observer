module PcbObserver.WatchTests

open System
open System.IO
open PcbObserver.Watch
open PcbObserver.Tests
open Xunit

let t (ms: float) = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds ms

[<Fact>]
let ``debounce merges bursts into one fire`` () =
    let fired = ref 0
    let d = Debounce(500, 2000, fun () -> incr fired)

    // Three quick events inside the quiet window.
    Assert.False(d.MarkDirty(t 0.0))
    Assert.False(d.MarkDirty(t 100.0))
    Assert.False(d.MarkDirty(t 200.0))
    Assert.False(d.Pump(t 600.0)) // still inside 500ms of the last dirty
    Assert.True(d.Pump(t 701.0)) // window closed
    Assert.False(d.Pump(t 900.0)) // no re-fire without new activity
    Assert.Equal(1, !fired)

[<Fact>]
let ``activity cap fires during continuous saves`` () =
    let fired = ref 0
    let d = Debounce(500, 2000, fun () -> incr fired)

    // Continuous activity never lets the quiet window close.
    Assert.False(d.MarkDirty(t 0.0))

    for ms in [ 400.0 .. 400.0 .. 1600.0 ] do
        Assert.False(d.MarkDirty(t ms))

    // 2000ms after first dirty: cap closes the window mid-activity.
    Assert.True(d.MarkDirty(t 2001.0))
    Assert.Equal(1, !fired)

[<Fact>]
let ``directory watcher reports present and waiting across replace and rename-away`` () =
    let root = tempDir ()

    try
        let source = Path.Combine(root, "board.kicad_pcb")
        File.WriteAllText(source, "one")

        let other = Path.Combine(root, "other.bin")
        File.WriteAllText(other, "payload")

        use watcher = new DirectoryWatcher(source, debounceMs = 200, activityCapMs = 1000)

        let events = ResizeArray<WatchEvent>()
        watcher.Events.Subscribe(events.Add) |> ignore

        // AT-003: atomic replace keeps the watched name alive.
        File.Replace(other, source, null)

        Assert.True(
            pollUntil 8000 (fun () -> events.Contains WatchEvent.SourcePresent),
            "replace should be reported as present"
        )

        // A8: rename-away makes the watched file name absent after debounce.
        File.Move(source, Path.Combine(root, "gone.bin"))

        Assert.True(
            pollUntil 8000 (fun () -> events.Contains WatchEvent.WaitingForSource),
            "rename-away should be reported as waiting"
        )

        // Recovery: the name comes back.
        File.WriteAllText(source, "three")

        Assert.True(
            pollUntil 8000 (fun () -> events |> Seq.filter (fun e -> e = WatchEvent.SourcePresent) |> Seq.length >= 2),
            "recreation should be reported as present again"
        )
    finally
        Directory.Delete(root, true)
