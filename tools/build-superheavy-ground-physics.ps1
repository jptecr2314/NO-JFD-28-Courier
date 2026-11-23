param(
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Nuclear Option',
    [string]$OutputDirectory = 'D:\Build\Superheavy\ground-runtime',
    [string]$Version,
    [string]$BundleFileName,
    [switch]$BakedAirframes,
    [switch]$CourierOnly,
    [string]$CompilerPath
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $root '.deps\compiler\tasks\net472\csc.exe'
if($CompilerPath){$compiler=[IO.Path]::GetFullPath($CompilerPath)}
$managed = Join-Path $GamePath 'NuclearOption_Data\Managed'
$bepin = Join-Path $GamePath 'BepInEx\core'
$sources = @('SuperheavyGroundPhysicsPlugin.cs', 'SuperheavyGroundVisuals.cs', 'SuperheavyGroundHighLift.cs',
    'SuperheavyGearPhysics.cs',
    'SuperheavyAirdrop.cs', 'SuperheavyAirdropZone.cs', 'SuperheavyAirdropEnvelope.cs', 'SuperheavyRetroBridge.cs', 'SuperheavyCargoGroundAI.cs', 'SuperheavyCargoIcons.cs',
    'SuperheavyGroundLiveries.cs', 'SuperheavyGroundCargoMotion.cs', 'SuperheavyGroundBombBay.cs',
    'SuperheavyGroundCargo.cs', 'SuperheavyCargoOrganizerMenu.cs', 'SuperheavyNativeMenuStyle.cs', 'SuperheavyCargoPacking.cs',
    'SuperheavySpatialCargoPlan.cs', 'SuperheavySpatialCargoRuntime.cs',
    'SuperheavySpatialWeaponsPlan.cs', 'SuperheavyArmamentCatalog.cs', 'SuperheavySpatialWeaponsRuntime.cs', 'SuperheavySpatialWeaponFeed.cs', 'SuperheavyWeaponsOrganizerMenu.cs',
    'SuperheavyRetractableBattery.cs', 'SuperheavyNavalTurretAdapter.cs', 'SuperheavyBayArmor.cs', 'SuperheavyPortableBundle.cs', 'SuperheavyQolStores.cs', 'SuperheavyMountedStores.cs',
    'SuperheavyGroundMfds.cs', 'SuperheavyMfdCanvas.cs', 'SuperheavyGroundControls.cs',
    'SuperheavyGroundEjection.cs', 'SuperheavyCrewAccess.cs', 'SuperheavyGroundApu.cs',
    'SuperheavyGroundFlightControls.cs', 'SuperheavyGroundDefense.cs',
    'SuperheavyFlightDynamics.cs', 'SuperheavyNativeDamage.cs', 'SuperheavyDamageRuntime.cs', 'SuperheavyDamageEvents.cs', 'SuperheavySurfaceDamage.cs', 'SurfaceDamageCut.cs', 'SuperheavyExternalCamera.cs', 'SuperheavyFamilyMenu.cs', 'SuperheavyFamilyCompatibility.cs', 'SuperheavyGunship.cs', 'SuperheavyBomberFeed.cs') |
    ForEach-Object { Join-Path $root ('src\Superheavy\' + $_) }
$sources += Join-Path $root 'src\Superheavy\SuperheavyVariantOverlayLayout.cs'
$output = Join-Path $OutputDirectory 'SuperheavyGroundPhysics.dll'
if($BakedAirframes){
    $sources=@($sources | Where-Object {(Split-Path -Leaf $_) -notin @('SuperheavyDamageRuntime.cs','SuperheavyDamageEvents.cs','SuperheavySurfaceDamage.cs','SurfaceDamageCut.cs','SuperheavyPortableBundle.cs','SuperheavyBayArmor.cs')})
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
if(!$Version){$Version=[string](Get-Content -LiteralPath (Join-Path $root 'packaging\courier-family\meta.json') -Raw|ConvertFrom-Json).artifact.version}
$bundleName=if($BundleFileName){$BundleFileName}else{'JFD-28 Courier_'+$Version+'.nobp'}
$bundle=Join-Path $OutputDirectory $bundleName
$bundleHash=if(Test-Path -LiteralPath $bundle){(Get-FileHash -LiteralPath $bundle -Algorithm SHA256).Hash}else{''}
$fingerprint=Join-Path $OutputDirectory 'SuperheavyPortableBundleFingerprint.g.cs'
$fingerprintSource='namespace Japensou.NuclearOption.Superheavy { internal static class SuperheavyPortableBundleFingerprint { internal const string FileName="'+$bundleName+'"; internal const string Sha256="'+$bundleHash+'"; } }'
[IO.File]::WriteAllText($fingerprint,$fingerprintSource,[Text.UTF8Encoding]::new($false))
$sources+=$fingerprint
$references = @(
    'mscorlib.dll', 'netstandard.dll', 'System.dll', 'System.Core.dll', 'Assembly-CSharp.dll',
    'UnityEngine.dll', 'UnityEngine.CoreModule.dll', 'UnityEngine.PhysicsModule.dll', 'UnityEngine.AnimationModule.dll', 'UnityEngine.AudioModule.dll',
    'UnityEngine.InputLegacyModule.dll', 'UnityEngine.JSONSerializeModule.dll',
    'UnityEngine.ParticleSystemModule.dll', 'UnityEngine.AssetBundleModule.dll',
    'UnityEngine.UIModule.dll', 'UnityEngine.UI.dll', 'Unity.TextMeshPro.dll', 'Mirage.dll'
) | ForEach-Object { Join-Path $managed $_ }
$references += @('BepInEx.dll', '0Harmony.dll') | ForEach-Object { Join-Path $bepin $_ }
$compilerArguments = @('/nologo', '/target:library', '/langversion:latest', '/nostdlib+', '/deterministic+', ('/out:' + $output))
$taskDefines=@();if($BakedAirframes){$taskDefines+='BAKED_AIRFRAMES'};if($CourierOnly){$taskDefines+='COURIER_ONLY'}
if($taskDefines.Count){$compilerArguments+=('/define:'+($taskDefines -join ';'))}
$compilerArguments += $references | ForEach-Object { '/reference:' + $_ }
$compilerArguments += $sources
& $compiler @compilerArguments
if ($LASTEXITCODE -ne 0) { throw "Compiler exited with $LASTEXITCODE" }
$runtimeHash=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
@{version=$version;bundleFile=$bundleName;bundleSha256=$bundleHash;runtimeSha256=$runtimeHash;
    sourceHashes=@(Get-FileHash -LiteralPath $sources -Algorithm SHA256 | ForEach-Object {@{path=$_.Path;sha256=$_.Hash}})} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'runtime-build.json') -Encoding utf8
Get-FileHash -LiteralPath $output -Algorithm SHA256
