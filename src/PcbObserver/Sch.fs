module PcbObserver.Sch

open System
open System.Collections.Generic
open System.IO

/// One hierarchical sheet reference inside a parent schematic.
type SheetEdge = { parent: string; sheetName: string; sheetFile: string }

/// Recursive discovery result from a root schematic (SCH-FR-002).
type Discovery =
    { root: string
      files: string list
      edges: SheetEdge list
      missing: string list }

/// A logical sheet instance page (SCH-FR-003): identity is the sheet-name
/// chain, never the exported file name alone (findings SS49-1).
type Page =
    { chain: string list
      file: string
      sheetFile: string }

/// Minimal s-expression text scanner: returns the source substrings of every
/// balanced `(sheet ...)` block, honoring quoted strings (with escapes) so
/// parentheses inside property values cannot confuse the matcher.
let sheetBlocks (text: string) : string list =
    let blocks = ResizeArray<string>()
    let n = text.Length
    let mutable i = 0
    let mutable depth = 0
    let mutable sheetStart = -1

    while i < n do
        let c = text[i]

        if c = '"' then
            // Skip the string literal (escape-aware).
            i <- i + 1

            let mutable closed = false

            while i < n && not closed do
                if text[i] = '\\' && i + 1 < n then i <- i + 2
                elif text[i] = '"' then
                    closed <- true
                    i <- i + 1
                else
                    i <- i + 1
        elif c = '(' then
            if depth = 1 && sheetStart < 0 then
                // Peek: does this top-level form start with the symbol `sheet`?
                let j = i + 1

                if j + 5 <= n
                   && text.Substring(j, 5) = "sheet"
                   && (j + 5 = n
                       || text[j + 5] = ' '
                       || text[j + 5] = '\t'
                       || text[j + 5] = '\n'
                       || text[j + 5] = '\r') then
                    sheetStart <- i

            depth <- depth + 1
            i <- i + 1
        elif c = ')' then
            depth <- depth - 1

            if depth = 1 && sheetStart >= 0 then
                blocks.Add(text.Substring(sheetStart, i + 1 - sheetStart))
                sheetStart <- -1

            i <- i + 1
        else
            i <- i + 1

    Seq.toList blocks

let private unescape (s: string) : string = s.Replace("\\\"", "\"").Replace("\\\\", "\\")

/// First two quoted strings inside a `(property ...)` block.
let private propertyValue (block: string) (name: string) : string option =
    // Find `(property` occurrences and parse their two string literals.
    let mutable idx = 0

    let mutable result = None

    while idx < block.Length - 10 && result.IsNone do
        idx <- block.IndexOf("(property", idx, StringComparison.Ordinal)

        if idx < 0 then
            result <- None
            idx <- block.Length
        else
            // Collect quoted literals after `(property`.
            let literals = ResizeArray<string>()
            let mutable j = idx + 9
            let mutable guardIter = 0

            while j < block.Length && literals.Count < 2 && guardIter < 3 do
                guardIter <- guardIter + 1

                while j < block.Length && block[j] <> '"' do
                    j <- j + 1

                if j < block.Length then
                    j <- j + 1
                    let start = j

                    while j < block.Length && block[j] <> '"' do
                        if block[j] = '\\' && j + 1 < block.Length then j <- j + 1
                        j <- j + 1

                    if j < block.Length then
                        literals.Add(block.Substring(start, j - start))
                        j <- j + 1
                    else
                        j <- block.Length

            if literals.Count >= 2
               && literals[0] = name
               && (idx = 0
                   || not (Char.IsLetter block[idx - 1])) then
                result <- Some(unescape literals[1])
            else
                idx <- idx + 9

    result

/// Sheet references of one schematic file: (Sheetname, Sheetfile) pairs.
let sheetReferences (text: string) : (string * string) list =
    [ for block in sheetBlocks text do
          match propertyValue block "Sheetname", propertyValue block "Sheetfile" with
          | Some name, Some file -> name, file
          | _ -> () ]

/// Recursive dependency discovery from a root schematic. Missing children are
/// reported, not thrown (SCH-FR-016: explicit state, session survives).
let discover (rootPath: string) : Discovery =
    let rootPath = Path.GetFullPath rootPath
    let visited = HashSet<string>()
    let files = ResizeArray<string>()
    let edges = ResizeArray<SheetEdge>()
    let missing = ResizeArray<string>()
    let queue = Queue<string>()

    visited.Add rootPath |> ignore
    files.Add rootPath
    queue.Enqueue rootPath

    while queue.Count > 0 do
        let parent = queue.Dequeue ()

        // The root must be readable — a swallowed failure here would silently
        // masquerade as "no hierarchy". Children tolerate mid-traversal races.
        let readParent () = File.ReadAllText parent

        let text =
            if parent = rootPath then
                readParent ()
            else
                try
                    readParent ()
                with _ ->
                    ""

        if text <> "" then
            for (name, file) in sheetReferences text do
                edges.Add { parent = parent; sheetName = name; sheetFile = file }

                let childPath =
                    Path.GetFullPath(Path.Combine(Path.GetDirectoryName parent, file))

                if File.Exists childPath then
                    if visited.Add childPath then
                        files.Add childPath
                        queue.Enqueue childPath
                elif not (missing.Contains childPath) then
                    missing.Add childPath

    { root = rootPath
      files = Seq.toList files
      edges = Seq.toList edges
      missing = Seq.toList missing }

/// Logical page model + anomalies. Pages are keyed by sheet-name chain; the
/// exported file name is derived (findings SS49-1: `<stem>[-<Chain>...].svg`,
/// spaces kept). Duplicate output names are reported as anomalies instead of
/// silently overwriting (findings SS49-1 caveat).
let mapPages (disc: Discovery) : Page list * string list =
    let stem = Path.GetFileNameWithoutExtension disc.root
    let childrenOf = disc.edges |> List.groupBy (fun e -> e.parent) |> dict

    let pages = ResizeArray<Page>()
    pages.Add({ chain = []; file = $"{stem}.svg"; sheetFile = disc.root })

    let anomalies = ResizeArray<string>()
    let queue = Queue<string * string list>()
    queue.Enqueue(disc.root, [])

    while queue.Count > 0 do
        let (parent, chain) = queue.Dequeue()

        match childrenOf.TryGetValue parent with
        | true, edges ->
            for edge in edges do
                let childChain = chain @ [ edge.sheetName ]
                let joined = String.Join("-", childChain)
                let file = $"{stem}-{joined}.svg"
                let childPath =
                    Path.GetFullPath(Path.Combine(Path.GetDirectoryName parent, edge.sheetFile))

                pages.Add({ chain = childChain; file = file; sheetFile = childPath })
                queue.Enqueue(childPath, childChain)
        | _ -> ()

    let duplicates =
        pages
        |> Seq.groupBy (fun p -> p.file)
        |> Seq.filter (fun (_, g) -> Seq.length g > 1)

    for (file, _) in duplicates do
        anomalies.Add $"duplicate page output name: {file} (duplicate Sheetname chain)"

    (Seq.toList pages, Seq.toList anomalies)
