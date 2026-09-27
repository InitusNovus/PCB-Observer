module PcbObserver.Watch

open System
open System.Collections.Generic
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

/// Watches one schematic root and its current physical dependency set.
///
/// Each dependency is represented by its full path.  A FileSystemWatcher is
/// attached to the dependency's parent directory when that directory exists;
/// when it does not, the nearest existing ancestor is watched recursively so a
/// later directory/file creation can recover the dependency without restart.
/// All directory watchers share one project-level debounce and activity cap.
type DependencyWatcher
    (
        rootPath: string,
        files: seq<string>,
        missing: seq<string>,
        ?debounceMs: int,
        ?activityCapMs: int,
        ?clock: unit -> DateTime
    ) =

    let debounceMs = defaultArg debounceMs 500
    let activityCapMs = defaultArg activityCapMs 2000
    let clock = defaultArg clock (fun () -> DateTime.UtcNow)
    let gate = obj ()
    let changed = Event<WatchEvent>()
    let root = Path.GetFullPath rootPath
    let mutable disposed = false

    let pathComparer = StringComparer.OrdinalIgnoreCase
    let directorySeparator = string Path.DirectorySeparatorChar

    let canonicalPath (path: string) =
        let full = Path.GetFullPath path
        let root = Path.GetPathRoot full

        if String.IsNullOrEmpty root then
            full
        elif String.Equals(full, root, StringComparison.OrdinalIgnoreCase) then
            root
        else
            full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

    let isSameOrDescendant (ancestor: string) (path: string) =
        let ancestor = canonicalPath ancestor
        let path = canonicalPath path

        String.Equals(ancestor, path, StringComparison.OrdinalIgnoreCase)
        || (if String.Equals(ancestor, Path.GetPathRoot ancestor, StringComparison.OrdinalIgnoreCase) then
                path.StartsWith(ancestor, StringComparison.OrdinalIgnoreCase)
            else
                path.StartsWith(ancestor + directorySeparator, StringComparison.OrdinalIgnoreCase))

    let mutable dependencyPaths = HashSet<string>(pathComparer)
    let mutable subscriptions = Dictionary<string, FileSystemWatcher>(pathComparer)
    // Assigned before subscriptions/timer are enabled in the constructor body.
    let mutable debounce: Debounce = Unchecked.defaultof<Debounce>

    let notifyFilter =
        NotifyFilters.FileName
        ||| NotifyFilters.DirectoryName
        ||| NotifyFilters.LastWrite
        ||| NotifyFilters.Size
        ||| NotifyFilters.CreationTime

    let rootPresent () =
        try
            File.Exists root
        with _ ->
            false

    let relevantPath (path: string) =
        if String.IsNullOrWhiteSpace path then
            false
        else
            let path = canonicalPath path

            dependencyPaths.Contains path
            || (dependencyPaths |> Seq.exists (fun dependency -> isSameOrDescendant path dependency))

    let markDirtyIfRelevant (paths: string seq) =
        lock gate (fun () ->
            if not disposed
               && (paths |> Seq.exists relevantPath) then
                debounce.MarkDirty(clock ()) |> ignore)

    let markDirtyOnError () =
        // An overflow loses the event path.  The affected watcher is already
        // restricted to dependency ancestors, so a full dependency re-check is
        // safer than silently missing a save.
        lock gate (fun () ->
            if not disposed then
                debounce.MarkDirty(clock ()) |> ignore)

    let disposeWatcher (watcher: FileSystemWatcher) =
        try
            watcher.EnableRaisingEvents <- false
        with _ ->
            ()

        try
            watcher.Dispose()
        with _ ->
            ()

    let disposeSubscriptions () =
        for watcher in subscriptions.Values do
            disposeWatcher watcher

        subscriptions.Clear()

    let nearestExistingAncestor (directory: string) =
        let mutable candidate = canonicalPath directory
        let mutable found = None
        let mutable doneSearching = false

        while not doneSearching do
            if Directory.Exists candidate then
                found <- Some(candidate)
                doneSearching <- true
            else
                let parent = Path.GetDirectoryName candidate

                if String.IsNullOrEmpty parent
                   || String.Equals(parent, candidate, StringComparison.OrdinalIgnoreCase) then
                    doneSearching <- true
                else
                    candidate <- canonicalPath parent

        found

    let buildPlan (paths: HashSet<string>) =
        let plan = Dictionary<string, bool>(pathComparer)

        for path in paths do
            let directory = Path.GetDirectoryName path

            if not (String.IsNullOrEmpty directory) then
                let parentExists = Directory.Exists directory

                match nearestExistingAncestor directory with
                | Some ancestor ->
                    let recursive = not parentExists

                    match plan.TryGetValue ancestor with
                    | true, previous -> plan[ancestor] <- previous || recursive
                    | _ -> plan[ancestor] <- recursive
                | None ->
                    ()

        plan

    let createWatcher (directory: string) (includeSubdirectories: bool) =
        try
            let watcher =
                new FileSystemWatcher(
                    directory,
                    EnableRaisingEvents = false,
                    IncludeSubdirectories = includeSubdirectories,
                    InternalBufferSize = 64 * 1024,
                    NotifyFilter = notifyFilter
                )

            try
                let onFsEvent (args: FileSystemEventArgs) =
                    markDirtyIfRelevant [ args.FullPath ]

                let onRename (args: RenamedEventArgs) =
                    markDirtyIfRelevant [ args.OldFullPath; args.FullPath ]

                watcher.Created.Add onFsEvent
                watcher.Changed.Add onFsEvent
                watcher.Deleted.Add onFsEvent
                watcher.Renamed.Add onRename
                watcher.Error.Add(fun _ -> markDirtyOnError ())
                watcher.EnableRaisingEvents <- true
                Some watcher
            with _ ->
                // EnableRaisingEvents and event-handler setup can fail after
                // the watcher has been allocated (for example while a
                // directory is being deleted). Never leak that partial
                // watcher.
                disposeWatcher watcher
                None
        with _ ->
            None

    /// Reconcile without taking down a working subscription first. New
    /// subscriptions are allocated before old/replaced ones are disposed, so
    /// a save cannot fall through a replacement window. Returns true only
    /// when the actual subscription set changed.
    let reconcilePlan (nextPlan: Dictionary<string, bool>) =
        let additions = ResizeArray<string * FileSystemWatcher>()

        // Allocate every new or mode-changed watcher while the old set remains
        // active. Failed allocations leave an existing watcher in place.
        for pair in nextPlan do
            match subscriptions.TryGetValue pair.Key with
            | true, current when current.IncludeSubdirectories = pair.Value ->
                ()
            | _ ->
                match createWatcher pair.Key pair.Value with
                | Some watcher -> additions.Add((pair.Key, watcher))
                | None -> ()

        let mutable changed = false

        // Install additions/replacements before removing anything stale.
        for (path, fresh) in additions do
            match subscriptions.TryGetValue path with
            | true, previous ->
                subscriptions[path] <- fresh
                disposeWatcher previous
                changed <- true
            | _ ->
                subscriptions.Add(path, fresh)
                changed <- true

        // Remove stale subscriptions only after all additions succeeded.
        let stale =
            subscriptions.Keys
            |> Seq.filter (fun path -> not (nextPlan.ContainsKey path))
            |> Seq.toArray

        for path in stale do
            match subscriptions.TryGetValue path with
            | true, previous ->
                subscriptions.Remove path |> ignore
                disposeWatcher previous
                changed <- true
            | _ ->
                ()

        changed

    let refresh (nextFiles: seq<string>) (nextMissing: seq<string>) =
        lock gate (fun () ->
            if not disposed then
                let nextPaths = HashSet<string>(pathComparer)
                nextPaths.Add(root) |> ignore

                for path in nextFiles do
                    if not (String.IsNullOrWhiteSpace path) then
                        nextPaths.Add(canonicalPath path) |> ignore

                for path in nextMissing do
                    if not (String.IsNullOrWhiteSpace path) then
                        nextPaths.Add(canonicalPath path) |> ignore

                dependencyPaths <- nextPaths
                reconcilePlan (buildPlan nextPaths) |> ignore)

    let fire () =
        let event =
            lock gate (fun () ->
                if disposed then
                    None
                elif rootPresent () then
                    Some SourcePresent
                else
                    Some WaitingForSource)

        match event with
        | Some value -> changed.Trigger value
        | None -> ()

    let pumpTimer =
        new Timers.Timer(float debounceMs / 2.0, AutoReset = true, Enabled = false)

    /// Directory watchers can remain alive while their directory is deleted,
    /// and a newly-created directory has no watcher until it is reconciled.
    /// Recompute the plan periodically; only an actual subscription transition
    /// opens the debounce window, avoiding a timer-driven dirty loop.
    let reconcileCurrent () =
        lock gate (fun () ->
            if not disposed
               && reconcilePlan (buildPlan dependencyPaths) then
                debounce.MarkDirty(clock ()) |> ignore)

    do
        debounce <- Debounce(debounceMs, activityCapMs, fire)

        pumpTimer.Elapsed.Add(fun _ ->
            reconcileCurrent ()

            lock gate (fun () ->
                if not disposed then
                    debounce.Pump(clock ()) |> ignore))

        refresh files missing
        pumpTimer.Enabled <- true

    member _.Events: IObservable<WatchEvent> = changed.Publish

    /// Replace the dependency paths after a successful hierarchy discovery.
    /// The root remains watched even if callers omit it from `nextFiles`.
    member _.Refresh(nextFiles: seq<string>, nextMissing: seq<string>) =
        refresh nextFiles nextMissing

    interface IDisposable with
        member _.Dispose() =
            lock gate (fun () ->
                if not disposed then
                    disposed <- true
                    pumpTimer.Dispose()
                    disposeSubscriptions())
