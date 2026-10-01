module PcbObserver.ConfigTests

open System
open System.IO
open PcbObserver.Config
open PcbObserver.Tests
open Xunit

[<Fact>]
let ``flag beats config beats default`` () =
    let root = tempDir ()

    try
        let cfgPath = Path.Combine(root, "config.json")
        File.WriteAllText(cfgPath, """{ "cli": "C:/cfg/kicad-cli.exe", "port": 9000, "debounce-ms": 250, "history-quota-mb": 256, "drc": true }""")

        let (cfg, path) = resolve Pcb (Some cfgPath) root None None None None None None None None

        Assert.Equal(Some cfgPath, path)
        Assert.Equal("C:/cfg/kicad-cli.exe", cfg.cli.value)
        Assert.Equal("config", cfg.cli.source)
        Assert.Equal("9000", cfg.port.value)
        Assert.Equal("250", cfg.debounceMs.value)
        Assert.Equal("256", cfg.historyQuotaMb.value)
        Assert.True(fst cfg.drc)
        Assert.Equal("config", snd cfg.drc)
        // No layers/output keys → defaults.
        Assert.True(cfg.layers.IsNone)
        Assert.True(cfg.output.IsNone)

        // A CLI flag overrides the config value.
        let (cfg2, _) = resolve Pcb (Some cfgPath) root (Some "C:/flag/cli.exe") (Some 9100) None None None None None None
        Assert.Equal("C:/flag/cli.exe", cfg2.cli.value)
        Assert.Equal("flag", cfg2.cli.source)
        Assert.Equal("9100", cfg2.port.value)
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``missing config file means defaults`` () =
    let root = tempDir ()

    try
        let (cfg, path) = resolve Pcb None root None None None None None None None None

        Assert.True(path.IsNone)
        Assert.Equal("default", cfg.cli.source)
        Assert.Equal("8765", cfg.port.value)
        Assert.Equal("500", cfg.debounceMs.value)
        Assert.Equal("512", cfg.historyQuotaMb.value)
        Assert.False(fst cfg.drc)
        Assert.Equal("default", snd cfg.drc)
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``unknown key rejects startup`` () =
    let root = tempDir ()

    try
        let cfgPath = Path.Combine(root, "config.json")
        File.WriteAllText(cfgPath, """{ "port": 9000, "bogus-key": 1 }""")

        Assert.Throws<Exception>(fun () -> resolve Pcb (Some cfgPath) root None None None None None None None None |> ignore)
        |> ignore
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``malformed json rejects startup`` () =
    let root = tempDir ()

    try
        let cfgPath = Path.Combine(root, "config.json")
        File.WriteAllText(cfgPath, "{ not json")

        Assert.Throws<Exception>(fun () -> resolve Pcb (Some cfgPath) root None None None None None None None None |> ignore)
        |> ignore
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``sch kind defaults erc not drc`` () =
    let root = tempDir ()

    try
        let cfgPath = Path.Combine(root, "config.json")
        File.WriteAllText(cfgPath, """{ "erc": true }""")

        let (cfg, _) = resolve Sch (Some cfgPath) root None None None None None None None None

        Assert.True(cfg.erc)
        Assert.Equal("config", cfg.ercSource)
    finally
        Directory.Delete(root, true)
