# Party Sorter

A plugin for FFXIV that lets you reorder your party list by dragging party members in the in-game UI — no need to open Character Configuration.

## Installation

1. In-game, open **ESC → Dalamud Settings → Experimental → Custom Plugin Repositories** and add:
   ```
   https://aemiliusxiv.github.io/DalamudPlugins/pluginmaster.json
   ```
2. Open the Plugin Installer (`/xlplugins`) and search for **Party Sorter**.

Party Sorter is part of the [AemiliusXIV plugin repository](https://github.com/AemiliusXIV/DalamudPlugins) — visit that page for an overview of all available plugins.

## How it works

- Hold the modifier key (default **Ctrl**) and drag a party member onto another to reorder.
- Slot 1 is always locked to your character — the plugin will not allow any swap involving your own position.
- Each unique group of party members gets its own remembered order. When you meet up with the same people again the saved order is reapplied automatically — perfect for statics that switch between a cross-world party outside of an instance and a regular party inside one.

## Commands

| Command | Action |
|---|---|
| `/psorter` | Open the Party Sorter window |
| `/psorter save` | Save the current live party order |

Settings include: enable/disable toggle, drag modifier key (Ctrl / Shift / Alt), drop behaviour (swap or shift), auto-reapply, per-duty separate orders, and an on-duty-enter window prompt.

## Privacy

Party Sorter runs entirely on your machine. It reads your current party list and the orders you save, and reorders the party list locally. Nothing is collected, stored off your PC, or sent over the network, and there is no telemetry.

## License

Copyright (c) 2026 AemiliusXIV

This project is source-available. You may fork and modify it, but the source code may not be copied into other projects or plugins, in source or compiled form, without explicit written permission. Forks must preserve this license and credit the original author. See the [LICENSE](LICENSE) file for full terms.

This project is not affiliated with or endorsed by Square Enix Co., Ltd. The use of third-party tools is prohibited under the FINAL FANTASY XIV Terms of Service; use of this plugin is entirely at your own risk. FINAL FANTASY XIV is a registered trademark of Square Enix Holdings Co., Ltd.
