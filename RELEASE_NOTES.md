## JFD-28 Courier 0.1.7 — Balanced (prerelease)

The JFD-28 Courier is a 400-tonne superheavy transport for Nuclear Option, built with Blueprinter. Eight GE-87 turbofans in paired pods lift a long cargo bay that can carry and airdrop ground vehicles and containers. A ventral bomb bay and four 35 mm CIWS turrets give it teeth.

### What it does

- **Flies and handles like something this big.** It has its own flight model and landing-gear physics. The flaps retract gradually after takeoff, so the aircraft does not sink when it cleans up.
- **The game's AI can fly it.** The AI pilot taxis it out of the HeavyHangar, follows the route at a sensible speed, lines up on the runway and takes off.
- **Cargo.** The bay holds vehicles and containers and airdrops them in flight. With the optional Cargo Retro Landing Kit installed, the menu lists only the Retro Kit version of each cargo that has one.
- **Defence.** Four 35 mm turrets, two dorsal and two ventral, use a container-style gunhouse made for this aircraft. They fire with muzzle flash, smoke and barrel recoil, and take the aircraft's livery. Two 20 mm flank turrets are optional. In testing the 35 mm turrets shot down 8 of 8 missiles fired from every direction.
- **A full interior.** The flight deck, cargo bay and ramp mechanisms are modelled. You can see them through the windows, an open ramp or bay, or battle damage, up to 250 m away.
- **Damage.** Wings, engines, ramp and landing gear can be damaged and come off, piece by piece.
- Liveries, cargo and weapon selectors, and a fixed Balanced detail level.

### Requirements

- Nuclear Option 0.34.6
- BepInEx 5.4.23
- Blueprinter 2.1.1
- HeavyHangar 0.2.0 (Blueprinter addon)

Dependencies are not bundled. Single-player and the mission editor only; multiplayer is not supported.

### Install

Download **JFD-28-Courier-0.1.7-Balanced.zip**. Close the game, then import the ZIP through your mod manager, or run `Install.ps1` as described in the README inside the ZIP. Keep only one Courier package active. The runtime-source ZIP is optional: it contains buildable runtime sources, not the Blender/Unity authoring project.

### Known limitations

- The detailed interior costs some GPU time. Weaker graphics cards and several Couriers close together will feel it.
- At the Heartland city airbase the game's taxi route from the heavy hangar is long. The AI needs about 2.5 minutes to reach the runway there.
- Landing and bomb-bay weapon release have not been tested yet.
- This is a prerelease, tested on one PC in single-player.

### Credits

The engines are based on "GE-87-3-A" by MoistSquid ([CC BY 4.0](https://creativecommons.org/licenses/by/4.0/)), modified. MoistSquid does not endorse this mod. Made with the help of an AI assistant (Claude, by Anthropic); see the README for details.

### SHA-256

```text
3E690DB6266EBD8B65AA63CA81356DCC67D74A3B6A6182B7D07B34F2B9E2EBD7  JFD-28-Courier-0.1.7-Balanced.zip
DA5B5397CB40679CF2E7ADE451C2FCF6B2B23205135D568CADF532E206DE0B5B  JFD-28-Courier-0.1.7-Balanced-runtime-source.zip
```
