# Ammo Box Capacity 1.0.0

A BepInEx 5 mod for **Apocalypter** that lets you choose how much ammo fits in the game's original **big ammo boxes** (`ammo_box_big_*`), with a **hard limit** per caliber.

## How the game works without the mod

- A big box only accepts another box (hold a box in your hand and press **F**, "Transfer Ammo") while it holds **499 rounds or fewer**.
- When it accepts, it takes **the whole box in your hand**, with nothing cut off. That is why some boxes end up a bit above 500 (506, 548, 654...).
- At 500 or more the box is "full" and accepts nothing until it drops below 500.
- The limit is **500 for every caliber** and comes from the boxes themselves (the `AmmoStorage` FSM). The mod reads this value from the game while it runs.

## What the mod does

- Replaces the big boxes' limit of 500 with the value you choose.
- **Hard limit**: when merging boxes, ammo only moves up to the limit. **Whatever is left stays in the box in your hand**, which does not disappear. If the box in your hand is emptied, it disappears as in the normal game.
- Boxes already above 500 (548, for example) **accept ammo again**, up to the new limit.
- Works on boxes that already exist and on boxes that appear later (a light check every 2 seconds).
- **Does not touch**:
  - small boxes (`ammo_box_22`, `ammo_box_9mm`, etc.);
  - the player's ammo capacity (that is what the *Configurable Capacity* mod is for);
  - the starting contents of boxes found in the world;
  - boxes already above the new limit: they keep their ammo, they just do not accept more.

## Installation

1. You need **BepInEx 5** installed in the game.
2. Download `AmmoBoxCapacity-<version>.zip` from [Releases](https://github.com/bgelatti/AmmoBoxCapacity/releases) and extract the `AmmoBoxCapacity` folder into `Apocalypter\BepInEx\plugins\`.
3. Start the game once to create the config file.

## Configuration

File `BepInEx\config\com.gelatti.apocalypter.ammoboxcapacity.cfg` (also shown in **Apocasetter**):

| Option | Default | What it does |
|---|---|---|
| `[General] Enabled` | `true` | Off = the game's original limit (500). |
| `[General] Multiplier` | `1` | Multiplies the original limit (500) for every caliber set to 0. E.g. `4` = 2000. |
| `[General] MaxPerBox_22` | `0` | Exact number for the big .22 box. `0` = original x Multiplier. |
| `[General] MaxPerBox_9mm` | `0` | Same for 9mm. |
| `[General] MaxPerBox_556mm` | `0` | Same for 5.56mm. |
| `[General] MaxPerBox_762mm` | `0` | Same for 7.62mm. |
| `[General] MaxPerBox_3006` | `0` | Same for .30-06. |
| `[General] MaxPerBox_12gauge` | `0` | Same for 12 gauge. |
| `[General] MaxPerBox_20gauge` | `0` | Same for 20 gauge. |
| `[General] MaxPerBox_arrow` | `0` | Same for arrows. |

Example: `MaxPerBox_556mm = 3000` lets the big 5.56 box hold up to 3000, and the other calibers follow `Multiplier`.

Changes take effect immediately, without a restart. Only big boxes for 12 gauge, 5.56, 7.62 and .30-06 have been seen so far. The other entries only matter if the game has a big box for that caliber.

At startup the mod writes the settings for each caliber to `LogOutput.log`. When it finds the first box of each caliber, it writes the **original limit read from the game** and the **new limit**, for example `[box] 556mm: original limit 500, new limit 3000`.

## Warning: back up your save

Box ammo is saved with the game. **If you remove the mod**, boxes above 500 **keep their ammo**, but they are "full" and accept nothing until they drop below 500. Still, **back up your save** before installing or removing the mod.

## Building

```
dotnet build -c Release
```

The Release build fills `release\AmmoBoxCapacity\` with the DLL, this README, the LICENSE and the CHANGELOG, ready to copy into `BepInEx\plugins`. Use `-p:GamePath="..."` if the game is installed somewhere else. With `-p:CopyToGame=true` the DLL and the README are also copied straight into `BepInEx\plugins\AmmoBoxCapacity`.

### Test build (Debug)

```
dotnet build -c Debug -p:CopyToGame=true
```

The Debug build includes the diagnostics tools (`src/Diagnostics.cs`), which are **not part of the Release build**. They live in the `[Debug]` section of the config:

- **F9** (`DumpKey`): while looking at a box, writes all of its FSMs to the log and then logs every change to its variables.
- **F10** (`DumpGameManagerKey`): writes the FSMs of `__GameManager__`.
- `LogActions` (off): logs int/float actions of the ammo FSMs. Takes effect only after restarting the game, and slows the game down a little.

The Debug build also logs every transfer between boxes, the small boxes it skips and the new limits after each config change.
