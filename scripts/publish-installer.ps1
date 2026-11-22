$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repoRoot 'artifacts\publish'
$installerDeps = Join-Path $repoRoot 'artifacts\installer-deps'
$setupOutput = Join-Path ([Environment]::GetFolderPath('Desktop')) 'KeyceCode-Setup-0.1.0.exe'
$webViewBootstrapper = Join-Path $installerDeps 'MicrosoftEdgeWebView2Setup.exe'
$installerCompiler = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'

if (-not (Test-Path -LiteralPath $installerCompiler)) {
    throw "Inno Setup 6 is required. Install it from https://jrsoftware.org/isdl.php"
}

New-Item -ItemType Directory -Force -Path $publishDir, $installerDeps | Out-Null
if (-not (Test-Path -LiteralPath $webViewBootstrapper)) {
    Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $webViewBootstrapper
}
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

