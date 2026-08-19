# SpiritVale Build Crafter

An in-game build planning workspace that reads the current SpiritVale client data.

## Features

- Plan class, base and job levels, all six base attributes, equipment, artifacts, refinements, cards, gems, and game-accurate skill levels with native game icons, point pools, and prerequisites.
- Inspect native item stats, effects, artifact bonuses, and imported substats directly in the selected-item workbench.
- Switch the equipment sidebar between focused Details and Replace & Tools tabs.
- Add, select, change, value-edit, and remove up to six planned equipment or artifact substats.
- Hover equipment or an artifact and press the configurable compare hotkey to inspect it beside the matching slot in the loaded build.
- Search compatible choices from the installed game data rather than a bundled frozen database.
- Save any number of builds as readable JSON files on the local machine, then reopen or duplicate them in game.
- Browse and import public builds from `spiritvalers.com` as editable local copies. Opening the community workspace and every debounced search issue a fresh server query instead of filtering only previously downloaded rows.
- Optional Item Encyclopedia handoff opens the selected item directly on its encyclopedia page.
- Keyboard support for `Escape`, `Ctrl+S`, `Ctrl+F`, and the default `Left Alt+C` item comparison shortcut, plus configurable hotkeys and UI scaling.

## Install

1. Install BepInEx 6 and SpiritVale Mod Configuration Menu.
2. Copy `NevarisBuildCrafter.dll` into `SpiritVale\BepInEx\plugins\BuildCrafter\`.
3. Optionally install `NevarisItemEncyclopedia.dll` to enable the item information button.
4. Enter the game world and press `F8`.

Local builds are stored under `BepInEx\config\BuildCrafter\builds\`. Community imports are not saved until you press **Save Local**.

## Network behavior

Community access is read-only and can be disabled in Mod Configuration Menu. Build Crafter requests public gallery rows from `https://spiritvalers.com/api/builds` and a selected public document from `https://spiritvalers.com/rest/v1/builds`. It does not request account credentials and does not upload builds.

## Build

```powershell
dotnet build .\BuildCrafter.csproj -c Release
```

The compiled plugin is written to `bin\Release\net6.0\NevarisBuildCrafter.dll`.
