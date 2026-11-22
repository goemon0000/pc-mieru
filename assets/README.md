# App icon

- `PCMieru.ico`: the icon of every exe (`ApplicationIcon` in each `src/*/*.csproj`) and of the main window (embedded in
  PCMieru.exe, loaded by `Shell/AppIcon.cs`). It has 16, 20, 24, 32, 40, 48, 64, 128 and 256 px: 32-bit BMP frames up to
  128 px, PNG at 256 px.
- `PCMieru-icon.png`: the 1024 px source of the icon with a transparent background, for a page or a store listing.

The design is a chip with an eye on it (the hardware details, and "mieru" = can be seen). It was drawn with OpenAI image
generation through Codex (2026-11-12) from a prompt that asked for an original design: no text, no company logos, and
nothing like the Windows logo or the Task Manager icon. It is covered by the repository's MIT license.

To make a new `.ico` from a new PNG: crop to the tile, scale it to 256 px, and write the sizes above. Keep the frames below
256 px in BMP format, because System.Drawing.Icon (the window icon) skips PNG frames. `AppIconTests` checks the sizes.
