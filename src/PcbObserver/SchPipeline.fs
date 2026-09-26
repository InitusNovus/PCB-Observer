module PcbObserver.SchPipeline

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open PcbObserver.Capture
open PcbObserver.Sch

let private jsonOptions = JsonSerializerOptions(WriteIndented = true)

/// Project-level snapshot: root + every reachable child copied preserving the
/// relative layout (kicad-cli resolves children relative to the root file).
/// Content-addressed by (relative path, per-file hash) pairs.
type SchSnapshot =
    { sequence: int
      sha256: string
      dir: string
      rootFile: string
      capturedAt: DateTime
      fileHashes: (string * string) list
      captureStatus: string }

let private fileSha (path: string) : string =
    Capture.sha256Hex(File.ReadAllBytes path)

let captureSch (disc: Discovery) (cacheDir: string) (sequence: int) : SchSnapshot =
    let rootDir = Path.GetDirectoryName disc.root
    let rootFile = Path.GetFileName disc.root

    let rel (p: string) = Path.GetRelativePath(rootDir, p)

    let fileHashes =
        [ for p in disc.files do
              (rel p, fileSha p) ]

    let idSource =
        fileHashes |> List.map (fun (r, h) -> $"{r}:{h}") |> String.concat "|"

    let id = Capture.sha256Hex(System.Text.Encoding.UTF8.GetBytes idSource)
    let target = Path.Combine(cacheDir, id)

    if not (Directory.Exists target) then
        Directory.CreateDirectory target |> ignore

        for p in disc.files do
            let dest = Path.Combine(target, rel p)
            Directory.CreateDirectory(Path.GetDirectoryName dest) |> ignore
            File.Copy(p, dest, true)

    let status = if List.isEmpty disc.missing then "stable" else "missing-children"

    { sequence = sequence
      sha256 = id
      dir = target
      rootFile = rootFile
      capturedAt = DateTime.UtcNow
      fileHashes = fileHashes
      captureStatus = status }

/// One `sch export svg` call from the snapshot root (whole-design baseline,
/// Stage 0 findings: one call renders every logical page).
let renderSch (run: string -> string[] -> unit) (cli: string) (stagingDir: string) (snap: SchSnapshot) : unit =
    Directory.CreateDirectory(stagingDir) |> ignore

    run cli [| "sch"; "export"; "svg"; "--output"; stagingDir; Path.Combine(snap.dir, snap.rootFile) |]

/// Page manifest row.
type PageStatus = { chain: string[]; file: string; sheet_file: string; status: string }

/// Post-render analysis: which logical pages were emitted, which are missing
/// children (explicit state, SCH-FR-016), which failed to appear (anomaly).
let analyzePages (disc: Discovery) (pages: Page list) (stagingDir: string) : PageStatus array * string list =
    let emitted =
        [ for f in Directory.GetFiles(stagingDir, "*.svg") do
              Path.GetFileName f ]
        |> Set.ofList

    let missingSet = Set.ofList disc.missing

    let rows =
        [| for p in pages do
               // Missing children are detected BEFORE emission: kicad-cli
               // silently emits an empty-frame SVG for them (findings SS49-7),
               // so file presence alone must not report "rendered".
               let status =
                   if missingSet.Contains p.sheetFile then "missing"
                   elif emitted.Contains p.file then "rendered"
                   else "not-emitted"

               { chain = Array.ofList p.chain
                 file = p.file
                 sheet_file = p.sheetFile
                 status = status } |]

    let anomalies =
        [ for r in rows do
              if r.status = "not-emitted" then $"page not emitted: {r.file}" ]

    (rows, anomalies)

/// Bundle manifest with pinned field names (sch shape: pages, not layers).
let writeSchManifest
    (stagingDir: string)
    (snap: SchSnapshot)
    (disc: Discovery)
    (pages: PageStatus array)
    (anomalies: string list)
    (rendererVersion: string)
    : unit =
    let manifest =
        {| snapshot_sequence = snap.sequence
           content_hash = snap.sha256
           source_path = disc.root
           renderer = {| kind = "kicad-cli"; version = rendererVersion |}
           created_at = snap.capturedAt.ToString("o")
           root_file = snap.rootFile
           pages = pages
           anomalies = Array.ofList anomalies
           status = "complete" |}

    File.WriteAllText(Path.Combine(stagingDir, "manifest.json"), JsonSerializer.Serialize(manifest, jsonOptions))

/// A sch bundle is publishable when every page is rendered or an explicitly
/// missing child (never silently incomplete).
let bundleIsPublishable (pages: PageStatus array) : bool =
    pages |> Array.forall (fun p -> p.status = "rendered" || p.status = "missing")

/// Adapter onto the PCB snapshot record so the shared queue carries it.
let toSnapshot (snap: SchSnapshot) : Capture.Snapshot =
    { sequence = snap.sequence
      sha256 = snap.sha256
      path = snap.dir
      capturedAt = snap.capturedAt
      captureStatus = snap.captureStatus }

/// Rebuild a SchSnapshot from the queue-carried adapter record.
let toSnapshotReconstruct (snap: Capture.Snapshot) (rootFile: string) : SchSnapshot =
    { sequence = snap.sequence
      sha256 = snap.sha256
      dir = snap.path
      rootFile = rootFile
      capturedAt = snap.capturedAt
      fileHashes = []
      captureStatus = snap.captureStatus }
