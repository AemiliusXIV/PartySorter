# Party Sorter

A plugin for FFXIV that lets you reorder your party list by dragging party members in the in-game UI — no need to open Character Configuration.

Available through the [AemiliusXIV plugin repository](https://github.com/AemiliusXIV/DalamudPlugins) or directly from the [GitHub repository](https://github.com/AemiliusXIV/PartySorter).

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

## License

Copyright (c) 2026 AemiliusXIV. All rights reserved.

This project is source-available. You may fork and modify it, but the source code may not be copied into other projects or plugins without explicit written permission. Forks must preserve this license and credit the original author. See the [LICENSE](LICENSE) file for full terms.

Use of this plugin is at your own risk and is not sanctioned by Square Enix Co., Ltd.
