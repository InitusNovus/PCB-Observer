module PcbObserver.Watch

open System
open System.IO

/// What the watcher reports to the capture pipeline after a debounce window.
/// Rule A8: the event KIND is irrelevant — what matters is whether the target
/// file name exists in the directory when the debounce window closes.
type WatchEvent =
    | SourcePresent
    | WaitingForSource

/// Pure debounce core, unit-testable with an injected clock.
/// - `markDirty` on any filesystem activity touching the watched directory.
/// - Fires after `debounceMs` of quiet, or after `activityCapMs` of continuous
///   activity (spec 7.3: keep retrying even while saves never stop).
type Debounce(debounceMs: int, activityCapMs: int, fire: unit -> unit) =

    let mutable firstDirty = Nullable<DateTime>()
    let mutable lastDirty = Nullable<DateTime>()

    /// Record filesystem activity. Returns true when this call fires immediately.
    member this.MarkDirty(at: DateTime) : bool =
        if not firstDirty.HasValue then firstDirty <- Nullable at
        lastDirty <- Nullable at
        // An activity cap overrun fires even mid-activity.
        (at - firstDirty.Value).TotalMilliseconds >= float activityCapMs
        && this.Pump(DateTime(at.Ticks + int64 activityCapMs * TimeSpan.TicksPerMillisecond))

    /// Advance time to `now`; fire when the window closes.
    member _.Pump(now: DateTime) : bool =
        if not lastDirty.HasValue then
            false
        else
            let quietClosed = (now - lastDirty.Value).TotalMilliseconds >= float debounceMs
            let capClosed = (now - firstDirty.Value).TotalMilliseconds >= float activityCapMs

            if quietClosed || capClosed then
                firstDirty <- Nullable()
                lastDirty <- Nullable()
                fire ()
                true
            else
                false


/// Watches the directory containing `sourcePath` (not the file itself — AT-003
/// rename/replace survives directory-level watches), filters by file name,
/// debounces, and reports SourcePresent / WaitingForSource.
type DirectoryWatcher(sourcePath: string, ?debounceMs: int, ?activityCapMs: int, ?clock: unit -> DateTime) =

    let debounceMs = defaultArg debounceMs 500
    let activityCapMs = defaultArg activityCapMs 2000
    let clock = defaultArg clock (fun () -> DateTime.UtcNow)
    let directory = Path.GetDirectoryName(Path.GetFullPath sourcePath)
    let fileName = Path.GetFileName sourcePath
    let gate = obj ()

    let changed = Event<WatchEvent>()
    let mutable disposed = false

    let fire () =
        lock gate (fun () ->
            // A8: after debounce, only file-name presence in the directory matters.
            let exists =
                try
                    Directory.Exists directory
                    && File.Exists(Path.Combine(directory, fileName))
                with _ ->
                    false

            changed.Trigger(if exists then SourcePresent else WaitingForSource))

    let debounce = Debounce(debounceMs, activityCapMs, fire)

    let mutable fsw: FileSystemWatcher = null

    let notifyFilter =
        NotifyFilters.FileName
        ||| NotifyFilters.LastWrite
        ||| NotifyFilters.Size
        ||| NotifyFilters.CreationTime

    let rec start () =
        let watcher =
            new FileSystemWatcher(
                directory,
                EnableRaisingEvents = true,
                IncludeSubdirectories = false,
                InternalBufferSize = 64 * 1024, // A8: default 4KB overflows under bursts
                NotifyFilter = notifyFilter
            )

        let onFsEvent (_: FileSystemEventArgs) =
            lock gate (fun () ->
                if not disposed then
                    debounce.MarkDirty(clock ()) |> ignore)

        let onError (_: ErrorEventArgs) =
            // Buffer overflow or loss: full rescan (R3). Treat as dirt so the
            // debounce window re-evaluates presence from scratch.
            lock gate (fun () ->
                if not disposed then debounce.MarkDirty(clock ()) |> ignore)

        watcher.Created.Add onFsEvent
        watcher.Changed.Add onFsEvent
        watcher.Renamed.Add onFsEvent
        watcher.Deleted.Add onFsEvent
        watcher.Error.Add onError
        fsw <- watcher

    // Close the debounce window during quiet periods with a polling timer.
    let pumpTimer =
        new Timers.Timer(float debounceMs / 2.0, AutoReset = true, Enabled = true)

    do pumpTimer.Elapsed.Add(fun _ ->
        lock gate (fun () ->
            if not disposed then debounce.Pump(clock ()) |> ignore))

    do start ()

    /// Raised on the thread that closed the debounce window.
    member _.Events: IObservable<WatchEvent> = changed.Publish

    interface IDisposable with
        member _.Dispose() =
            lock gate (fun () ->
                if not disposed then
                    disposed <- true
                    pumpTimer.Dispose()

                    if not (isNull fsw) then
                        fsw.EnableRaisingEvents <- false
                        fsw.Dispose())

/// Watches a directory for a DYNAMIC set of file names (schematic dependency
/// set changes as children are added/removed — SCH-FR-005). The name set is
/// re-queried on every debounce close, so new children are picked up without
/// restart. Presence rule: at least one watched name present.
type DirectoryWatcherSet
    (
        directory: string,
        fileNames: unit -> string seq,
        ?debounceMs: int,
        ?activityCapMs: int,
        ?clock: unit -> DateTime
    ) =

    let debounceMs = defaultArg debounceMs 500
    let activityCapMs = defaultArg activityCapMs 2000
    let clock = defaultArg clock (fun () -> DateTime.UtcNow)
    let gate = obj ()
    let changed = Event<WatchEvent>()
    let mutable disposed = false

    let fire () =
        lock gate (fun () ->
            let names =
                try
                    fileNames () |> Set.ofSeq
                with _ ->
                    Set.empty

            let exists =
                Directory.Exists directory
                && (names |> Seq.exists (fun n -> File.Exists(Path.Combine(directory, n))))

            changed.Trigger(if exists then SourcePresent else WaitingForSource))

    let debounce = Debounce(debounceMs, activityCapMs, fire)
    let mutable fsw: FileSystemWatcher = null
    let pumpTimer = new Timers.Timer(float debounceMs / 2.0, AutoReset = true, Enabled = true)

    do
        let notifyFilter =
            NotifyFilters.FileName
            ||| NotifyFilters.LastWrite
            ||| NotifyFilters.Size
            ||| NotifyFilters.CreationTime

        let watcher =
            new FileSystemWatcher(
                directory,
                EnableRaisingEvents = true,
                IncludeSubdirectories = false,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = notifyFilter
            )

        let onFsEvent (_: FileSystemEventArgs) =
            lock gate (fun () ->
                if not disposed then debounce.MarkDirty(clock ()) |> ignore)

        watcher.Created.Add onFsEvent
        watcher.Changed.Add onFsEvent
        watcher.Renamed.Add onFsEvent
        watcher.Deleted.Add onFsEvent

        watcher.Error.Add(fun _ ->
            lock gate (fun () ->
                if not disposed then debounce.MarkDirty(clock ()) |> ignore))

        fsw <- watcher

        pumpTimer.Elapsed.Add(fun _ ->
            lock gate (fun () ->
                if not disposed then debounce.Pump(clock ()) |> ignore))

    member _.Events: IObservable<WatchEvent> = changed.Publish

    interface IDisposable with
        member _.Dispose() =
            lock gate (fun () ->
                if not disposed then
                    disposed <- true
                    pumpTimer.Dispose()

                    if not (isNull fsw) then
                        fsw.EnableRaisingEvents <- false
                        fsw.Dispose())
