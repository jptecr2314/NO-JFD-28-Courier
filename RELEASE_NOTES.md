# 0.1.9 prerelease [0.34.6]

- Fixed: the Courier could not spawn with NOVR installed (relocated
  Assembly-CSharp); the version check now uses the game's own file.
- All eight engines share the same nacelle and burnt-looking exhaust; the inner
  pair no longer has a longer, white-backed cowl.
- Fixed: the rear of the engines was partly see-through; the exhaust duct
  behind the cowl chevrons is now closed as a metal sleeve.
- Low-speed roll restored: the flight assist no longer cancels most of the roll
  input at approach and cargo-drop speeds.
- Spoilers and speed brakes move rigidly on their hinges, evenly on both wings:
  speed brake about 40 degrees in flight (55 on the ground), roll spoilers up
  to about 35 degrees.
- The Cargo Retro Landing Kit 0.4.0 is now bundled in the package.
- Retro Kit supply containers: after an air release they steer to the nearest
  friendly unit in reach of the release height (distance and height difference),
  a naval container to a friendly ship, or land in the nearest clear spot.
- Requirements: Nuclear Option 0.34.6, BepInEx 5.4.23, Blueprinter 2.1.1,
  HeavyHangar 0.2.0. Single-player and mission editor only.

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
