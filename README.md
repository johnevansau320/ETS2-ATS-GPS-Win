# ETS2-ATS GPS for Windows

The Windows version of [ETS2-ATS GPS](https://github.com/johnevansau320/ETS2-ATS-GPS) (the route GPS for Euro Truck Simulator 2, with ProMods and other map mods, and American Truck Simulator). Licensed under GPL-3.0 (see LICENSE).

A native Windows (.NET 8 WPF) version of the ETS2 GPS Android app, and fully standalone: **one exe, nothing else to install or
start** (no Python, no companion). It has the road map with route, turn-by-turn guidance with voice, the lane panel, waypoints,
saved trips, POIs, hill / fuel / rest alerts, job auto-detect, the trip screen, and an option to avoid ferries.

It runs on the same PC as the game:
* telemetry comes straight from the TruckSim GPS Telemetry Server (default `127.0.0.1:31377`);
* the road map is built by the app itself from your game and active mods (the map builder is a C# port of the old Python
  companion and produces the identical file). The first start, or the first after a game / DLC / mod change, takes about
  10 seconds; the result is kept in `%APPDATA%\ETS2-GPS\etsmap.bin`. If the TruckSim server is not running the saved map is
  used as is;
* voice uses Windows speech on the default audio device (your headset);
* a small built-in web server (port 31378, same as the old companion) lets the Android tablet app, a browser / iPad
  (`http://<pc-ip>:31378/`) and the lane overlay window use this PC: map download, voice on the PC, lane panel, telemetry.
  If that port is taken (for example the old Python companion is still running) the server is simply off and a message says so.

Build: `build.ps1` (single self-contained exe in `publish\`, about 64 MB). Install: `install.ps1` (`-Autostart` also starts it at
login). The app icon is `Assets\app.ico`, made from `Assets\icon-source.jpg` by `make_icon.ps1`.

Setup program: `build-installer.ps1` builds the app and wraps it (with the lane overlay, if its build is next to the project) into `installer\Output\Ets2GpsSetup-<version>.exe` with Inno Setup 6. It installs for the current user (no administrator rights), adds a Start Menu shortcut and optional desktop / start-with-Windows shortcuts, and can be removed from Windows' Apps list (the uninstaller asks whether to delete your settings, trips and the map built from the game).

Stay on top of other apps: the **Pin on top** button (or the `P` key, or Settings) keeps the window above everything else; the
choice is remembered. `--topmost` forces it for one run. ETS2 must be windowed / borderless for any window to appear above the game.

Window: no title bar. Drag the green bar to move it (double-click maximises), drag the edges to resize; the whole UI scales
from 0.2x to 1x of a 1920x1080 layout. Minimise / maximise / close are the small buttons at the top right of the green bar.

Map look: steel-blue roads with outlines, lots / yards (tan) and green patches under the roads, road-number shields, company name tags, and symbols for fuel, rest areas, service centres, garages, truck dealers, weigh stations and ferry (B) / car-train (T) terminals. Shields appear from a moderate zoom, company names when zoomed in.

3D view: above 15 km/h the map tilts into perspective (back to flat below 12 km/h, or when you drag the map). Company tags show the game's logo, borders show a BORDER tag and the flag of the country entered, and big area (city) names are drawn. Logos and flags come from your own game files (built into `%APPDATA%\ETS2-GPS\etsassets.bin` with the map, never committed or released).

Opacity: Settings -> *Window* -> Opacity makes the window see-through (the game underneath shows). Going between 100 % and less takes effect after restarting the app (a see-through window renders a little slower, so it is only used when the saved opacity is below 100 %).

Lane overlay: the app starts the stand-alone lane overlay window (`Ets2LaneOverlay.exe`) when it starts, if it is not already running (Settings -> *Lane overlay window* turns this off, sets the path, or starts it now). It is looked for in the configured path, `%LOCALAPPDATA%\Programs\ETS2 Lane Overlay`, next to the app, and in the project's `overlay-dotnet\publish` folder; `install.ps1` installs it too when that build is next to the project.

Trip planner: **Plan** (left menu, or *Plan a trip…* in the Trips list) plans a trip without the game running and without any job: choose a start, add stops, choose the destination (city search; while previewing, right-click the map to use any spot), then **Preview on map** (route, length and time, zoomable all the way out), **Save trip** and later **Drive this trip**. A saved trip remembers its start; *Drive this trip* first leads you to the start (unless you are already near it) and then through the stops. Trips can be reopened (**View / edit the plan**), renamed and deleted from the Trips list.

Share trips: *Trips* -> *Import / export…* (and *Export this trip…* on a single trip) writes trips to a `.ets2trip` file (plain JSON, the same in the Windows app, the web version and the Android app) and reads one back in. Duplicates are skipped, a name that is already used gets a `(2)`, and invalid trips or ones outside the loaded map are skipped.

Keys: `+` / `-` zoom (up to 10x), `T` trip screen, `P` stay on top, `Esc` recentre / close trip screen, `F11` maximise.
Mouse: drag the map to look around (Recentre returns), wheel to zoom, double-click resets zoom, right-click a point for
Navigate here / Add as waypoint / Avoid road here.

Settings (the gear): route type, avoid ferries and car trains, window opacity, voice, telemetry address, built-in server on/off
and port, distance scale. `Save + rebuild map` forces a new map build.

Command line (testing): `--config file.json`, `--zoom <0.35..10>`, `--plan "Start|Stop|Destination"` (+ `--plan-save`, `--plan-dialog`, `--trips-dialog`, `--trips-export file`, `--trips-import file`), `--goto <city>`, `--trip`, `--build-map out.bin` (builds a map file and exits).

## New in 1.11.1

* **Rest areas**: the parking places that the game marks on its own map with a parking trigger are now on the map too (most rest areas in ATS and many in ETS2 were missing). Both maps are rebuilt once automatically.
* **Map mods with a `base` folder** (for example ATS Expansion) are read completely, so their roads show on the map.
* **Voice control**: *minimize the window* and *restore the window* (or *show the map*) minimize the GPS window or bring it back without taking the keyboard from the game. A bare *no* is no longer a command, so it is not picked up by mistake.

## New in 1.11.0

* **American Truck Simulator.** The app now works with ATS as well as ETS2 (it was renamed *ETS2-ATS GPS*). It sees which game is running and switches by itself: it builds the ATS map from your ATS install and mods (`atsmap.bin`, kept next to the ETS2 map), and in ATS shows mph, miles, gallons and US-style speed-limit signs and road shields (Interstate, US and state routes), with the game clock at 1:20. Saved trips, avoided roads and the distance scale are kept separately for each game.
* **Gravel, dirt and farm roads** that the game's GPS uses (common in rural America) are now on the map and can be routed over, as slow roads.
* **Company yards** are drawn with the same shapes as in the game's own map (lots, buildings, green areas).
* **Smoother route line**: the small sideways steps where the game's road pieces join, and inside junctions, are smoothed out.
* **Steam Cloud profiles**: the game's GPS destination and job are also read from saves that Steam keeps in the cloud (`Steam\userdata\...`).
* **More voices**: the modern Windows voices (for example the male *Microsoft George*) can be chosen in Settings > Voice, as well as voices from the **Voicebox** app when it runs on this PC (George speaks instead whenever Voicebox does not answer in time).
* **Voice control** listens to the microphone the way calls do (Windows' communications processing) and sets the level automatically, so quiet or noisy headset and wireless microphones work much better. The app never listens to itself while it speaks. *Not now* also understands *no thanks*, *maybe later*, *not this time* and more. What was heard is written to `%APPDATA%\ETS2-GPS\voice_control.log` (commands only, no audio) to help with microphone problems.
* The GitHub repositories were renamed (`ETS2-ATS-GPS-Win`, `ETS2-ATS-GPS`); the update check of older versions follows the old links automatically.

## New in 1.10.1

* **Roundabouts where traffic drives on the left** (UK, Ireland, Malta, Cyprus, Isle of Man, Channel Islands): exits are counted clockwise, so the spoken exit and the number in the turn picture match the route. They were counted anticlockwise as on the continent.
* **Voice control** works with real voices through a desk microphone: a command is accepted when the wake word and at least one word of the command were clearly heard, instead of needing a high score for the whole sentence. *Computer* works well as a wake word if *GPS* is often missed (Settings > Voice control).
* **Map badges** (fuel, rest areas, service centres ...) are 30% smaller.
* `install.ps1` closes a running ETS2 GPS before replacing it and starts it again afterwards.

## New in 1.10.0

* **Automatic quick save**: when the game's GPS gets a new destination before a job has started (a job with a pickup elsewhere, or a point set on the game's map), the app presses your own quick-save key once, while the game is in front and not paused, so the destination is read within seconds instead of at the next autosave. Settings > Game GPS destination switches it off; the game's quick-save slot is replaced.
* **The next turn after this one** is shown on the left, under the turn picture in the green bar, a little larger and in the same green.
* **Road-number shields** on the map are twice as large.

## New in 1.9.0

* **Drop-off flag**: a job route leads to the entrance of the company yard (where the game's own route reaches it) and the chequered flag stands on the **drop-off spot** inside, joined to the end of the route by a dashed line. The app tells you to follow the flag and announces *You have arrived at the drop off point* there. The drop-off spots (easy parking) come from the company yards in your game; the map is rebuilt once to add them.
* **Follows the game's GPS when there is no job**: when you set a destination on the game's map, or drive to a pickup, the app reads it from your newest save (after the game's next autosave or a quick save) and offers to navigate there, through the game's waypoints. Saves are only read, never changed. Settings > Game GPS destination switches it off.
* **Closed borders**: border crossings whose roads are all hidden in the game (the Russian borders, the Lebanon - Israel border) have gates that never open; routes now go round them.
* **Ferries**: after a ferry crossing the route starts on the road (it could start back across the sea), and ferry and car-train crossings show as dashed blue lines instead of grey roads.
* **Voice**: the new-job and game-GPS questions can be answered with *GPS, take the job* or *GPS, not now* (also *no thanks*, *maybe later*, *not this time*); *GPS, follow the game route* goes where the game's GPS is set.
* The tablet / browser version gets the drop-off spots and the game's GPS destination from this app (`/drops.json`, `/gamegps`).

## New in 1.8.0

* **Voice control**: drive the app by voice while the game has the focus. Say the wake word ("GPS") and a command, for example "GPS, nearest fuel station". Switch it on in Settings > Voice control. See *Voice control* below.
* **Job Info dashboard**: on a wide window the Job Info screen is the same dashboard as on the tablet (header strip, five headline cards for cargo, arrival, deadline, fuel for the trip and income, then five detail cards). It always fills the window without scrolling; a tall or narrow window shows the cards as a grid, also fitted to the window.
* The real time and distance under the route name are shown in yellow.

## Voice control

Settings > **Voice control (microphone)** switches it on and sets the wake word (default `GPS`). Every command starts with the wake word, so ordinary speech and the game's sound do nothing, and the app does not listen while it is speaking itself. Recognition uses the speech recognizer built into Windows (English) on your PC: no audio is recorded, stored or sent anywhere.

| Say "GPS, ..." | Does |
|---|---|
| nearest fuel station / rest area / service centre / weigh station / garage / truck dealer / ferry | Says how far and which way, and navigates there |
| take the job | Navigates to the job's delivery point |
| show job info / show the map (or back to navigation) | Opens or closes the Job Info screen |
| cancel the route / back to my route | Cancels, or returns to the route you had before a detour |
| how far is it / when do I arrive / what is next / speed limit / fuel level | Spoken answers |
| lane guidance, cruise assist, voice guidance on / off | Switches them |
| minimize the window / restore the window (or show the map) | Minimizes the GPS window, or brings it back without taking the keyboard from the game |
| help | Lists the commands |

## New in 1.7.2

* **New app icon**: the Windows app (window, taskbar, Start Menu, installer) and the web version (browser tab, and the home-screen icon when you add it on an iPad / iPhone / Android) use the ETS2 GPS truck and pin artwork.

## New in 1.7.1

* **Update check**: the app looks for a newer release on GitHub about once a day and tells you (Settings > Updates); nothing is downloaded or installed. See *Updates* below.

## Updates

The app looks for a newer release on GitHub about 20 seconds after it starts and then about once a day (one small request to the public GitHub API; nothing about you or your PC is sent beyond a normal web request). If there is one, a notice appears and **Settings > Updates** shows the version with **Open the download page** and **Skip this version**; **Check for updates now** looks immediately and **Look for new versions automatically** (on by default) switches the checks off. Nothing is downloaded or installed for you, and the app only ever opens a page on github.com. The tablet / browser version shows the same notice for the Windows app.

## New in 1.7.0

* **Roundabouts are recognised much better**: ones built from several road pieces, and ones the map builder had not flagged, now give a single "At the roundabout, take the Nth exit" at the entry instead of a "bear left / right" at the exit.
* **Exit ramps** that loop round (cloverleaf and similar) are announced by the side they leave on: "take the exit on the right / left".
* **Job routes lead you to the gate**: when the mapped road ends short of the company the app keeps the route, says once to follow the flag to the drop-off point and shows the game's remaining distance. A chequered **destination flag** marks the destination.
* **Delivered or cancelled jobs** end the route ("Job complete" / "Job cancelled").
* **Quieter guidance**: "Recalculating" is spoken once per time off the route; no recalculating inside the destination's or the pickup's yard; a prompt is not read out again after a recalculation.
* **Cruise control** presses the keys you have set in the game (read from your controls file), not just Numpad + and Numpad -.
* The lane overlay is started more reliably; the installer checks each file it copies. Works with TruckSim GPS PC Telemetry 2.0.

## New in 1.6.1

* **Lane overlay window** no longer blinks off and on: idle devices no longer blank the lanes, and the overlay keeps the last panel across a missed update (v1.6.1).

## New in 1.6.0

* **Job routes lead you to the gate**: when the mapped route ends short of the company, the app keeps the route, says once to follow the flag to the drop-off point and shows the game's remaining distance, instead of declaring the arrival. A chequered **destination flag** marks the destination.
* **Recalculating** is spoken once each time you leave the route.
* **Lane guidance** can be switched off (Settings > Show lane guidance).

## New in 1.5.2

* **Job Info screen** always counts in game kilometres, which fixes a wrong trip progress (for example 95 %) when Real distances was on (v1.5.2).

## New in 1.5.1

* **Border flags in the web version** (iPad / browser) are twice as large too, with the country name under the flag (v1.5.1).

## New in 1.5.0

* **Border flags** are twice as large and show the name of the country you are entering right under the flag (Windows app only; v1.5.0).

## New in 1.4.0

* **Flyovers and bridges**: the map now holds road heights. A road on a flyover, bridge or stacked interchange is drawn on top of the roads it crosses, with a shadow, and is raised in the 3D view while you drive; your route goes visibly over or under a crossing (the part below shows faintly through the deck). The map has to be rebuilt once (the Windows app does it itself when the game and the TruckSim server are running; on Android download the map again).
* **Pictogram badges**: fuel (lime green), rest areas, service centres, weigh stations, garages, truck dealers, ferries and car trains are drawn as a coloured badge with a picture instead of a letter.
* **Left menu auto-hides**: after a few seconds without being touched the menu fades away and a thin grip stays on the left edge; touch the grip to bring it back.
* **Real or Sim distances**: Settings > *Real distances* switches turn distances, lane guidance and trip lengths between the game's scaled (Sim) distances and real metres. The scale itself (default 19) is unchanged.
* **Cruise control follows the speed limit** (on by default; menu button *Cruise auto / Cruise off*): while cruise control is on, the Windows app taps the game's cruise faster / slower keys (Numpad + and Numpad -, the game's defaults) to keep it on the current speed limit. It only presses keys while Euro Truck Simulator 2 is the window in front. The web and Android buttons switch it on or off on the PC, so they need the Windows app running.
* **Windows only**: the map opens four zoom steps further in, and the place badges, company logos and company names are drawn larger.

## Requirements and build

* Windows 10/11, Euro Truck Simulator 2 (Steam) and the [TruckSim GPS Telemetry Server](https://github.com/TruckSim-GPS/trucksim-gps-server) running next to it.
* To build: the .NET 8 SDK, then `.\build.ps1` (single self-contained `publish\Ets2Gps.exe`) and `.\install.ps1` (Start Menu shortcut, optional autostart), or `.uild-installer.ps1` for a setup program (needs Inno Setup 6). GitHub Actions builds the exe on every push; releases carry a ready-made exe.
* The `web` folder is the browser / tablet version served by the built-in server; it is embedded in the exe.
* Map and file formats follow the MIT-licensed [ts-map](https://github.com/dariowouters/ts-map) project.
