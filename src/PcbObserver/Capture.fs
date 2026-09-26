module PcbObserver.Capture

open System
open System.IO
open System.Security.Cryptography

/// §39 Snapshot record (serialization field names are pinned by Store.fs).
type Snapshot =
    { sequence: int
      sha256: string
      path: string
      capturedAt: DateTime
      captureStatus: string }

/// Minimal s-expression structural check (§7.4): a file is plausible when the
/// first token is the symbol `kicad_pcb`, parentheses balance outside string
/// literals (strings honor \" escapes), and nothing but whitespace follows the
/// final closing parenthesis. Parenthesis counting alone is not enough.
let looksLikeKicadPcb (text: string) : bool =
    let n = text.Length
    let mutable i = 0

    let skipWs () =
        while i < n && Char.IsWhiteSpace text[i] do
            i <- i + 1

    let readToken () =
        let start = i

        while i < n && not (Char.IsWhiteSpace text[i]) && text[i] <> '(' && text[i] <> ')' do
            i <- i + 1

        text.Substring(start, i - start)

    skipWs ()

    if i >= n || text[i] <> '(' then false
    else
        i <- i + 1
        skipWs ()

        if readToken () <> "kicad_pcb" then false
        else
            let mutable depth = 1
            let mutable inString = false
            let mutable ok = true

            while i < n && ok do
                let c = text[i]

                if inString then
                    if c = '\\' && i + 1 < n then
                        i <- i + 2 // escaped char (covers \")
                    elif c = '"' then
                        inString <- false
                        i <- i + 1
                    else
                        i <- i + 1
                else
                    match c with
                    | '"' -> inString <- true; i <- i + 1
                    | '(' -> depth <- depth + 1; i <- i + 1
                    | ')' ->
                        depth <- depth - 1
                        i <- i + 1
                        if depth = 0 then
                            skipWs ()
                            // Trailing garbage after the root form closes = invalid.
                            ok <- i >= n
                    | _ -> i <- i + 1

            ok && depth = 0 && not inString

/// One stable-read attempt: stat → read → stat; None when the file changed
/// underneath us (§7.4) or failed the structural check.
let private tryReadStable (source: string) : byte[] option =
    try
        let info = FileInfo source
        let before = (info.Length, info.LastWriteTimeUtc)
        let payload = File.ReadAllBytes source
        let info2 = FileInfo source
        let after = (info2.Length, info2.LastWriteTimeUtc)

        if before <> after then
            None
        else
            let text = System.Text.Encoding.UTF8.GetString payload

            if not (looksLikeKicadPcb text) then None else Some payload
    with _ ->
        None

/// Retry loop with a quiet interval (§7.4). Returns None when the source
/// keeps changing or stays structurally incomplete for `maxAttempts`.
let readStable (source: string) (maxAttempts: int) (delay: unit -> unit) : byte[] option =
    let rec loop attempt =
        if attempt >= maxAttempts then
            None
        else
            match tryReadStable source with
            | Some payload -> Some payload
            | None ->
                delay ()
                loop (attempt + 1)

    loop 0

let sha256Hex (payload: byte[]) : string =
    SHA256.HashData payload |> Convert.ToHexString |> _.ToLowerInvariant()

/// Copy a saved PCB into observer-owned storage (content-addressed, deduped).
/// `sequence` is assigned by the single-writer capture path (A1).
let captureSnapshot (source: string) (cacheDir: string) (sequence: int) : Snapshot =
    Directory.CreateDirectory(cacheDir) |> ignore

    let payload =
        match readStable source 10 (fun () -> Threading.Thread.Sleep 500) with
        | Some bytes -> bytes
        | None -> invalidOp "PCB changed during capture; try again"

    let digest = sha256Hex payload
    let target = Path.Combine(cacheDir, $"{digest}.kicad_pcb")

    if not (File.Exists target) then File.WriteAllBytes(target, payload)

    { sequence = sequence
      sha256 = digest
      path = target
      capturedAt = DateTime.UtcNow
      captureStatus = "stable" }

/// Phase 0 one-shot entry (kept for the legacy CLI): content-addressed copy.
let captureBoard (source: string) (cacheDir: string) : string =
    Directory.CreateDirectory(cacheDir) |> ignore

    let stamp path =
        let info = FileInfo path
        struct (info.Length, info.LastWriteTimeUtc)

    let before = stamp source
    let payload = File.ReadAllBytes source
    let after = stamp source

    if before <> after then invalidOp "PCB changed during capture; try again"

    let digest = sha256Hex payload
    let target = Path.Combine(cacheDir, $"{digest}.kicad_pcb")

    if not (File.Exists target) then File.WriteAllBytes(target, payload)

    target
