# JFD-28 Courier 0.1.7 [0.34.6]

Courier-only distribution for Nuclear Option 0.34.6. **Balanced is fixed.**
Bastion and the Performance/Detailed choices are not included. The Courier's
turret options, cargo/weapon selectors and liveries remain.

## What is new in 0.1.7

First release in this repository; it supersedes the 0.44.0 prerelease.


- GE-87 configuration: eight GE-87-3-A engines in paired pods with three levels
  of detail, a refined wing root, the extended unified cargo bay, a compact crew
  access and a new primary structure.
- The game's own AI pilot now handles the 400 t Courier on the ground: a taxi
  speed plan for the corners ahead, braked pivots, the runway centreline on the
  takeoff run, a rate-limited rotation and a direct runway entry instead of the
  fighter turn-around at the runway end.
- Straight exit from the HeavyHangar: the AI rolls out on the spawn heading
  until the wing has passed the doors, then joins the taxi route.
- Flaps retract progressively with speed after takeoff, so the aircraft no
  longer sinks when it cleans up.
- With the optional Cargo Retro Landing Kit installed, every cargo that has a
  Retro Kit version is offered only as the Retro Kit version.
- The four 35 mm JFD-28 CIWS turrets have their own container-style gunhouse
  (390 triangles each): the gun sits low in an open front slot with its breech
  inside the house, fires with muzzle flash and smoke, and the barrel recoils.
  Elevation runs from -5 to 80 degrees. The gunhouse and the gun cradle take
  the aircraft's livery, and the turret no longer changes to another model
  when the Chimera mod is installed.- The whole interior (flight deck, cargo bay, structure and ramp mechanisms)
  is drawn within 250 m of the camera, from any angle: through the windows,
  an open ramp or bay, and damage openings. Dark grey cabin finish.

## Requirements

- Nuclear Option 0.34.6
- BepInEx 5.4.23
- Blueprinter 2.1.1
- HeavyHangar 0.2.0

Dependencies are not bundled.

## Multiplayer

Single-player and the mission editor only. Multiplayer was not tested and is
not supported in this release.

## Install

Close the game, then import the release ZIP through your mod manager, replacing
the older Courier package (including 0.44.0, whose version number is higher). Do not keep two copies of the package active
together. For a manual installation, run Install.ps1 with Windows PowerShell 5.1
(or PowerShell 7) and -GamePath pointing to the game's directory. The installer
checks the files, backs up the previous package outside the plugins tree and
preserves configuration files and other mods.

Balanced is built in; there is no quality dropdown. F1 still contains the
remaining mod settings when BepInEx ConfigurationManager is installed
separately.

## Known limitations

- The whole interior is drawn within 250 m of the camera: measured about +0.4 ms GPU and about +1,100 draw calls on the test PC (URP High, SRP Batcher, 60 fps held). Weaker GPUs and several Couriers close together cost more.
- Heartland city airbase: the game's taxi route from the heavy hangar is long; the AI takes about 2.5 minutes to reach the takeoff run there (about 1 to 1.5 minutes at the other tested bases).
- AI ground handling was tested from the HeavyHangar at Heartland (naval, city, Boscali North) and Ignus (SE, NE) only.
- When the game's own AI flies the Courier in pattern turns it can reach about 750 km/h; contacts between fuselage parts were recorded once at that speed.
- First detonation close to the aircraft: one frame of about 27 ms on the test PC (later ones about 17 ms).
- About 813,000 triangles at full detail, interior included.
- Not tested in this release: bomb-bay munition release and landing.

## Credits

The eight engines are based on "GE-87-3-A" by MoistSquid, licensed under
Creative Commons Attribution 4.0 International
(https://creativecommons.org/licenses/by/4.0/):
https://sketchfab.com/3d-models/ge-87-3-a-84ebcc9e701249cb9a229a794a962a2c

Changes: repainted materials, mounted in paired B-52-style pods, reduced level
of detail meshes (LOD0/LOD1/LOD2), inlet liner and far-range silhouettes
rebuilt as revolved surfaces. MoistSquid does not endorse this mod.

Nuclear Option and its game resources belong to their respective owners.
BepInEx, Harmony, Blueprinter and HeavyHangar remain separate dependencies
under their own terms.

## AI disclosure

This mod was made with the help of an AI assistant (Claude, by Anthropic). AI
assistance was used for the runtime code, the build and test tools (including
the Blender and Unity processing scripts) and the documentation. The author
directed the work, chose what to keep and tested the results in the game.

See VALIDATION.json and SHA256SUMS.txt for the tested release inputs.

## Build the runtime source

Use Windows, Windows PowerShell 5.1 or PowerShell 7, a C# compiler supporting
the source's language version, and the managed assemblies and BepInEx from your
own installed game. Supply csc.exe through -CompilerPath; the compiler and the
game DLLs are not redistributed here.

```powershell
./Build.ps1 -GamePath 'C:/Games/Nuclear Option' -CompilerPath 'C:/SDK/Roslyn/csc.exe' -ReleaseZip 'C:/Downloads/JFD-28-Courier-0.1.7-Balanced.zip'
```

The wrapper rebuilds CurrentAirframeDamage.dll with COURIER_ONLY and
COURIER_BALANCED_ONLY. The family runtime (SuperheavyGroundPhysics.dll) and the
airframe data are byte-identical to 0.44.0; the family runtime is taken from the
release ZIP, and its sources (the Superheavy*.cs files here) are the ones
published with 0.44.0, which reproduced it. Building the model bundle itself
requires the separate authoring project; it is not part of this snapshot.
