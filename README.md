# JFD-28 Courier 0.1.9 [0.34.6]

Courier-only distribution for Nuclear Option 0.34.6. **Balanced is fixed.**
Bastion and the Performance/Detailed choices are not included. The Courier's
turret options, cargo/weapon selectors and liveries remain.

## Features


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
- The Cargo Retro Landing Kit comes in the package: every cargo that has a
  Retro Kit version is offered only as the Retro Kit version.
- The four 35 mm JFD-28 CIWS turrets have their own container-style gunhouse
  (390 triangles each): the gun sits low in an open front slot with its breech
  inside the house, fires with muzzle flash and smoke, and the barrel recoils.
  Elevation runs from -5 to 80 degrees. The gunhouse and the gun cradle take
  the aircraft's livery, and the turret no longer changes to another model
  when the Chimera mod is installed.
- The whole interior (flight deck, cargo bay, structure and ramp mechanisms)
  is drawn within 250 m of the camera, from any angle: through the windows,
  an open ramp or bay, and damage openings. Dark grey cabin finish.

## Changes in 0.1.9

- Fixed: with NOVR (or another mod that relocates the game's Assembly-CSharp)
  installed, the Courier could not spawn. The game-version check now falls
  back to the game's own Managed/Assembly-CSharp.dll when the loaded assembly
  has no readable file.
- All eight GE-87 engines now share the same nacelle. The four inner engines
  used a longer, closed study cowl that hid the dark exhaust, so their backs
  looked white next to the burnt-looking outer engines.
- Fixed: the rear of every engine looked partly see-through. Behind the
  painted cowl's chevrons the exhaust duct was only drawn from the inside, so
  you saw through the nacelle; it is now closed from outside as a metal
  sleeve, and the cowl keeps your livery up to the chevrons.
- Low-speed roll: below about 360 km/h the flight assist no longer cancels most
  of the roll input, so the Courier banks again at approach and cargo-drop
  speeds instead of feeling like a stone. High-speed roll and pitch limits are
  unchanged.
- Spoilers and speed brakes: the visible panels now stay locked to their hinge
  and move together on both wings. In flight the speed brake opens to about 40
  degrees (55 on the ground) at a steady hinge rate, and roll spoilers rise up
  to about 35 degrees on the wing going down. Before, the panels lagged, sagged
  and opened unevenly, and the speed brake stood almost upright.
- The Cargo Retro Landing Kit 0.4.0 is bundled in the package (folder
  BepInEx/plugins/Type12RetroKit). It replaces any older copy of the kit.
- Retro Kit supply containers (munitions, fuel and naval supply). After an air
  release the container steers to the nearest friendly unit it can reach from
  its release height, judging horizontal distance and height difference, and
  lands beside it; a unit several kilometres away is never chosen. A naval
  container settles on the water beside a friendly ship. With no friendly unit
  in reach, it lands in the nearest clear spot. The light fuel container cannot
  steer against its drag, so it brakes straight down where it falls when that
  ground is safe. The cockpit notice says where it is going. Remember that the
  container still rolls along the bay for about ten seconds after release.

## Changes in 0.1.8

- Pitch handling for a heavy transport: with full back stick the flight
  assist now holds about 2.5 g, about 5 degrees per second of pitch rate and
  about 30 degrees of nose-up attitude, so the Courier no longer loops. Nose-down
  inputs and flying with the flight assist off are unchanged.
- Roll spoilers open visibly on their hinges (about 25 degrees on the wing going
  down) instead of rising like a flat step; a strip of wing-root fairing no
  longer lifts with the inboard spoiler. Roll forces are unchanged.
- Cargo still mounted when the Courier is destroyed now comes out of the wreck
  as destroyed vehicles, as on other cargo aircraft.
- All in-game airdrop and gun-cradle notices are in English.
- Gun interlock: before a defensive gun fires, a thin check runs along each
  barrel; if it meets any part of the same Courier, that gun holds fire until
  the barrel clears. The guns can no longer shoot their own aircraft. Other
  aircraft and units are not affected.
- Ground unloading: each vehicle waits until it has settled off the ramp, then
  drives about 85 m aft of the Courier (alternating left and right) before its
  normal AI takes over and heads for its objectives, so vehicles no longer turn
  back into the aircraft. Pressing release again while a vehicle is still on
  the ramp no longer asks you to clear the area: the release is queued and the
  next vehicle comes out as soon as the exit lane is free.
- Cargo ramp: the slatted face of the folding ramp toe now flips out together
  with the toe; before, it stayed lying on top of the open ramp.
- Cargo layouts with more than two loads: cargo added by other mods (for example
  the CRV-L Serval vehicles) and its Retro Kit version could be chosen in the
  cargo organizer but was refused when the Courier spawned, so only the game's
  own loads appeared. The Courier now accepts every load the organizer offers.
- Cargo is released only with the normal weapon release (select Cargo and
  fire), on the ground or in the air. The separate Ctrl+F10 unload key was
  removed.
- The 20 mm flank guns now show a muzzle flash and smoke on every burst,
  using the effects of the Courier's 35 mm guns.

## Requirements

- Nuclear Option 0.34.6
- BepInEx 5.4.23
- Blueprinter 2.1.1
- HeavyHangar 0.2.0

Dependencies are not bundled. The Cargo Retro Landing Kit is part of this
package.

## Multiplayer

Single-player and the mission editor only. Multiplayer was not tested and is
not supported in this release.

## Install

Close the game, then import the release ZIP through your mod manager, replacing
any older Courier package. Do not keep two copies of the package active
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
- The gun interlock holds fire while a barrel points at the Courier itself, so a turret can stay silent against targets hidden behind the tail, wings or engines from where it sits.
- Retro Kit containers pick a friendly unit within a reach that grows with release height (200 to 1,200 m around the point where the container would fall). Remember the container rolls along the bay for about ten seconds after release before it leaves the aircraft.
- The 1,500 L fuel container is too light to steer against its drag: it is never guided to a unit; it brakes straight down where it falls when that ground is safe, otherwise the release is refused.
- Not tested in this release: the Naval Supply Container Retro Kit delivery to a friendly ship, the Small Munitions Pallet and Radar Container Retro Kits, and bomb-bay munition release and landing.

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
./Build.ps1 -GamePath 'C:/Games/Nuclear Option' -CompilerPath 'C:/SDK/Roslyn/csc.exe' -ReleaseZip 'C:/Downloads/JFD-28-Courier-0.1.8-Balanced.zip'
```

The wrapper rebuilds CurrentAirframeDamage.dll with COURIER_ONLY and
COURIER_BALANCED_ONLY. The family runtime (SuperheavyGroundPhysics.dll) and the
airframe data are frozen; the family runtime is taken from the release ZIP, and
its sources (the Superheavy*.cs files here) are the ones that reproduced it. Building the model bundle itself
requires the separate authoring project; it is not part of this snapshot.
