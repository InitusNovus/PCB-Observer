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

/// Run kicad-cli `pcb drc` / `sch erc` with JSON output into `outPath`.
/// Returns the tool's stdout (unused) and never throws for tool-level
/// failures — the summary carries status=failed instead.
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

        use p = Process.Start psi

        if not (p.WaitForExit 180_000) then
            p.Kill(entireProcessTree = true)
            { sequence = sequence; snapshot_sha256 = snapshotHash; kind = subcommand; status = "failed"; errors = 0; warnings = 0; excluded = 0; total = 0; tool_version = toolVersion; note = "timeout after 180s" }
        else
            let _ = p.StandardOutput.ReadToEnd()
            let _ = p.StandardError.ReadToEnd()

            if p.ExitCode <> 0 && not (File.Exists outPath) then
                { sequence = sequence; snapshot_sha256 = snapshotHash; kind = subcommand; status = "failed"; errors = 0; warnings = 0; excluded = 0; total = 0; tool_version = toolVersion; note = $"kicad-cli exited with {p.ExitCode}" }
            else
                // Parse the JSON report for severity counts.
                use doc = JsonDocument.Parse(File.ReadAllText outPath)
                let root = doc.RootElement
                let mutable el = Unchecked.defaultof<JsonElement>
                let countOf (severity: string) =
                    if root.TryGetProperty("violations", &el) then
                        Seq.filter
                            (fun (v: JsonElement) ->
                                let mutable sev = Unchecked.defaultof<JsonElement>
                                v.TryGetProperty("severity", &sev) && sev.GetString() = severity)
                            (el.EnumerateArray())
                        |> Seq.length
                    else
                        0

                let errs = countOf "error"
                let warns = countOf "warning"
                let excl = countOf "excluded"

                { sequence = sequence
                  snapshot_sha256 = snapshotHash
                  kind = subcommand
                  status = (if errs + warns > 0 then "violations" else "ok")
                  errors = errs
                  warnings = warns
                  excluded = excl
                  total = errs + warns
                  tool_version = toolVersion
                  note = $"bound to snapshot #{sequence}" }
    with e ->
        { sequence = sequence
          snapshot_sha256 = snapshotHash
          kind = subcommand
          status = "failed"
          errors = 0
          warnings = 0
          excluded = 0
          total = 0
          tool_version = toolVersion
          note = e.Message }

/// Persist the summary beside its bundle as drc.json / erc.json.
let writeSummary (bundleDir: string) (summary: RuleSummary) : unit =
    let name = if summary.kind = "erc" then "erc.json" else "drc.json"
    File.WriteAllText(Path.Combine(bundleDir, name), JsonSerializer.Serialize(summary, JsonSerializerOptions(WriteIndented = true)))
