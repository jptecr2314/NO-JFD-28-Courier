param([Parameter(Mandatory)][string]$GamePath,[Parameter(Mandatory)][string]$CompilerPath,[Parameter(Mandatory)][string]$ReleaseZip,[string]$OutputDirectory=(Join-Path $PSScriptRoot 'artifacts'))
# Rebuilds the JFD-28 Courier 0.2.0 damage runtime (CurrentAirframeDamage.dll) from this snapshot.
# The family runtime (SuperheavyGroundPhysics.dll) is frozen; it is taken from the
# release ZIP. Windows PowerShell 5.1 or PowerShell 7.
$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($ReleaseZip))
try{
  foreach($name in 'SuperheavyGroundPhysics.dll','JFD-28 Courier_0.2.0.nobp'){
    $entry=$zip.Entries|Where-Object{$_.FullName -eq ('BepInEx/plugins/br.japensou.nuclearoption.superheavygroundtest/'+$name)}
    if(!$entry){throw "$name not found in the release ZIP"}
    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,(Join-Path $OutputDirectory $name),$true)
  }
}finally{$zip.Dispose()}
& "$PSScriptRoot/tools/build-baked-airframe-data.ps1" -GamePath $GamePath -CompilerPath $CompilerPath
if(!$?){throw 'Data build failed'}
& "$PSScriptRoot/tools/build-baked-airframe-runtime.ps1" -GamePath $GamePath -CompilerPath $CompilerPath -OutputDirectory $OutputDirectory -PreserveFamilyRuntime -CourierOnly -BalancedOnly -Version '0.2.0' -BundleFileName 'JFD-28 Courier_0.2.0.nobp'
if(!$?){throw 'Runtime build failed'}
