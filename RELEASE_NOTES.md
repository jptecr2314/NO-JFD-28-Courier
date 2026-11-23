# 0.1.7 prerelease [0.34.6]

First release in this repository; supersedes 0.44.0.

- GE-87 configuration: eight GE-87-3-A engines (paired pods, LOD0/1/2), refined
  wing root, extended unified cargo bay, compact crew access, new primary
  structure.
- AI ground handling for the 400 t Courier: taxi speed plan, braked pivots,
  runway centreline, rate-limited rotation and direct runway entry.
- Straight exit from the HeavyHangar: the AI rolls out on the spawn heading
  until the wing has passed the doors, then joins the taxi route.
- Progressive flap retraction after takeoff (no sink on clean-up).
- With the optional Cargo Retro Landing Kit, cargo with a Retro Kit version is offered only as Retro Kit.
- Flank turrets listed as "Flank Defense" / "20 mm JFD-28 CIWS", like the 35 mm JFD-28 CIWS turrets.
- 35 mm turrets: own container-style gunhouse (390 triangles), muzzle flash, smoke and barrel recoil, -5 to 80 degree elevation, livery on the gunhouse and cradle; no model swap with the Chimera mod.
- The whole interior (flight deck, cargo bay, structure and ramp mechanisms)
  is drawn within 250 m of the camera, from any angle: through the windows,
  an open ramp or bay, and damage openings. Dark grey cabin finish.
- Requirements: Nuclear Option 0.34.6, BepInEx 5.4.23, Blueprinter 2.1.1,
  HeavyHangar 0.2.0. Single-player and mission editor only.

## Known limitations

- The whole interior is drawn within 250 m of the camera: measured about +0.4 ms GPU and about +1,100 draw calls on the test PC (URP High, SRP Batcher, 60 fps held). Weaker GPUs and several Couriers close together cost more.
- Heartland city airbase: the game's taxi route from the heavy hangar is long; the AI takes about 2.5 minutes to reach the takeoff run there (about 1 to 1.5 minutes at the other tested bases).
- AI ground handling was tested from the HeavyHangar at Heartland (naval, city, Boscali North) and Ignus (SE, NE) only.
- When the game's own AI flies the Courier in pattern turns it can reach about 750 km/h; contacts between fuselage parts were recorded once at that speed.
- First detonation close to the aircraft: one frame of about 27 ms on the test PC (later ones about 17 ms).
- About 813,000 triangles at full detail, interior included.
- Not tested in this release: bomb-bay munition release and landing.
