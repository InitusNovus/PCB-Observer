module PcbObserver.RuleCheck

open System
open System.Diagnostics
open System.IO
open System.Text.Json

/// §25/addendum §35 sidecar rule checks: DRC (PCB) and ERC (schematic) run
/// against observer-owned snapshots ONLY, every result is bound to the exact
/// snapshot sequence, and a check never blocks publication. Stale results are
/// never displayed as current — consumers key strictly on the bundle sequence
/// carried in this file.

type RuleSummary =
    { sequence: int
      snapshot_sha256: string
      kind: string // "drc" | "erc"
      status: string // "ok" | "violations" | "failed"
      errors: int
      warnings: int
      excluded: int
      total: int
      tool_version: string
      note: string }

/// Run kicad-cli `pcb drc` / `sch erc` with JSON output. `outPath` receives
/// the raw report and is DELETED on every exit path (parse, tool failure,
/// timeout, exception) — staging purge and the byte quota are blind to loose
/// files at the staging root. Tool-level failures never throw: the summary
/// carries status=failed with the reason instead.
let runRuleCheck
    (cli: string)
    (kind: string) // "pcb drc" | "sch erc"
    (snapshotFile: string)
    (outPath: string)
    (sequence: int)
    (snapshotHash: string)
    (toolVersion: string)
    : RuleSummary =
    let kindParts = kind.Split(' ', 2)
    let subcommand = kindParts[1]

    let failed (note: string) : RuleSummary =
        { sequence = sequence
          snapshot_sha256 = snapshotHash
          kind = subcommand
          status = "failed"
          errors = 0
          warnings = 0
          excluded = 0
          total = 0
          tool_version = toolVersion
          note = note }

    let cleanup () =
        try
            if File.Exists outPath then File.Delete outPath
        with _ ->
            ()

    try
        let psi = ProcessStartInfo(cli)
        psi.ArgumentList.Add kindParts[0]
        psi.ArgumentList.Add subcommand
        psi.ArgumentList.Add "--format"
        psi.ArgumentList.Add "json"
        psi.ArgumentList.Add "--output"
        psi.ArgumentList.Add outPath
        psi.ArgumentList.Add snapshotFile
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true

        // Drain both pipes concurrently BEFORE WaitForExit: kicad-cli can
        // emit enough text to fill a pipe buffer, which would deadlock a
        // post-exit ReadToEnd and surface as a bogus 180s timeout.
        use p = Process.Start psi
        let outTask = p.StandardOutput.ReadToEndAsync()
        let errTask = p.StandardError.ReadToEndAsync()

        let result =
            if not (p.WaitForExit 180_000) then
                p.Kill(entireProcessTree = true)
                failed "timeout after 180s"
            else
                let _ = outTask.Result
                let _ = errTask.Result

                if p.ExitCode <> 0 && not (File.Exists outPath) then
                    failed $"kicad-cli exited with {p.ExitCode}"
                else
                    use doc = JsonDocument.Parse(File.ReadAllText outPath)
                    let root = doc.RootElement
                    let mutable el = Unchecked.defaultof<JsonElement>

                    let countBy (arrayName: string) (severity: string) =
                        if root.TryGetProperty(arrayName, &el) then
                            Seq.filter
                                (fun (v: JsonElement) ->
                                    let mutable sev = Unchecked.defaultof<JsonElement>
                                    v.TryGetProperty("severity", &sev) && sev.GetString() = severity)
                                (el.EnumerateArray())
                            |> Seq.length
                        else
                            0

                    let arrayCount (arrayName: string) =
                        if root.TryGetProperty(arrayName, &el) then (el.EnumerateArray() |> Seq.length) else 0

                    // kicad-cli's DRC JSON also carries unconnected_items and
                    // schematic_parity arrays whose entries KiCad itself counts
                    // in its exit verdict (boundary review F2): a board with
                    // only those issues must never report 통과.
                    let errs = countBy "violations" "error"
                    let warns = countBy "violations" "warning"
                    let excl = countBy "violations" "excluded"
                    let unconnected = arrayCount "unconnected_items"
                    let parity = arrayCount "schematic_parity"
                    let totalErrs = errs + unconnected + parity

                    { sequence = sequence
                      snapshot_sha256 = snapshotHash
                      kind = subcommand
                      status = (if totalErrs + warns > 0 then "violations" else "ok")
                      errors = totalErrs
                      warnings = warns
                      excluded = excl
                      total = totalErrs + warns
                      tool_version = toolVersion
                      note =
                        (if unconnected + parity > 0 then
                             $"bound to snapshot #{sequence} · includes {unconnected} unconnected / {parity} parity"
                         else
                             $"bound to snapshot #{sequence}") }

        cleanup ()
        result
    with e ->
        cleanup ()
        failed e.Message

/// Persist the summary beside its bundle as drc.json / erc.json — atomically
/// (temp + move) so a concurrent reader never sees a torn file.
let writeSummary (bundleDir: string) (summary: RuleSummary) : unit =
    let name = if summary.kind = "erc" then "erc.json" else "drc.json"
    let final = Path.Combine(bundleDir, name)
    let tmp = final + ".tmp"

    File.WriteAllText(tmp, JsonSerializer.Serialize(summary, JsonSerializerOptions(WriteIndented = true)))
    File.Move(tmp, final, true)
