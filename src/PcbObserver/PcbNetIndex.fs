module PcbObserver.PcbNetIndex

open System
open System.IO
open System.Text.Json

/// §24 narrow scope: an indexing/display function over net-ASSIGNED objects.
/// This is explicitly NOT connectivity analysis — no electrical continuity
/// is claimed or verified (spec §24 terminology rule).

/// A display primitive in board millimetre coordinates.
type Primitive =
    | Segment of x1: float * y1: float * x2: float * y2: float * width: float
    | Via of x: float * y: float * dia: float
    | Pad of x: float * y: float * dia: float
    | ZonePoly of pts: (float * float) list

/// One net's index: display primitives as [kind; x1; y1; x2; y2] fraction
/// rows (vias/pads repeat the centre in all four) plus an explicit
/// truncation flag when the per-net cap was hit.
type NetEntry = { name: string; rows: float list list; truncated: bool }

type NetIndex =
    { viewBox: string
      nets: NetEntry list
      totalPrimitives: int
      truncatedNets: int
      extractedPrimitives: int
      totalTruncated: bool }

/// Cap defaults: keep the browser payload bounded on heavily-routed boards.
let DefaultPerNetCap = 400
let DefaultTotalCap = 20000

// ---------------------------------------------------------------------------
// Escape-aware s-expression tree (same string contract as Sch.fs: quotes may
// contain escapes and unbalanced parentheses).
// ---------------------------------------------------------------------------

type Sx =
    | Atom of string
    | Str of string
    | List of Sx list

let private parseSx (s: string) : Sx list =
    let n = s.Length
    let mutable i = 0

    let rec parseOne () : Sx option =
        while i < n && Char.IsWhiteSpace s[i] do
            i <- i + 1

        if i >= n then
            None
        else
            match s[i] with
            | '(' ->
                i <- i + 1
                let items = ResizeArray<Sx>()

                let rec loop () =
                    while i < n && Char.IsWhiteSpace s[i] do
                        i <- i + 1

                    if i < n && s[i] = ')' then
                        i <- i + 1
                    else
                        match parseOne () with
                        | Some x -> items.Add x; loop ()
                        | None -> ()

                loop ()
                Some(List(List.ofSeq items))
            | '"' ->
                i <- i + 1
                let buf = Text.StringBuilder()

                let rec str () =
                    if i >= n then
                        ()
                    elif s[i] = '\\' && i + 1 < n then
                        buf.Append s[i + 1] |> ignore
                        i <- i + 2
                        str ()
                    elif s[i] = '"' then
                        i <- i + 1
                    else
                        buf.Append s[i] |> ignore
                        i <- i + 1
                        str ()

                str ()
                Some(Str(buf.ToString()))
            | c when c = ')' ->
                i <- i + 1
                None
            | _ ->
                let start = i

                while i < n && not (Char.IsWhiteSpace s[i]) && s[i] <> '(' && s[i] <> ')' && s[i] <> '"' do
                    i <- i + 1

                Some(Atom(s.Substring(start, i - start)))

    let roots = ResizeArray<Sx>()

    // Resilient top scan: a stray ')' yields None but is already consumed,
    // so keep scanning instead of silently dropping the file's tail.
    let rec top () =
        if i < n then
            match parseOne () with
            | Some x -> roots.Add x
            | None -> ()

            top ()

    top ()
    List.ofSeq roots

// -- tree helpers ------------------------------------------------------------

let private headOf = function
    | List (Atom h :: _) -> Some h
    | List (Str h :: _) -> Some h
    | _ -> None

let private children form = match form with List xs -> xs | _ -> []

let private named (name: string) (form: Sx) : Sx list =
    children form
    |> List.filter (fun c -> headOf c = Some name)

let private tryNamedOne (name: string) (form: Sx) : Sx option =
    named name form |> List.tryHead

/// Atoms/strings of a form in order: (at 1 2 90) -> ["1";"2";"90"].
let private values = function
    | List (_ :: xs) -> xs |> List.choose (function Atom a -> Some a | Str s -> Some s | List _ -> None)
    | _ -> []

let private tryFloat (xs: string list) (idx: int) : float option =
    if idx < xs.Length then
        match Double.TryParse(xs[idx]) with
        | true, v -> Some v
        | _ -> None
    else
        None

let private floatOf (xs: string list) (idx: int) (fallback: float) = tryFloat xs idx |> Option.defaultValue fallback

/// Net reference resolution: `(net "NAME")`, `(net N "NAME")`, or `(net N)`
/// resolved through top-level declarations.
let private tryNetName (declarations: Map<int, string>) (form: Sx) : string option =
    match tryNamedOne "net" form |> Option.map values with
    | Some (n :: name :: _) when name <> "" ->
        // (net N "NAME") — prefer the declared name, fall back to inline.
        match Int32.TryParse n with
        | true, num -> declarations |> Map.tryFind num |> Option.orElse (Some name)
        | _ -> Some name
    | Some [ n ] when n <> "" ->
        // Ambiguous single token: numeric (KiCad 9) resolves through
        // declarations; otherwise it is a KiCad 10 name reference.
        match Int32.TryParse n with
        | true, num -> declarations |> Map.tryFind num |> Option.orElse (Some n)
        | _ -> Some n
    | _ -> None

// -- extraction ----------------------------------------------------------------

/// Diagnostics: head atoms of the parsed top-level forms.
let topHeads (pcbPath: string) : string list =
    parseSx (File.ReadAllText pcbPath) |> List.choose headOf

let private extractSegment (net: string) (form: Sx) : (string * Primitive) option =
    match tryNamedOne "start" form, tryNamedOne "end" form with
    | Some st, Some en ->
        let sv, ev = values st, values en

        Some(net, Segment(floatOf sv 0 0.0, floatOf sv 1 0.0, floatOf ev 0 0.0, floatOf ev 1 0.0, tryFloat (form |> tryNamedOne "width" |> Option.map values |> Option.defaultValue []) 0 |> Option.defaultValue 0.2))
    | _ -> None

let private extractVia (net: string) (form: Sx) : (string * Primitive) option =
    match tryNamedOne "at" form with
    | Some at ->
        let av = values at
        Some(net, Via(floatOf av 0 0.0, floatOf av 1 0.0, floatOf (form |> tryNamedOne "size" |> Option.map values |> Option.defaultValue []) 0 0.8))
    | None -> None

/// Zone copper: each filled_polygon's pts (the actual filled copper — the
/// outline `polygon` is only the pour boundary). Teardrop zones are renderer
/// artifacts, not designed copper, and are skipped.
let private extractZone (declarations: Map<int, string>) (form: Sx) : (string * Primitive) list =
    let isTeardrop =
        // (attr (teardrop ...)) — the marker sits inside the attr form's
        // children, so search one level down (belt and braces: any descendant
        // named teardrop marks the zone).
        let rec hasTeardrop (form: Sx) : bool =
            match form with
            | List (Atom "teardrop" :: _) -> true
            | List xs -> xs |> List.exists hasTeardrop
            | _ -> false

        form |> named "attr" |> List.exists hasTeardrop

    if isTeardrop then []
    else
        let netName =
            match tryNamedOne "net_name" form |> Option.map values with
            | Some [ name ] when name <> "" -> Some name
            | _ -> tryNetName declarations form

        match netName with
        | None -> []
        | Some net ->
            let polys =
                form
                |> named "filled_polygon"
                |> List.choose (fun fp ->
                    match fp |> tryNamedOne "pts" with
                    | Some pts ->
                        let xy =
                            pts
                            |> named "xy"
                            |> List.map (fun f ->
                                let v = values f
                                (floatOf v 0 0.0, floatOf v 1 0.0))

                        if List.length xy >= 3 then Some xy else None
                    | None -> None)

            polys |> List.map (fun pts -> (net, ZonePoly pts))

/// Pad absolute position: footprint (at fx fy frot) then pad (at px py prot);
/// the pad centre is footprint-at + rotate(pad-offset, frot). The pad marker
/// is a DISPLAY aid (§24) — shape/orientation fidelity is not claimed.
let private extractPads (declarations: Map<int, string>) (footprint: Sx) : (string * Primitive) list =
    match tryNamedOne "at" footprint with
    | None -> []
    | Some fAt ->
        let fv = values fAt
        let fx, fy, frot = floatOf fv 0 0.0, floatOf fv 1 0.0, floatOf fv 2 0.0
        let rad = frot * Math.PI / 180.0
        let cosr, sinr = cos rad, sin rad

        footprint
        |> named "pad"
        |> List.choose (fun pad ->
            match tryNetName declarations pad with
            | None -> None
            | Some net ->
                match tryNamedOne "at" pad with
                | None -> None
                | Some pAt ->
                    let pv = values pAt
                    let px, py = floatOf pv 0 0.0, floatOf pv 1 0.0
                    // KiCad board space is y-down: the pad-offset rotation is
                    // (x·cosθ + y·sinθ, −x·sinθ + y·cosθ) — the mirrored form
                    // of the math-CCW matrix (boundary review finding).
                    let ax = fx + px * cosr + py * sinr
                    let ay = fy - px * sinr + py * cosr
                    let sizeV = pad |> tryNamedOne "size" |> Option.map values |> Option.defaultValue []
                    let dia = max (floatOf sizeV 0 1.0) (floatOf sizeV 1 1.0)
                    Some(net, Pad(ax, ay, dia)))

/// Parse a snapshot .kicad_pcb into a net index with primitives converted to
/// SVG-fraction coordinates for the given viewBox ("x0 y0 W H", millimetres).
let buildIndex (pcbPath: string) (viewBox: string) (perNetCap: int) (totalCap: int) : NetIndex =
    let tree = parseSx (File.ReadAllText pcbPath)

    // The board body lives inside the single (kicad_pcb ...) root.
    let board =
        match tree |> List.tryFind (fun f -> headOf f = Some "kicad_pcb") with
        | Some root -> children root
        | None -> tree // tolerate fragment inputs (tests, partial files)

    let declarations =
        board
        |> List.choose (fun f ->
            match headOf f with
            | Some "net" ->
                match values f with
                | n :: name :: _ ->
                    match Int32.TryParse n with
                    | true, num -> Some(num, if name = "" then $"<net {n}>" else name)
                    | _ -> None
                | _ -> None
            | _ -> None)
        |> Map.ofList

    let prims =
        seq {
            for f in board do
                match headOf f with
                | Some "segment" ->
                    match tryNetName declarations f with
                    | Some net -> match extractSegment net f with Some p -> Some p | None -> None
                    | None -> ()
                | Some "via" ->
                    match tryNetName declarations f with
                    | Some net -> match extractVia net f with Some p -> Some p | None -> None
                    | None -> ()
                | Some "footprint" ->
                    for p in extractPads declarations f do
                        Some p
                | Some "zone" ->
                    for p in extractZone declarations f do
                        Some p
                | _ -> ()
        }
        |> Seq.choose id

    let vb = viewBox.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries) |> Array.map Double.Parse
    let x0, y0, w, h = vb[0], vb[1], (if vb.Length > 2 then vb[2] else 1.0), (if vb.Length > 3 then vb[3] else 1.0)
    let w = if w <= 0 then 1.0 else w
    let h = if h <= 0 then 1.0 else h

    let frac (v: float) (o: float) (size: float) = Math.Round((v - o) / size, 5)

    let extracted = prims |> List.ofSeq
    let totalTruncatedPrims = max 0 (extracted.Length - totalCap)

    let groups =
        extracted
        |> List.truncate totalCap
        |> List.groupBy fst
        |> List.map (fun (net, items) ->
            let all = items
            let kept = all |> List.truncate perNetCap

            { name = net
              rows =
                [ for (_, p) in kept ->
                      match p with
                      | Segment (x1, y1, x2, y2, wd) ->
                          let row: float list = [ frac x1 x0 w; frac y1 y0 h; frac x2 x0 w; frac y2 y0 h; frac (wd / 2.0) 0.0 w ]
                          row
                      | Via (x, y, d) ->
                          let row: float list = [ frac x x0 w; frac y y0 h; frac (d / 2.0) 0.0 w ]
                          row
                      | Pad (x, y, d) ->
                          let row: float list = [ frac x x0 w; frac y y0 h; frac (d / 2.0) 0.0 w ]
                          row
                      | ZonePoly pts ->
                          // Even-length row (>= 6) discriminates polygons from
                          // circles (3) and segments (5) in nets.json.
                          let row: float list =
                              [ for (px, py) in pts do
                                    frac px x0 w
                                    frac py y0 h ]

                          row ]
              truncated = all.Length > perNetCap })

    let totalPrims = groups |> List.sumBy (fun g -> g.rows.Length)
    let truncatedNets = groups |> List.filter (fun g -> g.truncated) |> List.length

    { viewBox = viewBox
      nets = groups
      totalPrimitives = totalPrims
      truncatedNets = truncatedNets
      extractedPrimitives = extracted.Length
      totalTruncated = totalTruncatedPrims > 0 }

/// Serialize the index as nets.json for a bundle.
let writeNetsJson (path: string) (index: NetIndex) : unit =
    let json =
        JsonSerializer.Serialize(
            {| viewBox = index.viewBox
               total_primitives = index.totalPrimitives
               extracted_primitives = index.extractedPrimitives
               total_truncated = index.totalTruncated
               note = "display-only highlight index (spec §24): assigned objects, not connectivity analysis"
               nets = [ for g in index.nets -> {| name = g.name; truncated = g.truncated; prims = g.rows |} ] |},
            JsonSerializerOptions(WriteIndented = false)
        )

    File.WriteAllText(path, json)
