param([string]$GamePath='C:\Program Files (x86)\Steam\steamapps\common\Nuclear Option',
      [string]$OutputDirectory='D:\Build\Superheavy\blueprinter-owned\candidate\BepInEx\plugins\CourierBastion',
      [switch]$PreserveFamilyRuntime,[switch]$CourierOnly,[switch]$BalancedOnly,
      [string]$Version='0.43.0',[string]$BundleFileName='Courier Bastion_0.43.0.nobp',[string]$CompilerPath)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
if($PreserveFamilyRuntime){if(!(Test-Path -LiteralPath (Join-Path $OutputDirectory 'SuperheavyGroundPhysics.dll'))){throw 'Frozen family runtime missing.'}}
else{& (Join-Path $PSScriptRoot 'build-superheavy-ground-physics.ps1') -GamePath $GamePath -OutputDirectory $OutputDirectory -BakedAirframes -CourierOnly:$CourierOnly -Version $Version -BundleFileName $BundleFileName -CompilerPath $CompilerPath}
$taskManaged=Join-Path $GamePath 'NuclearOption_Data\Managed'
$taskData=Join-Path $taskRoot 'unity\Blueprinter-Editor\Assets\SuperheavyAirframeData\SuperheavyAirframeData.dll'
Copy-Item -LiteralPath $taskData -Destination $OutputDirectory -Force
$taskGameHash=(Get-FileHash -LiteralPath (Join-Path $taskManaged 'Assembly-CSharp.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
$taskFamilyHash=(Get-FileHash -LiteralPath (Join-Path $OutputDirectory 'SuperheavyGroundPhysics.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
$taskContract=Join-Path $OutputDirectory 'CurrentDamageContract.g.cs'
[IO.File]::WriteAllText($taskContract,('namespace Japensou.NuclearOption.CurrentDamage { internal static class CurrentDamageContract { internal const string GameAssemblySha256="'+$taskGameHash+'"; internal const string FamilyRuntimeSha256="'+$taskFamilyHash+'"; } }'),[Text.UTF8Encoding]::new($false))
$taskRefs=@('mscorlib.dll','netstandard.dll','System.dll','System.Core.dll','Assembly-CSharp.dll','UnityEngine.dll','UnityEngine.CoreModule.dll','UnityEngine.PhysicsModule.dll','UnityEngine.AnimationModule.dll','UnityEngine.ParticleSystemModule.dll','Mirage.dll','Newtonsoft.Json.dll') | ForEach-Object {Join-Path $taskManaged $_}
$taskRefs+=@('BepInEx.dll','0Harmony.dll') | ForEach-Object {Join-Path $GamePath ('BepInEx\core\'+$_)}
$taskRefs+=Join-Path $OutputDirectory 'SuperheavyAirframeData.dll'
$taskArgs=@('/nologo','/target:library','/langversion:latest','/nostdlib+','/deterministic+',('/out:'+(Join-Path $OutputDirectory 'CurrentAirframeDamage.dll')))
$taskDefines=@();if($BalancedOnly){$taskDefines+='COURIER_BALANCED_ONLY'};if($CourierOnly){$taskDefines+='COURIER_ONLY'}
if($taskDefines.Count){$taskArgs+=('/define:'+($taskDefines -join ';'))}
$taskArgs+=$taskRefs | ForEach-Object {'/reference:'+$_}
$taskArgs+=@('CurrentAirframeDamage.cs','NativePartitionIntegration.cs','FreshDamageProfiles.cs','FreshRuptureKernel.cs','FreshStructureDamage.cs','CourierDamageVisuals.cs','CourierPresentation.cs','CourierAssemblyOwnership.cs','CurrentAirframeCompatibility.cs','CourierBlownFlaps.cs','CourierClimbOutFlaps.cs','CourierGroundPilot.cs','CourierRetroOnlyCargo.cs','CourierTurretNames.cs','CourierCiwsArt.cs','CourierDefenseCoverage.cs','CourierHandling.cs','CourierCargoRelease.cs','CourierEnglishNotices.cs','CourierFireInterlock.cs','CourierGroundUnload.cs','CourierRampToe.cs','CourierGunSmoke.cs','CourierSpoilers.cs','CourierContainerDrop.cs','CourierEngineUniform.cs') | ForEach-Object {Join-Path $taskRoot ('src\Superheavy\'+$_)}
$taskArgs+=$taskContract
$taskCompiler=if($CompilerPath){[IO.Path]::GetFullPath($CompilerPath)}else{Join-Path $taskRoot '.deps\compiler\tasks\net472\csc.exe'}
& $taskCompiler @taskArgs
if($LASTEXITCODE -ne 0){throw 'Baked runtime compilation failed.'}
Get-ChildItem -LiteralPath $OutputDirectory -Filter '*.dll' | Get-FileHash
