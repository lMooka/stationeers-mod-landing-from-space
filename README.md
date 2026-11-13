# Landing From Space

A Stationeers mod: choose where you land, then fly your landing capsule down from space.

![Preview](ModPackage/About/Preview.png)

## Features

- **Choose the spot.** When your capsule spawns, the world map opens. Click where you want to land and press **Land here** (or Enter). **Default spot** lands the normal way. Works on every spawn, including respawns. On worlds with lava, lava is marked in red and the capsule never lands on it or on a slope next to it. The map also shows your last 5 landing sites and where your landers came down (saved with the world in `LandingFromSpace.xml`, per player: nobody else sees them); click one in the list to aim there again.
- **World map on M.** Outside the capsule, **M** opens the same map just to look: where you are, lava, your landing sites and landers. Nothing can be chosen there. **M** or **Esc** closes it (the key is in the options).
- **Guided descent.** The capsule starts 500 m above the point and comes down slowly, seen from an outside camera. **WASD** steers it (the capsule leans with its heat shield facing where it goes), **Shift** falls faster (speeding up and slowing down gradually), the **mouse** turns the camera. The last 25 m are the game's own landing.
- **Thrusters and re-entry.** Each thruster flame follows the lean, and a re-entry shock wave of plasma wraps the heat shield while the capsule falls fast.
- **Starting kit.** On a new world, the lander (or the brutal crate, wreckage and tanks) comes down from the sky next to the first player with the mod, as soon as they step out of the capsule. Community start conditions give each player their own lander, which follows that player's capsule. On worlds with lava it lands right next to the capsule. The lander rides down on the thrusters of its four top pods, with engine sound, until the fuel runs out: the flame sputters, the engine cuts out and it drops the last metres with a thud.

## Admin command

`respawn <player>` resets that player on their next respawn: they come back with the new player equipment on the body (full suit, helmet, tanks and tools instead of the respawn kit) and get the starting lander again (it lands next to their capsule). It only runs on the server: in the server console, or from a client as `serverrun respawn <player>`, which the game accepts only with the server's `ServerAuthSecret`. `respawn list` shows who is marked and `respawn cancel <player>` removes the mark. Marks are saved with the world, so they also work for players who are offline.

## Multiplayer

The host needs the mod. Players that also have it get the map and the guided descent; players without it land the normal way. Thruster lean and re-entry effects are only drawn for players with the mod.

## Options

`BepInEx/config/mooka.stationeers.landingfromspace.cfg`: heights, speeds, lean angle, lander radius and fall, camera and effects.

## Requirements

BepInEx + StationeersLaunchPad.

## Building

- `deploy.ps1` builds the mod (`net48`, referencing the game's `Managed` DLLs and BepInEx) and installs it in `Documents\My Games\Stationeers\mods\LandingFromSpace`. Pass `-GameDir` if the game is not in `G:\SteamLibrary\steamapps\common\Stationeers`.
- `selftest.ps1` builds with the self-test, opens a throwaway world in the game (Vulcan by default: lava and slopes make it the hardest), checks the whole landing and reinstalls the normal build. The game must be closed. `-World Lunar` and `-StartCondition Brutal` pick other worlds and start conditions.
- `make-cover.ps1` builds the Workshop images from a 1920x1080 game capture (`cover-source.png`).
