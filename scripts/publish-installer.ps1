$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repoRoot 'artifacts\publish'
$installerDeps = Join-Path $repoRoot 'artifacts\installer-deps'
$setupOutput = Join-Path ([Environment]::GetFolderPath('Desktop')) 'KeyceCode-Setup-0.2.0.exe'
$webViewBootstrapper = Join-Path $installerDeps 'MicrosoftEdgeWebView2Setup.exe'
$nodeVersion = '26.13.1'
$nodeArchive = Join-Path $installerDeps "node-v$nodeVersion-win-x64.zip"
$nodeExtract = Join-Path $installerDeps "node-v$nodeVersion-win-x64"
$nodeHostDir = Join-Path $publishDir 'ExtensionHost'
$installerCompiler = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'

if (-not (Test-Path -LiteralPath $installerCompiler)) {
    throw "Inno Setup 6 is required. Install it from https://jrsoftware.org/isdl.php"
}

New-Item -ItemType Directory -Force -Path $publishDir, $installerDeps | Out-Null
if (-not (Test-Path -LiteralPath $webViewBootstrapper)) {
    Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $webViewBootstrapper
}

# Bundle the official Node.js runtime so extension activation does not depend on a separate installation.
$nodeBaseUrl = "https://nodejs.org/dist/v$nodeVersion"
if (-not (Test-Path -LiteralPath $nodeArchive)) {
    Invoke-WebRequest -UseBasicParsing -Uri "$nodeBaseUrl/node-v$nodeVersion-win-x64.zip" -OutFile $nodeArchive
}
$checksums = (Invoke-WebRequest -UseBasicParsing -Uri "$nodeBaseUrl/SHASUMS256.txt").Content
$checksumLine = $checksums -split "`n" | Where-Object { $_ -match "node-v$nodeVersion-win-x64\.zip\s*$" } | Select-Object -First 1
if (-not $checksumLine) { throw "Could not find the Node.js archive checksum for v$nodeVersion." }
$expectedHash = ($checksumLine -split '\s+')[0].ToLowerInvariant()
$actualHash = (Get-FileHash -LiteralPath $nodeArchive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $expectedHash) { throw "Node.js archive SHA-256 mismatch: expected $expectedHash, got $actualHash." }
if (-not (Test-Path -LiteralPath (Join-Path $nodeExtract 'node.exe'))) {
    Expand-Archive -LiteralPath $nodeArchive -DestinationPath $installerDeps -Force
}
New-Item -ItemType Directory -Force -Path $nodeHostDir | Out-Null
Copy-Item -LiteralPath (Join-Path $nodeExtract 'node.exe') -Destination (Join-Path $nodeHostDir 'node.exe') -Force
Copy-Item -LiteralPath (Join-Path $nodeExtract 'LICENSE') -Destination (Join-Path $nodeHostDir 'node-LICENSE.txt') -Force
$signature = Get-AuthenticodeSignature -FilePath $webViewBootstrapper
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike '*Microsoft Corporation*') {
    throw 'The downloaded WebView2 installer does not have a valid Microsoft signature.'
}

dotnet publish (Join-Path $repoRoot 'src\KeyceCode\KeyceCode.csproj') `
    --configuration Release --runtime win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false `
    --output $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

& $installerCompiler "/DPublishDir=$publishDir" "/DInstallerDeps=$installerDeps" `
    "/O$(Split-Path -Parent $setupOutput)" "/F$([System.IO.Path]::GetFileNameWithoutExtension($setupOutput))" `
    (Join-Path $repoRoot 'installer\KeyceCode.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed with exit code $LASTEXITCODE" }

Write-Host "Setup created: $setupOutput"

