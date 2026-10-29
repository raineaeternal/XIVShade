# XIVShade

A Dalamud plugin that runs ReShade-style effects (`.fx` files and ReShade presets) on FINAL FANTASY XIV, without
injecting ReShade itself.

## Using it

1. Get a ReShade shader repository, for example [reshade-shaders](https://github.com/crosire/reshade-shaders), or any
   collection of `.fx` files with their `.fxh` headers and textures.
2. Open the plugin window with `/xivshade`. On the **Folder** tab, point it at a folder that contains `Shaders` and
   `Textures` directories (or copy the files into the default folder shown there) and press **Rescan**.
3. On the **Effects** tab, tick the techniques you want. Their settings appear on the **Settings** tab; right-click a
   setting to reset it.

Techniques run in the order you enabled them. The preset is saved as `XIVShade.ini` in the plugin's config folder in
ReShade's format (`Techniques=`, `PreprocessorDefinitions=`, and one `[File.fx]` section per effect).

**Include UI** moves the effects from the 3D scene to the final presented image, so the HUD is processed as well.
Depth (`DEPTH`/`ReShade::DepthBuffer`) is only available when the UI is not included.

## How it works

The effect front end is written in C#: a C preprocessor, then a translator from ReShade FX to HLSL that handles
`texture`, `sampler`, `uniform`, `technique` and `namespace` declarations. The result is compiled with `D3DCompile`.
Effects run from a render command that the plugin inserts into the game's own command buffer, after the game's
post-effect chain and before the UI, so they execute on the render thread alongside other plugins such as UpscaleBuddy.

Supported: 2D textures and render targets (with mip generation), sampler states, uniforms of scalar and vector types
with the common UI annotations, multi-pass techniques with render targets and blending, `COLOR` and `DEPTH` textures,
textures loaded from image files, and the `timer`, `frametime`, `framecount`, `random` and `date` uniform sources.

Not supported: 1D and 3D textures, storage textures and compute passes, uniform arrays and matrix uniforms, and the
mouse, key and overlay uniform sources (they read as zero).

## Building

Requires XIVLauncher, FFXIV and Dalamud to be installed and run once, plus the .NET SDK.

Open `XIVShade.slnx` and build. The plugin is output to `XIVShade/bin/x64/<Configuration>/XIVShade.dll`.

## Installing

1. In-game, run `/xlsettings`, go to `Experimental` and add the full path to `XIVShade.dll` under Dev Plugin Locations.
2. Run `/xlplugins`, open `Dev Tools > Installed Dev Plugins` and enable XIVShade.
3. Use `/xivshade` to open the plugin window.
