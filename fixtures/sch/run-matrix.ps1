# Stage 0 schematic experiment matrix (addendum §49 items 1-7).
# Runs the five hierarchy fixtures through kicad-cli sch export svg and records
# export counts, file naming, page order, shared-instance behavior, --pages
# addressing, mutation behavior, child-only edit propagation, latency, and
# missing-child failure mode. Writes fixtures/sch/matrix-results.json.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File fixtures/sch/run-matrix.ps1 `
#     -KiCadCli "C:/Program Files/KiCad/10.0/bin/kicad-cli.exe"
param(
    [string]$KiCadCli = "C:/Program Files/KiCad/10.0/bin/kicad-cli.exe",
    [string]$WorkRoot = (Join-Path $env:TEMP "pcbo-sch-stage0"),
    [string]$OutJson = ""
)
$ErrorActionPreference = "Stop"
$fixtures = $PSScriptRoot
if (!$OutJson) { $OutJson = Join-Path $fixtures "matrix-results.json" }
$rootUuid = "5b9623a5-6d01-41fc-9865-e1bc779418c8"
$shA = "00000000-0000-0000-0000-00004b3a1333"
$shB = "00000000-0000-0000-0000-00004b3a13a4"
$shC = "00000000-0000-0000-0000-00004b3a13b0"
$project = "complex_hierarchy"
$results = [ordered]@{}
$results.meta = [ordered]@{
    kicadCli    = $KiCadCli
    startedUtc  = (Get-Date).ToUniversalTime().ToString("o")
    workRoot    = $WorkRoot
    psVersion   = $PSVersionTable.PSVersion.ToString()
}
$results.kicadVersion = (& $KiCadCli version) 2>&1 | Out-String

# ---------- helpers ----------------------------------------------------------
$utf8 = [System.Text.UTF8Encoding]::new($false)
function Read-Text($path) {
    $t = [System.IO.File]::ReadAllText($path)
    $script:NL = if ($t -match "`r`n") { "`r`n" } else { "`n" }
    return $t -replace "`r`n", "`n"
}
function Write-Text($path, $text) {
    [System.IO.File]::WriteAllText($path, ($text -replace "`n", $script:NL), $utf8)
}
function T($n, $s) { ("`t" * $n) + $s }

# Sheet block, anchored to the syntax observed in the KiCad demo
# complex_hierarchy.kicad_sch (property keys Sheetname/Sheetfile, UUID instance
# blocks with (project ... (path ...) (page ...))).
function SheetBlock($name, $file, $uuid, $path, $page, $x) {
    $L = @()
    $L += T 1 "(sheet"
    $L += T 2 "(at $x 111.76)"
    $L += T 2 "(size 50.8 36.83)"
    $L += T 2 "(exclude_from_sim no)"
    $L += T 2 "(in_bom yes)"
    $L += T 2 "(on_board yes)"
    $L += T 2 "(dnp no)"
    $L += T 2 "(stroke"
    $L += T 3 "(width 0)"
    $L += T 3 "(type solid)"
    $L += T 2 ")"
    $L += T 2 "(fill"
    $L += T 3 "(color 0 0 0 0.0000)"
    $L += T 2 ")"
    $L += T 2 "(uuid `"$uuid`")"
    $L += T 2 "(property `"Sheetname`" `"$name`""
    $L += T 3 "(at $x 110.9975 0)"
    $L += T 3 "(effects"
    $L += T 4 "(font"
    $L += T 5 "(size 1.524 1.524)"
    $L += T 4 ")"
    $L += T 4 "(justify left bottom)"
    $L += T 3 ")"
    $L += T 2 ")"
    $L += T 2 "(property `"Sheetfile`" `"$file`""
    $L += T 3 "(at $x 149.2001 0)"
    $L += T 3 "(effects"
    $L += T 4 "(font"
    $L += T 5 "(size 1.524 1.524)"
    $L += T 4 ")"
    $L += T 4 "(justify left top)"
    $L += T 3 ")"
    $L += T 2 ")"
    $L += T 2 "(instances"
    $L += T 3 "(project `"$project`""
    $L += T 4 "(path `"$path`""
    $L += T 5 "(page `"$page`")"
    $L += T 4 ")"
    $L += T 3 ")"
    $L += T 2 ")"
    $L += T 1 ")"
    return ($L -join "`n")
}
function Set-RootSheets($text, $blocks) {
    $pat = '(?s)\t\(sheet\b.*?(?=\n\t\(sheet_instances)'
    if ($blocks.Count -eq 0) { return [regex]::Replace($text, $pat, "") }
    return [regex]::Replace($text, $pat, ($blocks -join "`n`n"))
}

# Run kicad-cli sch export svg; capture exit code, wall time, plotted order.
function Invoke-Export($sch, $outDir, $pages) {
    if (Test-Path $outDir) { Remove-Item -Recurse -Force $outDir }
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    $std = Join-Path $outDir "_stdout.txt"
    $err = Join-Path $outDir "_stderr.txt"
    $a = @("sch", "export", "svg", "--output", "`"$outDir`"")
    if ($pages) { $a += @("--pages", ($pages -join ",")) }
    $a += "`"$sch`""
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process -FilePath "`"$KiCadCli`"" -ArgumentList $a -NoNewWindow -Wait -PassThru `
        -RedirectStandardOutput $std -RedirectStandardError $err
    $sw.Stop()
    Start-Sleep -Milliseconds 50
    $stdout = if (Test-Path $std) { [IO.File]::ReadAllText($std) } else { "" }
    $stderr = if (Test-Path $err) { [IO.File]::ReadAllText($err) } else { "" }
    Remove-Item $std, $err -Force -ErrorAction SilentlyContinue
    $svgs = @(Get-ChildItem $outDir -Filter *.svg | Sort-Object Name |
        ForEach-Object { [ordered]@{ file = $_.Name; bytes = $_.Length } })
    $order = @([regex]::Matches($stdout, "'([^']+\.svg)'") | ForEach-Object {
        Split-Path $_.Groups[1].Value -Leaf })
    return [ordered]@{
        exitCode = $p.ExitCode
        ms       = [math]::Round($sw.Elapsed.TotalMilliseconds, 1)
        svgCount = $svgs.Count
        files    = $svgs
        plotOrder = $order
        stdout   = $stdout.Trim()
        stderr   = $stderr.Trim()
    }
}
function Get-Sha256Hex($path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha.ComputeHash([IO.File]::ReadAllBytes($path))) -replace "-", "").ToLowerInvariant()
    } finally { $sha.Dispose() }
}
function Get-DirHash($dir) {
    $h = Get-ChildItem $dir -Filter *.svg | Sort-Object Name | ForEach-Object {
        $_.Name + "=" + (Get-Sha256Hex $_.FullName) }
    return ($h -join "")
}

# ---------- item 1: per-case export (count, naming, order, mapping) ----------
Write-Host "== item 1: per-case export"
$item1 = @()
$expected = @{ case_a_flat = 1; case_b_basic = 3; case_c_nested = 4; case_d_shared = 4; case_e_mutation = 3 }
foreach ($case in $expected.Keys) {
    $out = Join-Path $WorkRoot "item1/$case"
    $r = Invoke-Export (Join-Path $fixtures "$case/root.kicad_sch") $out $null
    $ok = ($r.exitCode -eq 0 -and $r.svgCount -eq $expected[$case])
    $r.expectedPages = $expected[$case]
    $r.pass = $ok
    $item1 += [ordered]@{ case = $case; result = $r }
    Write-Host ("  {0}: exit={1} pages={2}/{3} pass={4} {5}ms" -f $case, $r.exitCode, $r.svgCount, $expected[$case], $ok, $r.ms)
}
$results.item1_perCaseExport = $item1

# ---------- item 2: case D shared instances, refdes per instance -------------
Write-Host "== item 2: case D shared instances"
$dOut = Join-Path $WorkRoot "item2/case_d"
$dExp = Invoke-Export (Join-Path $fixtures "case_d_shared/root.kicad_sch") $dOut $null
$dNet = Join-Path $WorkRoot "item2/case_d.net"
$p = Start-Process -FilePath "`"$KiCadCli`"" -NoNewWindow -Wait -PassThru `
    -ArgumentList @("sch", "export", "netlist", "--output", "`"$dNet`"", "`"$(Join-Path $fixtures 'case_d_shared/root.kicad_sch')`"") `
    -RedirectStandardOutput (Join-Path $WorkRoot "n_out.txt") -RedirectStandardError (Join-Path $WorkRoot "n_err.txt")
$netWarn = [IO.File]::ReadAllText((Join-Path $WorkRoot "n_err.txt")).Trim()
Remove-Item (Join-Path $WorkRoot "n_out.txt"), (Join-Path $WorkRoot "n_err.txt") -Force -ErrorAction SilentlyContinue
$nt = [IO.File]::ReadAllText($dNet)
$compMap = [regex]::Matches($nt, '\(comp\s+\(ref "([^"]+)"\)(?:(?!\(comp\b).)*?\(sheetpath\s+\(names "([^"]*)"\)', 'Singleline') |
    ForEach-Object { [pscustomobject]@{ ref = $_.Groups[1].Value; sheet = $_.Groups[2].Value } }
$perSheet = $compMap | Group-Object sheet | ForEach-Object {
    [ordered]@{ sheet = $_.Name; compCount = $_.Count; sampleRefs = @($_.Group | Select-Object -First 6 -ExpandProperty ref) }
}
$cRefs = @($compMap | Where-Object { $_.sheet -eq "/Channel C/" } | ForEach-Object { $_.ref })
$aRefs = @($compMap | Where-Object { $_.sheet -eq "/Channel A/" } | ForEach-Object { $_.ref })
$dupC = @($cRefs | Where-Object { $aRefs -contains $_ } | Select-Object -Unique -First 8)
$results.item2_caseD_sharedInstances = [ordered]@{
    export           = $dExp
    netlistExitCode  = $p.ExitCode
    netlistWarning   = $netWarn
    compsPerSheet    = $perSheet
    channelC_refReuseExamples = $dupC
}
Write-Host ("  case D: exit={0} pages={1}; netlist exit={2}" -f $dExp.exitCode, $dExp.svgCount, $p.ExitCode)
$perSheet | ForEach-Object { Write-Host ("    {0}: {1} comps e.g. {2}" -f $_.sheet, $_.compCount, ($_.sampleRefs -join ",")) }

# ---------- item 3: case D --pages addressing of shared instances ------------
Write-Host "== item 3: case D --pages"
$item3 = @()
foreach ($sel in @(@("3", "4"), @("3"), @("1"), @("1", "2", "3", "4"), @("9"), @("Channel B"))) {
    $out = Join-Path $WorkRoot ("item3/pages_" + (($sel -join "_") -replace " ", "_"))
    $r = Invoke-Export (Join-Path $fixtures "case_d_shared/root.kicad_sch") $out $sel
    $item3 += [ordered]@{ pagesArg = ($sel -join ","); result = $r }
    Write-Host ("  --pages '{0}': exit={1} files={2}" -f ($sel -join ","), $r.exitCode, (($r.files | ForEach-Object { $_.file }) -join " | "))
}
$rBlank = Invoke-Export (Join-Path $fixtures "case_d_shared/root.kicad_sch") (Join-Path $WorkRoot "item3/pages_blank") @("")
$item3 += [ordered]@{ pagesArg = "(blank)"; result = $rBlank }
Write-Host ("  --pages '': exit={0} files={1}" -f $rBlank.exitCode, (($rBlank.files | ForEach-Object { $_.file }) -join " | "))
$results.item3_caseD_pages = $item3

# ---------- item 4: case E mutations (rename/reorder/retarget/add/remove) ----
Write-Host "== item 4: case E mutations"
$eDir = Join-Path $WorkRoot "item4/case_e"
New-Item -ItemType Directory -Path (Split-Path $eDir -Parent) -Force | Out-Null
Copy-Item -Recurse -Force (Join-Path $fixtures "case_e_mutation") $eDir
$rootPath = Join-Path $eDir "root.kicad_sch"
$item4 = @()
function Mutate-And-Export($label, $sheets, $note) {
    $t = Read-Text $rootPath
    $blocks = @($sheets | ForEach-Object {
        SheetBlock $_.name $_.file $_.uuid "/$rootUuid" $_.page $_.x })
    Write-Text $rootPath (Set-RootSheets $t $blocks)
    $out = Join-Path $eDir ("out_" + ($label -replace " ", "_"))
    $r = Invoke-Export $rootPath $out $null
    $script:item4 += [ordered]@{
        mutation = $label; note = $note
        layout   = @($sheets | ForEach-Object { "$($_.name) -> $($_.file) page $($_.page)" })
        result   = $r
    }
    Write-Host ("  [{0}] exit={1} pages={2}: {3}" -f $label, $r.exitCode, $r.svgCount, (($r.files | ForEach-Object { $_.file }) -join " | "))
}
$sheets = @(
    [pscustomobject]@{ name = "Power"; file = "power.kicad_sch"; uuid = $shA; page = "2"; x = "71.12" },
    [pscustomobject]@{ name = "MCU";   file = "mcu.kicad_sch";   uuid = $shB; page = "3"; x = "154.94" }
)
Mutate-And-Export "M0 baseline" $sheets "unchanged copy of case B"
$sheets[0].name = "Power Supply"
Mutate-And-Export "M1 rename" $sheets "Sheetname Power -> 'Power Supply' (space in name)"
$tmp = $sheets[0].page; $sheets[0].page = $sheets[1].page; $sheets[1].page = $tmp
Mutate-And-Export "M2 reorder" $sheets "page numbers 2/3 swapped between the two sheets"
$sheets[1].file = "power.kicad_sch"
Mutate-And-Export "M3 retarget" $sheets "MCU now points at power.kicad_sch (same file twice)"
Copy-Item (Join-Path $eDir "mcu.kicad_sch") (Join-Path $eDir "extra.kicad_sch")
$sheets += [pscustomobject]@{ name = "Extra"; file = "extra.kicad_sch"; uuid = $shC; page = "4"; x = "238.76" }
Mutate-And-Export "M4 add" $sheets "third sheet Extra added as page 4 (aux is a reserved Windows device name, hence 'extra')"
$sheets = @($sheets | Where-Object { $_.name -ne "Power Supply" })
Mutate-And-Export "M5 remove" $sheets "Power Supply (page 3 after M2 swap) removed -> page gap 1,2,4"
$item4 += [ordered]@{
    mutation = "M5b --pages probe on gapped design"
    note     = "on M5 layout: --pages 2 vs --pages 3"
    probes   = @(
        (Invoke-Export $rootPath (Join-Path $eDir "out_probe_p2") @("2")),
        (Invoke-Export $rootPath (Join-Path $eDir "out_probe_p3") @("3"))
    )
}
$results.item4_caseE_mutations = $item4

# ---------- item 5: child-only edit reflected via root export ----------------
Write-Host "== item 5: child-only edit"
$bDir = Join-Path $WorkRoot "item5/case_b"
Copy-Item -Recurse -Force (Join-Path $fixtures "case_b_basic") $bDir
$bRoot = Join-Path $bDir "root.kicad_sch"
$bMcu = Join-Path $bDir "mcu.kicad_sch"
$baseOut = Join-Path $bDir "out_before"
$null = Invoke-Export $bRoot $baseOut $null
$hashBefore = Get-DirHash $baseOut
$rootHashBefore = Get-Sha256Hex $bRoot
$t = Read-Text $bMcu
$t = $t.Replace('(title "Complex hierarchy: demo")', '(title "MCU child-only edit AFTER")')
Write-Text $bMcu $t
$afterOut = Join-Path $bDir "out_after"
$r5 = Invoke-Export $bRoot $afterOut $null
$hashAfter = Get-DirHash $afterOut
$rootHashAfter = Get-Sha256Hex $bRoot
$results.item5_childOnlyEdit = [ordered]@{
    rootFileUntouched = ($rootHashBefore -eq $rootHashAfter)
    mcuSvgChanged     = ($hashBefore -ne $hashAfter)
    hashBefore        = $hashBefore.Substring(0, 16)
    hashAfter         = $hashAfter.Substring(0, 16)
    export            = $r5
}
Write-Host ("  root untouched={0} mcu.svg changed={1}" -f ($rootHashBefore -eq $rootHashAfter), ($hashBefore -ne $hashAfter))

# ---------- item 6: latency 1/5/10/20 logical pages, 3 runs each -------------
Write-Host "== item 6: latency"
$item6 = @()
$chanSrc = Join-Path $fixtures "case_d_shared/channel.kicad_sch"
foreach ($pagesTotal in 1, 5, 10, 20) {
    $dir = Join-Path $WorkRoot "item6/p$pagesTotal"
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    Copy-Item $chanSrc (Join-Path $dir "channel.kicad_sch")
    $t = Read-Text (Join-Path $fixtures "case_a_flat/root.kicad_sch")
    $blocks = @()
    for ($i = 2; $i -le $pagesTotal; $i++) {
        $n = $i - 1
        $blocks += SheetBlock "Channel $n" "channel.kicad_sch" ("00000000-0000-0000-0000-00004b3c{0:x4}" -f $n) "/$rootUuid" "$i" "71.12"
    }
    $rp = Join-Path $dir "root.kicad_sch"
    # case_a root has no sheet region; insert the generated blocks directly
    # before the root's (sheet_instances) block.
    $inserted = [regex]::Replace($t, '\n\t\(sheet_instances',
        "`n" + ($blocks -join "`n`n") + "`n`t(sheet_instances")
    Write-Text $rp $inserted
    $runs = @()
    for ($run = 1; $run -le 3; $run++) {
        $r = Invoke-Export $rp (Join-Path $dir "out$run") $null
        $runs += $r.ms
        if ($r.exitCode -ne 0 -or $r.svgCount -ne $pagesTotal) {
            Write-Host ("  P={0} run={1} FAILED exit={2} pages={3}" -f $pagesTotal, $run, $r.exitCode, $r.svgCount)
        }
    }
    $mean = ($runs | Measure-Object -Average).Average
    $min = ($runs | Measure-Object -Minimum).Minimum
    $max = ($runs | Measure-Object -Maximum).Maximum
    $item6 += [ordered]@{
        pages = $pagesTotal
        childInstances = ($pagesTotal - 1)
        runsMs = $runs
        meanMs = [math]::Round($mean, 1)
        minMs  = [math]::Round($min, 1)
        maxMs  = [math]::Round($max, 1)
        spreadMs = [math]::Round($max - $min, 1)
    }
    Write-Host ("  P={0}: runs={1} mean={2}ms spread={3}ms" -f $pagesTotal, ($runs -join "/"), [math]::Round($mean, 1), [math]::Round($max - $min, 1))
}
$results.item6_latency = $item6

# ---------- item 7: missing child failure mode (on case E work copy) ---------
Write-Host "== item 7: missing child"
Remove-Item (Join-Path $eDir "extra.kicad_sch") -Force
$r7 = Invoke-Export $rootPath (Join-Path $eDir "out_missing") @("4")
$r7all = Invoke-Export $rootPath (Join-Path $eDir "out_missing_all") $null
$results.item7_missingChild = [ordered]@{
    removedFile  = "extra.kicad_sch"
    pagesProbe   = $r7
    fullExport   = $r7all
}
Write-Host ("  --pages 4: exit={0} stderr='{1}'" -f $r7.exitCode, $r7.stderr)
Write-Host ("  all pages: exit={0} files={1}" -f $r7all.exitCode, (($r7all.files | ForEach-Object { $_.file }) -join " | "))

# ---------- write results ----------------------------------------------------
$results.finishedUtc = (Get-Date).ToUniversalTime().ToString("o")
$results | ConvertTo-Json -Depth 6 | Out-File -FilePath $OutJson -Encoding utf8
Write-Host "== results written: $OutJson"
