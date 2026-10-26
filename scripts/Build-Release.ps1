param([string]$AppVersion='',[string]$OutputDirectory='',[string]$InformationalVersion='',[switch]$SkipSetup)
$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
if(-not $AppVersion){$AppVersion=([xml](Get-Content -Raw (Join-Path $repoRoot 'ClaudeLocalBridge.csproj'))).Project.PropertyGroup.Version}
if($AppVersion -notmatch '^\d+\.\d+\.\d+$'){throw 'Expected a three-part application version.'}
if(-not $OutputDirectory){$OutputDirectory=Join-Path $repoRoot ('artifacts\release\'+$AppVersion+'\win-x64')}
$OutputDirectory=[System.IO.Path]::GetFullPath($OutputDirectory)
$helperOutput=Join-Path $repoRoot ('artifacts\updater\'+$AppVersion+'\win-x64')
$common=@('-c','Release','-r','win-x64','--self-contained','true',('-p:Version='+$AppVersion))
if($InformationalVersion){$common+=('-p:InformationalVersion='+$InformationalVersion);$common+='-p:IncludeSourceRevisionInInformationalVersion=false'}
& dotnet publish (Join-Path $repoRoot 'tools\Updater\LocalClaudeBridge.Updater.csproj') @common '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-o' $helperOutput
if($LASTEXITCODE -ne 0){throw 'Updater helper build failed.'}
& dotnet publish (Join-Path $repoRoot 'ClaudeLocalBridge.csproj') @common '-o' $OutputDirectory
if($LASTEXITCODE -ne 0){throw 'Windows application build failed.'}
Copy-Item -LiteralPath (Join-Path $helperOutput 'LocalClaudeBridge.Updater.exe') -Destination (Join-Path $OutputDirectory 'LocalClaudeBridge.Updater.exe') -Force
$packageFolder=Split-Path -Parent $OutputDirectory
$zip=Join-Path $packageFolder 'LocalClaudeBridge-Windows-x64.zip'
Compress-Archive -Path (Join-Path $OutputDirectory '*') -DestinationPath $zip -CompressionLevel Fastest -Force
$digest=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath (Join-Path $packageFolder 'SHA256SUMS.txt') -Value ($digest+'  LocalClaudeBridge-Windows-x64.zip')
if(-not $SkipSetup){
    $metadata=Join-Path $packageFolder 'setup-package.json'
    @{Version=$AppVersion;Size=(Get-Item -LiteralPath $zip).Length;Sha256=$digest} | ConvertTo-Json | Set-Content -LiteralPath $metadata
    $setupOutput=Join-Path $repoRoot ('artifacts\setup\'+$AppVersion)
    & dotnet publish (Join-Path $repoRoot 'tools\Setup\LocalClaudeBridge.Setup.csproj') @common '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' ('-p:PackagePath='+$zip) ('-p:PackageMetadata='+$metadata) '-o' $setupOutput
    if($LASTEXITCODE -ne 0){throw 'Single-file installer build failed.'}
    $setup=Join-Path $packageFolder ('LocalClaudeBridge-Setup-'+$AppVersion+'-x64.exe')
    Copy-Item -LiteralPath (Join-Path $setupOutput 'LocalClaudeBridge-Setup.exe') -Destination $setup -Force
    Add-Content -LiteralPath (Join-Path $packageFolder 'SHA256SUMS.txt') -Value ((Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()+'  '+(Split-Path -Leaf $setup))
}
[pscustomobject]@{Version=$AppVersion;Package=$zip;Size=(Get-Item -LiteralPath $zip).Length;SHA256=$digest}|ConvertTo-Json
