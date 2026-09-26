module PcbObserver.Capture

open System
open System.IO
open System.Security.Cryptography

/// Copy a saved PCB into observer-owned storage; never render the source directly.
/// The source is read once and verified unchanged (size + mtime) across the read.
let captureBoard (source: string) (cacheDir: string) : string =
    Directory.CreateDirectory(cacheDir) |> ignore

    let stamp path =
        let info = FileInfo path
        struct (info.Length, info.LastWriteTimeUtc)

    let before = stamp source
    let payload = File.ReadAllBytes source
    let after = stamp source

    if before <> after then
        invalidOp "PCB changed during capture; try again"

    let digest =
        SHA256.HashData payload |> Convert.ToHexString |> _.ToLowerInvariant()

    let target = Path.Combine(cacheDir, $"{digest}.kicad_pcb")

    if not (File.Exists target) then File.WriteAllBytes(target, payload)

    target
