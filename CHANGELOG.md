# Changelog

## 1.0.0
- Configurable limit for the big ammo boxes (`ammo_box_big_*`): `MaxPerBox_<caliber>` with an exact number, or `Multiplier` on the original limit (500), which is read from the boxes while the game runs.
- Hard limit: when merging boxes, ammo only moves up to the limit, and whatever is left stays in the box in your hand.
- Boxes already above 500 accept ammo again, up to the new limit.
- Applies to existing boxes and to boxes that appear later (checked every 2 s); config changes take effect immediately.
- Does not touch small boxes, the player's ammo capacity or the starting contents of boxes.
- Diagnostics tools (F9, F10, `LogActions`) are in Debug builds only; the Release build does not include them.
- Lean log: the Release build only writes the settings at startup, the original and new limit of each caliber, warnings and errors.

## 0.1.0
- Diagnostics build, used to find where the game applies the box limit.
