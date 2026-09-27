module PcbObserver.WatchTests

open System
open System.IO
open System.Threading
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

[<Fact>]
let ``dependency watcher reports nested child saves`` () =
    let root = tempDir ()

    try
        let source = Path.Combine(root, "root.kicad_sch")
        let childDir = Path.Combine(root, "blocks", "power")
        Directory.CreateDirectory childDir |> ignore
        let child = Path.Combine(childDir, "power.kicad_sch")
        File.WriteAllText(source, "root")
        File.WriteAllText(child, "child")

        use watcher =
            new DependencyWatcher(source, [ source; child ], [], debounceMs = 120, activityCapMs = 1000)

        let events = ResizeArray<WatchEvent>()
        use subscription = watcher.Events.Subscribe(events.Add)

        File.AppendAllText(child, "-saved")

        Assert.True(
            pollUntil 8000 (fun () -> events.Contains WatchEvent.SourcePresent),
            "nested child save should close the project debounce"
        )
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``dependency watcher matches full paths instead of same basenames`` () =
    let root = tempDir ()

    try
        let source = Path.Combine(root, "root.kicad_sch")
        let trackedDir = Path.Combine(root, "tracked")
        Directory.CreateDirectory trackedDir |> ignore
        let tracked = Path.Combine(trackedDir, "same.kicad_sch")
        let missing = Path.Combine(root, "future", "deep", "same.kicad_sch")
        File.WriteAllText(source, "root")
        File.WriteAllText(tracked, "tracked")

        // The absent dependency forces a recursive subscription at `root`, so
        // this unrelated same-basename file exercises path-aware filtering.
        use watcher =
            new DependencyWatcher(source, [ source; tracked ], [ missing ], debounceMs = 120, activityCapMs = 1000)

        let events = ResizeArray<WatchEvent>()
        use subscription = watcher.Events.Subscribe(events.Add)
        let noiseDir = Path.Combine(root, "noise")
        Directory.CreateDirectory noiseDir |> ignore
        File.WriteAllText(Path.Combine(noiseDir, "same.kicad_sch"), "noise")

        Assert.False(
            pollUntil 1200 (fun () -> events.Count > 0),
            "an unrelated file with the same basename must not trigger capture"
        )

        File.AppendAllText(tracked, "-saved")

        Assert.True(
            pollUntil 8000 (fun () -> events.Contains WatchEvent.SourcePresent),
            "the tracked full path should remain relevant"
        )
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``dependency watcher recovers a missing child through absent parent directories`` () =
    let root = tempDir ()

    try
        let source = Path.Combine(root, "root.kicad_sch")
        let missing = Path.Combine(root, "new", "nested", "child.kicad_sch")
        File.WriteAllText(source, "root")

        use watcher =
            new DependencyWatcher(source, [ source ], [ missing ], debounceMs = 120, activityCapMs = 1000)

        let events = ResizeArray<WatchEvent>()
        use subscription = watcher.Events.Subscribe(events.Add)
        Directory.CreateDirectory(Path.GetDirectoryName missing) |> ignore

        Assert.True(
            pollUntil 8000 (fun () -> events.Contains WatchEvent.SourcePresent),
            "creating the missing parent path should be observed"
        )

        events.Clear()
        File.WriteAllText(missing, "child")

        Assert.True(
            pollUntil 8000 (fun () -> events.Contains WatchEvent.SourcePresent),
            "creating the missing child should be observed"
        )
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``dependency watcher reconciles a deleted and recreated child directory`` () =
    let root = tempDir ()

    try
        let source = Path.Combine(root, "root.kicad_sch")
        let childDir = Path.Combine(root, "nested")
        let child = Path.Combine(childDir, "child.kicad_sch")
        Directory.CreateDirectory childDir |> ignore
        File.WriteAllText(source, "root")
        File.WriteAllText(child, "child")

        use watcher =
            new DependencyWatcher(source, [ source; child ], [], debounceMs = 120, activityCapMs = 1000)

        let events = ResizeArray<WatchEvent>()
        use subscription = watcher.Events.Subscribe(events.Add)
        Directory.Delete(childDir, true)

        Assert.True(
            pollUntil 8000 (fun () -> events.Contains WatchEvent.SourcePresent),
            "deleting a dependency directory should reconcile to its existing ancestor"
        )

        events.Clear()
        Directory.CreateDirectory childDir |> ignore
        File.WriteAllText(child, "child-restored")

        Assert.True(
            pollUntil 8000 (fun () -> events.Contains WatchEvent.SourcePresent),
            "recreating a dependency directory should restore its direct subscription"
        )
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``dependency watcher refresh adds and removes full-path subscriptions`` () =
    let root = tempDir ()

    try
        let source = Path.Combine(root, "root.kicad_sch")
        let childA = Path.Combine(root, "a.kicad_sch")
        let childB = Path.Combine(root, "b.kicad_sch")
        File.WriteAllText(source, "root")
        File.WriteAllText(childA, "a")
        File.WriteAllText(childB, "b")

        use watcher =
            new DependencyWatcher(source, [ source; childA ], [], debounceMs = 120, activityCapMs = 1000)

        let events = ResizeArray<WatchEvent>()
        use subscription = watcher.Events.Subscribe(events.Add)
        File.AppendAllText(childA, "-saved")

        Assert.True(
            pollUntil 8000 (fun () -> events.Contains WatchEvent.SourcePresent),
            "initial child should be watched"
        )

        events.Clear()
        watcher.Refresh([ source; childB ], [])
        File.AppendAllText(childA, "-removed")

        Assert.False(
            pollUntil 1200 (fun () -> events.Count > 0),
            "removed child should no longer trigger capture"
        )

        File.AppendAllText(childB, "-added")

        Assert.True(
            pollUntil 8000 (fun () -> events.Contains WatchEvent.SourcePresent),
            "new child should trigger after a refresh"
        )
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``dependency watcher handles rename old and new paths`` () =
    let root = tempDir ()

    try
        let source = Path.Combine(root, "root.kicad_sch")
        let oldChild = Path.Combine(root, "old.kicad_sch")
        let newChild = Path.Combine(root, "renamed.kicad_sch")
        File.WriteAllText(source, "root")
        File.WriteAllText(oldChild, "child")

        use watcher =
            new DependencyWatcher(source, [ source; oldChild ], [], debounceMs = 120, activityCapMs = 1000)

        let events = ResizeArray<WatchEvent>()
        use subscription = watcher.Events.Subscribe(events.Add)
        File.Move(oldChild, newChild)

        Assert.True(
            pollUntil 8000 (fun () -> events.Contains WatchEvent.SourcePresent),
            "the old path in a rename should trigger a dependency check"
        )

        events.Clear()
        watcher.Refresh([ source; newChild ], [])
        File.AppendAllText(newChild, "-saved")

        Assert.True(
            pollUntil 8000 (fun () -> events.Contains WatchEvent.SourcePresent),
            "the new path should trigger after the dependency refresh"
        )
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``dependency watcher reports root absence and recovery`` () =
    let root = tempDir ()

    try
        let source = Path.Combine(root, "root.kicad_sch")
        File.WriteAllText(source, "root")

        use watcher = new DependencyWatcher(source, [ source ], [], debounceMs = 120, activityCapMs = 1000)
        let events = ResizeArray<WatchEvent>()
        use subscription = watcher.Events.Subscribe(events.Add)

        File.Delete source

        Assert.True(
            pollUntil 8000 (fun () -> events.Contains WatchEvent.WaitingForSource),
            "root deletion should enter the waiting state"
        )

        events.Clear()
        File.WriteAllText(source, "root-restored")

        Assert.True(
            pollUntil 8000 (fun () -> events.Contains WatchEvent.SourcePresent),
            "root recreation should leave the waiting state"
        )
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``dependency watcher coalesces activity across directories`` () =
    let root = tempDir ()

    try
        let source = Path.Combine(root, "root.kicad_sch")
        let childDir = Path.Combine(root, "nested")
        Directory.CreateDirectory childDir |> ignore
        let child = Path.Combine(childDir, "child.kicad_sch")
        File.WriteAllText(source, "root")
        File.WriteAllText(child, "child")

        use watcher =
            new DependencyWatcher(source, [ source; child ], [], debounceMs = 180, activityCapMs = 1000)

        let events = ResizeArray<WatchEvent>()
        use subscription = watcher.Events.Subscribe(events.Add)
        File.AppendAllText(source, "-saved")
        File.AppendAllText(child, "-saved")
        File.AppendAllText(source, "-saved-again")

        Assert.True(
            pollUntil 8000 (fun () -> events.Count >= 1),
            "project activity should eventually close one debounce window"
        )

        Thread.Sleep 700
        Assert.Equal(1, events.Count)
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``dependency watcher disposal stops filesystem notifications`` () =
    let root = tempDir ()

    try
        let source = Path.Combine(root, "root.kicad_sch")
        File.WriteAllText(source, "root")

        let watcher = new DependencyWatcher(source, [ source ], [], debounceMs = 120, activityCapMs = 1000)
        let events = ResizeArray<WatchEvent>()
        use subscription = watcher.Events.Subscribe(events.Add)
        (watcher :> IDisposable).Dispose()
        File.AppendAllText(source, "-after-dispose")

        Thread.Sleep 700
        Assert.Empty(events)
    finally
        Directory.Delete(root, true)
