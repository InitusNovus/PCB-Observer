module PcbObserver.Config

open System
open System.IO
open System.Text.Json

/// §38 configuration file. CLI flags take precedence over the file, the file
/// over built-in defaults. Unknown keys and malformed JSON are STARTUP ERRORS
/// (§38: never silently ignored); a missing file simply means defaults.

type WatchKind =
    | Pcb
    | Sch

type ConfigValue =
    | FromFlag of string
    | FromConfig of string
    | Default of string

    member this.value =
        match this with
        | FromFlag v
        | FromConfig v
        | Default v -> v
    member this.source =
        match this with
        | FromFlag _ -> "flag"
        | FromConfig _ -> "config"
        | Default _ -> "default"

/// Effective settings for one watch run, with per-key provenance.
type EffectiveConfig =
    { cli: ConfigValue
      port: ConfigValue
      debounceMs: ConfigValue
      layers: ConfigValue option // None = renderer default set
      historyQuotaMb: ConfigValue
      drc: bool * string // enabled, source
      erc: bool
      ercSource: string
      output: ConfigValue option }

let private allowedKeys =
    set [ "cli"; "port"; "debounce-ms"; "layers"; "history-quota-mb"; "drc"; "erc"; "output" ]

/// Parse and validate a config file. Throws with an explicit message on
/// unknown keys or malformed JSON — the caller turns that into a startup
/// error, never a silent fallback.
let loadFile (path: string) : Map<string, string> =
    use doc =
        try
            JsonDocument.Parse(File.ReadAllText path)
        with e ->
            failwith $"config file is not valid JSON ({path}): {e.Message}"

    if doc.RootElement.ValueKind <> JsonValueKind.Object then
        failwith $"config file must be a JSON object ({path})"

    let mutable result = Map.empty

    for pair in doc.RootElement.EnumerateObject() do
        if not (allowedKeys.Contains pair.Name) then
            failwith $"config file has an unknown key '{pair.Name}' ({path}) — remove it or fix the spelling"

        // Eagerly extract the raw text: the document is disposed on return,
        // and lazy JsonElements would throw ObjectDisposedException later.
        let raw =
            match pair.Value.ValueKind with
            | JsonValueKind.String -> pair.Value.GetString()
            | JsonValueKind.True
            | JsonValueKind.False
            | JsonValueKind.Number -> pair.Value.GetRawText()
            | _ -> failwith $"config key '{pair.Name}' must be a string, number, or boolean ({path})"

        result <- Map.add pair.Name raw result

    result

/// Resolve one string setting with flag > config > default precedence.
let private resolveString (flag: string option) (configKey: string) (config: Map<string, string>) (defaultValue: string) : ConfigValue =
    match flag with
    | Some v -> FromFlag v
    | None ->
        match config.TryFind configKey with
        | Some v -> FromConfig v
        | None -> Default defaultValue

let private resolveInt (flag: int option) (configKey: string) (config: Map<string, string>) (defaultValue: int) : ConfigValue =
    match flag with
    | Some v -> FromFlag(string v)
    | None ->
        match config.TryFind configKey with
        | Some v -> FromConfig v
        | None -> Default(string defaultValue)

let private resolveBool (flag: bool option) (configKey: string) (config: Map<string, string>) : (bool * string) =
    match flag with
    | Some v -> (v, "flag")
    | None ->
        match config.TryFind configKey with
        | Some v when v = "true" -> (true, "config")
        | Some v when v = "false" -> (false, "config")
        | _ -> (false, "default")

let private defaultValueFor (kind: WatchKind) (key: string) : string =
    match kind, key with
    | _, "cli" -> @"C:\Program Files\KiCad\10.0\bin\kicad-cli.exe"
    | _, "port" -> "8765"
    | _, "debounce-ms" -> "500"
    | _, "history-quota-mb" -> "512"
    | Pcb, "drc"
    | Sch, "erc" -> "false"
    | _ -> ""

/// Build the effective configuration. `configPathOpt` is the --config value
/// (Some forces that file); None falls back to <observerRoot>/config.json.
let resolve
    (kind: WatchKind)
    (configPathOpt: string option)
    (observerRoot: string)
    (cliFlag: string option)
    (portFlag: int option)
    (debounceFlag: int option)
    (layersFlag: string option)
    (quotaFlag: int option)
    (drcFlag: bool option)
    (ercFlag: bool option)
    (outputFlag: string option)
    : EffectiveConfig * string option =
    let configPath =
        match configPathOpt with
        | Some p -> Some p
        | None ->
            let defaultPath = Path.Combine(observerRoot, "config.json")
            if File.Exists defaultPath then Some defaultPath else None

    let config =
        match configPath with
        | Some p -> loadFile p
        | None -> Map.empty

    let def k = defaultValueFor kind k

    let layersValue =
        match layersFlag with
        | Some v -> Some(FromFlag v)
        | None ->
            match config.TryFind "layers" with
            | Some v -> Some(FromConfig v)
            | None -> None

    let outputValue =
        match outputFlag with
        | Some v -> Some(FromFlag v)
        | None ->
            match config.TryFind "output" with
            | Some v -> Some(FromConfig v)
            | None -> None

    let drc', drcSrc = resolveBool drcFlag "drc" config
    let erc', ercSrc = resolveBool ercFlag "erc" config

    ( { cli = resolveString cliFlag "cli" config (def "cli")
        port = resolveInt portFlag "port" config (int (def "port"))
        debounceMs = resolveInt debounceFlag "debounce-ms" config (int (def "debounce-ms"))
        layers = layersValue
        historyQuotaMb = resolveInt quotaFlag "history-quota-mb" config (int (def "history-quota-mb"))
        drc = (drc', drcSrc)
        erc = erc'
        ercSource = ercSrc
        output = outputValue },
      configPath )

/// Print the effective configuration with per-key provenance.
let print (cfg: EffectiveConfig) (configPath: string option) : unit =
    let src (path: string option) =
        match path with
        | Some p -> $" (config: {p})"
        | None -> ""

    printfn $"Configuration{src configPath}:"

    let line (name: string) (v: ConfigValue) =
        printfn $"  {name,-16} = {v.value} [{v.source}]"

    line "cli" cfg.cli
    line "port" cfg.port
    line "debounce-ms" cfg.debounceMs
    (match cfg.layers with
     | Some l -> line "layers" l
     | None -> printfn "  %-16s = <renderer default set> [default]" "layers")

    line "history-quota-mb" cfg.historyQuotaMb
    let drcName = "drc"
    let ercName = "erc"
    printfn $"  {drcName,-16} = {fst cfg.drc} [{snd cfg.drc}]"
    printfn $"  {ercName,-16} = {cfg.erc} [{cfg.ercSource}]"

    (match cfg.output with
     | Some o -> line "output" o
     | None -> printfn "  %-16s = <observer root> [default]" "output")
