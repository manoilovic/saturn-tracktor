param(
    [string]$Iscc = 'ISCC.exe',
    [string]$DotNet = 'dotnet'
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskBuild = Join-Path $taskRoot 'build'
$taskPayload = Join-Path $PSScriptRoot 'payload'
$taskSource = Join-Path $taskBuild 'source'
$taskArtifacts = Join-Path $taskRoot 'artifacts'
New-Item -ItemType Directory -Force -Path $taskBuild,$taskPayload,$taskSource,$taskArtifacts,(Join-Path $taskPayload 'licenses') | Out-Null

# Signed driver files must keep their original bytes, including INF line endings.
$taskDriverHashes = @{
    'fxvad.inf' = 'BA175CFDDD91B87BDDA3F1DF2A70249E1742E846B843381EB0438B70F91A110A'
    'fxvad.sys' = '425629B6309000013E8CD1A9B827BEE365D21C9F743873AADD0C3BC96A999D2A'
    'fxvadntamd64.cat' = '25C8DAE186155D20F74FEEDEFB4F84161E4215925B8FD0C898F68F3E50EBCD7D'
    'fxdevcon64.exe' = 'CA83823A9A29AD6FCB3507FBC48A3F11B63DEA593214C924420A30D0089A9A57'
}
foreach ($taskDriverName in $taskDriverHashes.Keys) {
    $taskDriverFile = Join-Path $taskRoot ('SaturnTracktor/Assets/' + $taskDriverName)
    if ((Get-FileHash -LiteralPath $taskDriverFile -Algorithm SHA256).Hash -ne $taskDriverHashes[$taskDriverName]) {
        throw ('Signed audio component was modified: ' + $taskDriverName)
    }
}

& $DotNet publish (Join-Path $taskRoot 'SaturnTracktor/SaturnTracktor.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $taskBuild 'publish')
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

$taskProject = Join-Path $taskSource 'SaturnTracktor'
New-Item -ItemType Directory -Force -Path $taskProject,(Join-Path $taskSource 'installer') | Out-Null
Get-ChildItem (Join-Path $taskRoot 'SaturnTracktor') -File | Where-Object { $_.Extension -in '.cs','.csproj','.manifest','.txt' } | Copy-Item -Destination $taskProject -Force
Copy-Item (Join-Path $taskRoot 'SaturnTracktor/Assets') -Destination $taskProject -Recurse -Force
Copy-Item (Join-Path $taskRoot 'ThirdParty') -Destination $taskSource -Recurse -Force
Copy-Item (Join-Path $PSScriptRoot '*.iss'),(Join-Path $PSScriptRoot '*.ps1') -Destination (Join-Path $taskSource 'installer') -Force
Copy-Item (Join-Path $taskRoot 'README.md'),(Join-Path $taskRoot 'BUILD.md'),(Join-Path $taskRoot 'LICENSE') -Destination $taskSource -Force
Compress-Archive -Path (Join-Path $taskSource '*') -DestinationPath (Join-Path $taskPayload 'SaturnTracktor-source.zip') -Force

Copy-Item (Join-Path $taskBuild 'publish/SaturnTracktor.exe') -Destination $taskPayload -Force
Copy-Item (Join-Path $taskRoot 'README.md') -Destination $taskPayload -Force
Copy-Item (Join-Path $taskRoot 'LICENSE') -Destination (Join-Path $taskPayload 'LICENSE.txt') -Force
Copy-Item (Join-Path $taskRoot 'SaturnTracktor/Assets/Saturn.ico') -Destination $taskPayload -Force
Copy-Item (Join-Path $taskRoot 'SaturnTracktor/Assets/ThirdPartyNotices.txt') -Destination (Join-Path $taskPayload 'THIRD-PARTY-NOTICES.txt') -Force
Get-ChildItem (Join-Path $taskRoot 'SaturnTracktor/Assets') -File | Where-Object { $_.Name -match 'LICENSE|license|MIT|AGPL' } | Copy-Item -Destination (Join-Path $taskPayload 'licenses') -Force
Copy-Item (Join-Path $taskRoot 'ThirdParty/DotNet/*') -Destination (Join-Path $taskPayload 'licenses') -Force

$taskBootstrapper = Join-Path $taskPayload 'MicrosoftEdgeWebview2Setup.exe'
if (-not (Test-Path -LiteralPath $taskBootstrapper)) {
    Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $taskBootstrapper
}
$taskSignature = Get-AuthenticodeSignature -LiteralPath $taskBootstrapper
if ($taskSignature.Status -ne 'Valid' -or $taskSignature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
    throw 'The WebView2 bootstrapper must have a valid Microsoft signature.'
}
& $Iscc ("/O$taskArtifacts") (Join-Path $PSScriptRoot 'SaturnTracktor.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
Copy-Item (Join-Path $taskPayload 'SaturnTracktor-source.zip') -Destination $taskArtifacts -Force
Get-ChildItem $taskArtifacts -File | Where-Object { $_.Extension -in '.exe','.zip' } | ForEach-Object {
    (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash + '  ' + $_.Name
} | Set-Content (Join-Path $taskArtifacts 'SHA256SUMS.txt') -Encoding ascii
