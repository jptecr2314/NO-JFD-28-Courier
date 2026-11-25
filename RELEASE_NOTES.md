# 0.2.0 prerelease [0.34.6]

- New cargo: Spearhead MBT (71 t), offered wherever the Type-12 MBT is. With
  the bundled Retro Kit it is offered as the Spearhead MBT [Retro Kit].
- Faster cargo release: loads start sooner and move faster along the bay. In
  the air the first load leaves about 6.5 s after release instead of about
  10 s; on the ground three IFVs are out in 30 s instead of 39 s. Pressing
  release again while a load is still moving now queues the next one in the
  air too, so a full salvo comes out from repeated presses.
- Ground unloading works with heavy loads: with three Spearheads the gear sits
  lower and the runway itself counted as blocking the ramp exit.
- More weapons for the ventral bay: every bay-door air-to-ground missile or
  bomb that fits one of the bay's existing positions is offered as a full rack,
  including weapons added by other mods (PAB-125, PAB-250 and air-to-air
  missiles stay out).
- Damage looks like the game's own aircraft: holes spread with lost health,
  with dark cavities and soot, instead of whole panels stripped by a few
  machine-gun rounds; the skin no longer comes off from small-arms fire.
- Engines: weathered, irregular exhaust heat marks instead of a perfectly even
  band.
- Fixed: two interior ring segments crossed the open bomb bay as bars.
- Sounds: bullet impacts, engine-fire warnings, bomb bay doors (now from the bay,
  not from the inner engines), cargo ramp and speed brakes. The eight engines no
  longer play in phase as one stacked loop, and all of them switch correctly
  between cockpit (2D) and outside (3D) sound.
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
- Retro Kit containers pick a friendly unit within a reach that grows with release height (200 to 1,200 m around the point where the container would fall). The container still travels along the bay for a few seconds after release before it leaves the aircraft.
- The 1,500 L fuel container is too light to steer against its drag: it is never guided to a unit; it brakes straight down where it falls when that ground is safe, otherwise the release is refused.
- Not tested in this release: the Naval Supply Container Retro Kit delivery to a friendly ship, the Small Munitions Pallet and Radar Container Retro Kits, and bomb-bay munition landing. Of the containers, only the Munitions Container was airdropped at the new, faster release speed.
- Bay weapons from other mods: the bay accepts them by the same rule as the game's own weapons, but no other weapon mod was installed during testing. The new bay racks were checked by loading and by the ammunition count when fired.
- The Spearhead MBT was tested released in the air at 700 m above ground with the Retro Kit, and unloaded on the ground on a level runway; three Spearheads make the Courier about 324 t.
- Damage appearance was measured with forced health levels; it was not re-checked with live fire in flight in this release.
- The new sounds were checked by their sources, clips and 2D/3D state in the game, not judged by ear.

