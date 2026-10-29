# XIVShade

A Dalamud plugin that applies ReShade-style post-processing to FINAL FANTASY XIV.

## Building

Requires XIVLauncher, FFXIV and Dalamud to be installed and run once, plus the .NET SDK.

Open `XIVShade.slnx` and build. The plugin is output to `XIVShade/bin/x64/<Configuration>/XIVShade.dll`.

## Installing

1. In-game, run `/xlsettings`, go to `Experimental` and add the full path to `XIVShade.dll` under Dev Plugin Locations.
2. Run `/xlplugins`, open `Dev Tools > Installed Dev Plugins` and enable XIVShade.
3. Use `/xivshade` to open the plugin window.
