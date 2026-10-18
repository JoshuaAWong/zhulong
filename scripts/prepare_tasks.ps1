$ErrorActionPreference = 'Stop'

if (-not $env:ZHULONG_PYTHONW) { throw 'ZHULONG_PYTHONW is required.' }
if (-not $env:ZHULONG_PROJECT) { throw 'ZHULONG_PROJECT is required.' }
$outDir = $env:ZHULONG_TASK_XML_DIR
if (-not $outDir) { $outDir = $env:TEMP }
if (-not (Test-Path -LiteralPath $outDir -PathType Container)) {
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
}

$pythonw = [System.Security.SecurityElement]::Escape($env:ZHULONG_PYTHONW)
$project = [System.Security.SecurityElement]::Escape($env:ZHULONG_PROJECT)
$templates = Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.xml' -File
$written = 0

foreach ($template in $templates) {
    $content = [System.IO.File]::ReadAllText($template.FullName)
    $content = $content.Replace('__PYTHONW__', $pythonw).Replace('__PROJECT__', $project)
    if ($content -match '<LogonTrigger') {
        $outName = 'zhulong_autostart.xml'
    } elseif ($content -match 'PT5M') {
        $outName = 'zhulong_watchdog.xml'
    } elseif ($content -match 'PT1H') {
        $outName = 'zhulong_ac7.xml'
    } else {
        continue
    }
    $destination = Join-Path $outDir $outName
    [System.IO.File]::WriteAllText($destination, $content, [System.Text.Encoding]::Unicode)
    $document = New-Object System.Xml.XmlDocument
    $document.Load($destination)
    $written += 1
}

if ($written -ne 3) {
    throw "Expected 3 task XML files, generated $written"
}
