param([string]$GamePath='C:\Program Files (x86)\Steam\steamapps\common\Nuclear Option',[string]$CompilerPath)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskOut=Join-Path $taskRoot 'unity\Blueprinter-Editor\Assets\SuperheavyAirframeData'
New-Item -ItemType Directory -Force -Path $taskOut | Out-Null
$taskManaged=Join-Path $GamePath 'NuclearOption_Data\Managed'
$taskArgs=@('/nologo','/target:library','/langversion:latest','/nostdlib+','/deterministic+',('/out:'+(Join-Path $taskOut 'SuperheavyAirframeData.dll')))
$taskArgs+=@('mscorlib.dll','netstandard.dll','System.dll','UnityEngine.CoreModule.dll') | ForEach-Object {'/reference:'+(Join-Path $taskManaged $_)}
$taskArgs+=Join-Path $taskRoot 'src\Superheavy\BakedAirframe.cs'
$taskCompiler=if($CompilerPath){[IO.Path]::GetFullPath($CompilerPath)}else{Join-Path $taskRoot '.deps\compiler\tasks\net472\csc.exe'}
& $taskCompiler @taskArgs
if($LASTEXITCODE -ne 0){throw 'Baked airframe data compilation failed.'}
