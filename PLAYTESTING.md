# Cubuild Playtest Build

Thanks for testing! This is a single self-contained Windows x64 executable. Run **Cubuild.exe**; no installer, DLLs, or extra folders are required. On launch it unpacks its bundled runtime/content to a temporary folder automatically.

Windows SmartScreen may warn that the app is unsigned; choose “More info” → “Run anyway” if you trust the sender.

## What to test

1. Start a Creative world and play normally for a while.
2. Open **Custom Blocks** from the title screen.
3. Create a block, draw its six faces, and give it a name.
4. Place it in the world, save, quit to the title, and load the world again.
5. Optional: send the world save to someone else (or load it on a clean install) and confirm the custom block comes back correctly.

## Controls

- **W / A / S / D** — move.
- **Mouse** — look; **left click** mine/attack; **right click** place/use/interact.
- **Mouse wheel** — change hotbar slot.
- **1–9, 0** — select hotbar slots 1–10.
- **Space** — jump / swim up / ascend in creative flight.
- **Double-tap Space** — toggle creative flight.
- **Left Shift** — descend while flying / swim down.
- **Left or Right Ctrl** — faster creative flight (no ground sprint).
- **E** — inventory; **Q** — drop one item.
- **Esc** — close a menu, pause, or resume.
- **F5** — third-person view; **F11** — fullscreen.
- **F** — cycle render distance; **T** — advance time.

These bindings are intentionally disabled in this build: **F2, Enter, F3, F6, F7, F8, B, G, H, P, J, O, L, and /**.
Chat and chat commands are therefore unavailable. Multiplayer and Survival are also disabled.

## Custom blocks

- Only the display name is entered; the internal identity is generated automatically.
- The six face textures are edited separately; the cube preview can be rotated with the middle mouse button.
- Click a block's icon in the Custom Blocks list to edit it.
- Blocks are limited to 126 custom entries.
- Duplicate display names are allowed; use the icon preview to tell them apart.
- Custom blocks are cube-shaped only in this build.

## Where data is stored

- World saves: `%LOCALAPPDATA%\Cubuild\saves`
  Existing worlds from an older build's `saves` folder are copied there automatically on first run.
- Custom block definitions and textures:
  `%LOCALAPPDATA%\Cubuild\CustomContent\custom_blocks.json`
- Error logs: `app_error.log` and `save_error.log` next to the executable or in the current working directory.

World saves use the v24 format. Loading a world automatically imports any custom blocks embedded in that save that are not installed yet. Share a world by sending its `.cubuild` file; the custom block definitions travel inside it.

To share your custom block pack separately, copy `custom_blocks.json` from the path above. Keep the file intact; it contains the permanent IDs and texture data.

## Reporting bugs

Please include:

- What you did and what you expected.
- The exact error or a screenshot.
- The relevant save file if the problem is world-specific.
- `app_error.log` / `save_error.log` if present.

## Known limitations

- Multiplayer and Survival are unavailable.
- Chat and commands are unavailable.
- Custom blocks cannot be deleted or reordered yet.
- Custom blocks are full cubes with six textures; no slabs, stairs, fluids, or custom shapes.
- Networking does not yet translate custom block packs between clients.
