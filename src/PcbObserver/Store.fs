module PcbObserver.Store

open System
open System.IO
open System.Text.Json
open PcbObserver.Capture

open PcbObserver.Render

let private jsonOptions = JsonSerializerOptions(WriteIndented = true)

/// §39 Snapshot metadata row (pinned field names, C7).
type SnapshotRow =
    { sequence: int
      captured_at: DateTime
      source_path: string
      content_hash: string
      files: string[]
      capture_status: string }

let private rowToJson (row: SnapshotRow) : obj =
    {| sequence = row.sequence
       captured_at = row.captured_at.ToString("o")
       source_path = row.source_path
       content_hash = row.content_hash
       files = row.files
       capture_status = row.capture_status |}

/// Observer-owned storage, outside the source project (§8.2).
/// Layout: <root>/{snapshots,renders,staging,metadata,logs}
type Store(projectRoot: string) =

    let snapshotsDir = Path.Combine(projectRoot, "snapshots")
    let rendersDir = Path.Combine(projectRoot, "renders")
    let stagingDir = Path.Combine(projectRoot, "staging") // sibling of renders/ (A7: same volume)
    let metadataDir = Path.Combine(projectRoot, "metadata")
    let logsDir = Path.Combine(projectRoot, "logs")
    let metadataPath = Path.Combine(metadataDir, "snapshots.json")

    do
        for dir in [ snapshotsDir; rendersDir; stagingDir; metadataDir; logsDir ] do
            Directory.CreateDirectory dir |> ignore

    // A7: stale staging from a crashed run must never survive startup —
    // Program.fs watch startup calls PurgeStaleStaging() explicitly.

    member this.SnapshotsDir = snapshotsDir
    member this.RendersDir = rendersDir
    member this.StagingDir = stagingDir
    member this.MetadataPath = metadataPath

    /// A7: delete everything under staging/.
    member this.PurgeStaleStaging() : unit =
        for dir in Directory.EnumerateDirectories stagingDir do
            try
                Directory.Delete(dir, true)
            with _ ->
                ()

    member this.LoadSnapshots() : SnapshotRow list =
        if File.Exists metadataPath then
            try
                use doc = JsonDocument.Parse(File.ReadAllText metadataPath)

                [ for el in doc.RootElement.EnumerateArray() ->
                      let str (name: string) = el.GetProperty(name).GetString()

                      { sequence = el.GetProperty("sequence").GetInt32()
                        captured_at = DateTime.Parse(str "captured_at", Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.RoundtripKind)
                        source_path = str "source_path"
                        content_hash = str "content_hash"
                        files = [| for f in el.GetProperty("files").EnumerateArray() -> f.GetString() |]
                        capture_status = str "capture_status" } ]
            with e ->
                // Preserve failure evidence instead of silently masking a
                // corrupt metadata file (seq safety still holds via renders scan).
                File.AppendAllLines(
                    Path.Combine(logsDir, "metadata-errors.log"),
                    [ $"{DateTime.UtcNow:o}\t{e.GetType().Name}: {e.Message}" ]
                )

                []
        else
            []

    /// A1: monotonic sequence across restarts. D1 (pass-2): only metadata and
    /// renders/ decide — snapshot files are sha256 digests and carry no seq.
    member this.NextSequence() : int =
        let metadataMax =
            this.LoadSnapshots() |> List.map (fun r -> r.sequence) |> List.fold max 0

        let rendersMax =
            [ for dir in Directory.EnumerateDirectories rendersDir do
                  match Int32.TryParse(Path.GetFileName dir) with
                  | true, seq -> seq
                  | _ -> () ]
            |> List.fold max 0

        1 + max metadataMax rendersMax

    member this.StagingFor(sequence: int) : string =
        let dir = Path.Combine(stagingDir, string sequence)
        Directory.CreateDirectory dir |> ignore
        dir

    /// §39 Render Bundle manifest with pinned field names (C7). The manifest
    /// is written AFTER rendering and self-describes the layer set actually
    /// rendered, so completeness never depends on a global constant.
    member this.WriteManifest(stagingDir: string, snap: Snapshot, sourcePath: string, rendererKind: string, rendererVersion: string, renderedLayers: string[]) : unit =
        let manifest =
            {| snapshot_sequence = snap.sequence
               content_hash = snap.sha256
               source_path = sourcePath
               renderer = {| kind = rendererKind; version = rendererVersion |}
               created_at = snap.capturedAt.ToString("o")
               layers = renderedLayers
               status = "complete" |}

        File.WriteAllText(Path.Combine(stagingDir, "manifest.json"), JsonSerializer.Serialize(manifest, jsonOptions))

    /// §11.2 layer-composition proof: same manifest shape, plus each layer's
    /// recorded viewBox and a consistency flag. viewBoxes are optional so
    /// legacy/unknown bundles stay readable; absent map => no extra fields.
    member this.WriteManifestWithViewBoxes
        (
            stagingDir: string,
            snap: Snapshot,
            sourcePath: string,
            rendererKind: string,
            rendererVersion: string,
            renderedLayers: string[],
            layerViewBoxes: (string * string) list
        ) : unit =
        let viewBoxes = Map.ofList layerViewBoxes

        let manifestJson =
            if Map.isEmpty viewBoxes then
                JsonSerializer.Serialize(
                    {| snapshot_sequence = snap.sequence
                       content_hash = snap.sha256
                       source_path = sourcePath
                       renderer = {| kind = rendererKind; version = rendererVersion |}
                       created_at = snap.capturedAt.ToString("o")
                       layers = renderedLayers
                       status = "complete" |},
                    jsonOptions
                )
            else
                let distinct = layerViewBoxes |> List.map snd |> List.distinct
                let consistent = distinct.Length = 1

                JsonSerializer.Serialize(
                    {| snapshot_sequence = snap.sequence
                       content_hash = snap.sha256
                       source_path = sourcePath
                       renderer = {| kind = rendererKind; version = rendererVersion |}
                       created_at = snap.capturedAt.ToString("o")
                       layers = renderedLayers
                       layer_view_boxes = [ for l in renderedLayers -> {| layer = l; view_box = Map.tryFind l viewBoxes |} ]
                       view_box_consistent = consistent
                       status = "complete" |},
                    jsonOptions
                )

        File.WriteAllText(Path.Combine(stagingDir, "manifest.json"), manifestJson)

    /// §39 Snapshot metadata row, appended atomically (A1: temp + rename).
    member this.AppendSnapshot(snap: Snapshot, sourcePath: string) : unit =
        let rows = this.LoadSnapshots() |> List.map rowToJson
        let all = rows @ [ rowToJson { sequence = snap.sequence
                                       captured_at = snap.capturedAt
                                       source_path = sourcePath
                                       content_hash = snap.sha256
                                       files = [| snap.path |]
                                       capture_status = snap.captureStatus } ]

        let json = JsonSerializer.Serialize(all, jsonOptions)
        let tmp = metadataPath + ".tmp"
        File.WriteAllText(tmp, json)
        File.Move(tmp, metadataPath, true)

    /// Completeness gate before publication (AT-005). Manifest-driven and
    /// self-describing: a PCB bundle lists `layers`, a schematic bundle lists
    /// `pages[].file` — complete iff its manifest exists and every artifact
    /// it lists is present. Legacy bundles with smaller sets stay complete.
    member this.IsBundleComplete(dir: string) : bool =
        let manifestPath = Path.Combine(dir, "manifest.json")

        if not (File.Exists manifestPath) then
            false
        else
            try
                use doc = JsonDocument.Parse(File.ReadAllText manifestPath)
                let root = doc.RootElement
                let mutable el = Unchecked.defaultof<JsonElement>

                let layerFiles =
                    if root.TryGetProperty("layers", &el) then
                        [ for l in el.EnumerateArray() do
                              let name = l.GetString()

                              if not (isNull name) then
                                  $"{name}.svg" ]
                    else
                        []

                let pageFiles =
                    if root.TryGetProperty("pages", &el) then
                        [ for p in el.EnumerateArray() do
                              if p.TryGetProperty("file", &el) then
                                  el.GetString() ]
                    else
                        []

                let artifacts =
                    layerFiles @ pageFiles
                    |> List.filter (fun f -> not (isNull f) && f <> "")

                artifacts.Length > 0
                && artifacts |> List.forall (fun f -> File.Exists(Path.Combine(dir, f)))
            with _ ->
                false

    /// Atomic publish: directory rename staging/<seq> → renders/<seq>.
    /// Monotonic seq makes the target unique (R5).
    member this.PublishBundle(sequence: int) : bool =
        let source = Path.Combine(stagingDir, string sequence)
        let target = Path.Combine(rendersDir, string sequence)

        if not (this.IsBundleComplete source) then
            try
                Directory.Delete(source, true)
            with _ ->
                ()
            false
        else
            try
                Directory.Move(source, target)
                true
            with _ ->
                false

    member this.BundlePath(sequence: int) : string = Path.Combine(rendersDir, string sequence)

    member this.CompleteBundles() : int list =
        [ for dir in Directory.EnumerateDirectories rendersDir do
              match Int32.TryParse(Path.GetFileName dir) with
              | true, seq when this.IsBundleComplete dir -> seq
              | _ -> () ]
        |> List.sortDescending

    /// A5: cap complete bundles at `cap` while protecting the last published
    /// bundle and the `protectRecent` most recent complete ones.
    member this.PruneHistory(lastPublished: int, ?cap: int, ?protectRecent: int) : unit =
        let cap = defaultArg cap 50
        let protectRecent = defaultArg protectRecent 10
        let complete = this.CompleteBundles()
        let protectedSet = set (lastPublished :: List.truncate protectRecent complete)

        if complete.Length > cap then
            for seq in complete |> List.skip cap do
                if not (protectedSet.Contains seq) then
                    try
                        Directory.Delete(this.BundlePath seq, true)
                    with _ ->
                        ()

    /// C6: one-line store size log per publish — feeds §40 disk-cost data.
    member this.LogStoreSize(sequence: int) : unit =
        let size dir =
            try
                Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                |> Seq.sumBy (fun f -> FileInfo(f).Length)
            with _ ->
                0L

        let bytes = size snapshotsDir + size rendersDir
        let line = $"{DateTime.UtcNow:o}\tseq={sequence}\tbytes={bytes}"
        File.AppendAllLines(Path.Combine(logsDir, "size.log"), [ line ])
