# Commodore Game Loader
A comprehensive GUI application for loading Commodore 64, VIC-20, C16 and Plus/4 games onto original hardware from your PC's sound output, with fast loading, tape control and automatic game info and artwork.

https://github.com/Nyimski/Commodore-Game-Loader/releases/tag/v1.25.3

## Screenshot

![CGL](https://nyimski.net/Images/CGL.png)


## Features

### Core Functionality
- **Three Systems**: Commodore 64, VIC-20 and C16 / C116 / Plus/4, each with its own games, pictures, info and settings
- **Game Browser** with instant search (supports multi-term filtering across title, year, publisher, genre and RAM)
- **Sortable Columns**: Title, Year, Publisher, Genre, plus RAM (VIC-20, C16) and Machine (C16 / Plus/4)
- **Filters**: Favourites, Genre, RAM size and Machine, so you only see what your setup can run
- **Picture Viewer**: Game Case, Title Screen or Game Screen
- **Game Info Panel**: year, publisher, developer, genre, players and description, editable in Edit Mode
- **Manual Viewer**: PDF, TXT, web pages and pictures, inside the app
- Supports **.tap**, **.tzx**, **.csw**, **.prg**, **.p00**, **.t64**, **.wav**, **.mp3**, **.flac**, **.ogg** and **.gz** files
- Subfolders inside the Games folder are fully supported

### Tape Control
- ▶️ **Play**: Start/Resume game playback
- ⏹️ **Stop**: Pause the tape, just like a real Datasette
- ⏏️ **Eject**: Completely end playback
- ⏪ **Rewind**: Move back 1 tape block
- ⏩ **Fast Forward**: Jump to next tape block
- 🔢 **Tape Counter**: Shows block number and percentage loaded
- 0️⃣ **Counter Reset**: Mark reference point (e.g., after loading screens)
- ↪️ **Jump**: Jumps to the point set by Counter Reset
- ⏱️ **Load Time**: Shown next to the current game before you press Play
- ⏸️ **Stop Tape Between Parts**: Optional per game, stops where the original tape motor stopped

### Fast Load
- ⚡ Built-in fast loaders for all three systems, a game that takes ten minutes can load in under a minute
- Five speeds per game: **Ultra** (default), **Hyper**, **Turbo**, **Fast** and **Off**
- Multi-part VIC-20 tapes fast load too, with the tape stopping before each part
- Border colours show each good block; **red only ever means a bad block**, just Rewind and Play
- Games with their own loaders automatically play at normal speed
- Built fast-load tapes are cached, so Play starts instantly next time
- **Short Leader** option to cut the lead-in tone and start loading sooner

### Game Info and Artwork
- 🖼️ **Download Images**: case, title and in-game screens
- 📝 **Download Game Info**: year, publisher, developer, genre, players, memory and description
- Sources: **libretro thumbnails**, **The Commodore Zone** (C64, including music and magazine reviews), **Plus/4 World** (C16 / Plus/4, including RAM), **Wikipedia** and **TheGamesDB** (optional free key)
- Only fills in what's missing, your own edits are never overwritten
- Runs in the background so you can keep using the app
- **Possible Matches** window for titles that are close but not exact

### Edit Mode
- **Edit Details**: title, RAM, year, publisher, genre and more in one window
- **Genres**: pick, create your own, or use several per game
- **Download Images / Info** for just the selected games
- **Rename**: renames the game and its pictures, info and manual together
- **Move** and **Delete** (sent to the Recycle Bin)
- **Add Manual** and **Import Manuals From Folder**
- **Import Games From Folder** and **Find Missing Games**
- **Convert to PRG**: turns TAP and audio files into PRGs, which load more reliably and are more likely to fast load
- Drag and drop games, pictures and manuals straight into the app
- Multi-select with Shift-click and Ctrl-click

### Convenience Features
- ★ **Favourites**
- **Remember Last Game**: Auto-reopens your last-played game
- **Customizable Folders**: Set paths for games, images, manuals and info per system
- **Tape Options per system**: Invert signal, Sine wave output and Short Leader
- **Built-in Help** covering connecting, loading and every feature

## Requirements
- **Windows 10/11** (64-bit)
- **.NET 8.0 Desktop Runtime**
- **Python** (embedded in distribution)
- **Microsoft Edge WebView2** (optional, for viewing PDF and web-page manuals inside the app)
- An audio-to-cassette adaptor for your Commodore (see below)

## Installation
1. Download latest release
2. Extract to preferred location
3. Run `Commodore Game Loader.exe`
4. Choose your system from the **System** menu and add your games
5. Optional: download the CGL assets for your system (pictures, game info and manuals) from [nyimski.net](https://nyimski.net/Downloads.html) and extract them into the `Commodore Game Loader` folder

## Connecting Your Commodore
The app plays the same sound a cassette makes, so you need a device that feeds audio from the PC's headphone or line output into the cassette port:
- **µCassette (uCassette)**: C64, C128 and VIC-20, and the C16 / Plus/4 with a C16 cassette converter
- **MP32C64 / DatassetteAudioAdapter**: VIC-20, C64, C128 and Plus/4
- **Cassadapt**: C64 or C16
- **A Datasette with an audio input mod**, such as the C2N Aquarius mod
- **A Datasette with a car cassette adaptor**: can work, but less reliable

The C16, C116 and Plus/4 use a round 7-pin cassette socket, so a C64-style adaptor needs a converter that plugs into the C16 and gives a standard C64 / VIC-20 cassette port.

## Usage Guide

### First-Time Setup
1. Choose your machine from the **System** menu
2. Open **Settings** (Menu → Settings) and set folders if you don't want the standard ones inside the app folder:
   - Games
   - Images
   - Manuals
   - Info
3. Set **Tape Options** for your adaptor (Invert signal, Sine wave output)
4. Optional: paste a free **TheGamesDB API key** in **Settings → Sources** (it's kept only on your PC, in `thegamesdb_key.txt`, so don't share that file)
5. Enable "Remember Last Game" if desired

### Playing Games
1. Select game from list (use search to filter)
2. On the Commodore type:
   - **C64 / VIC-20**: `LOAD` and press **RETURN** (or **SHIFT + RUN/STOP**)
   - **C16 / C116 / Plus/4**: `LOAD"",1` and press **RETURN**
3. When the screen says **PRESS PLAY ON TAPE**, click **Play** in the app
4. Most games start by themselves. If one stops at **READY**, type `RUN` and press **RETURN**
5. Use **Stop**, **Rewind**, **Fast Forward** as needed

### Edit Mode Features
1. Enable **Edit Mode** (Menu → Edit Mode → Editor On)
2. Select one or more games and right-click for options:
   - **Edit Details**, **Genre**, **RAM**
   - **Download Images** / **Download Info**
   - **Stop Tape Between Parts**
   - **Rename**, **Move**, **Delete**
   - **Add Manual**, **Convert to PRG**
3. Set the **Fast Load** speed for the selected game in the Tape Status panel
4. Drag and drop files directly onto:
   - Game list (to add games)
   - Image panel (to update pictures)
   - Info panel (to add text or manuals)

### Downloading Info and Images
1. Enable **Edit Mode**
2. Choose **Download Images**, **Download Game Info** or **Download Info and Images** for all games
3. Progress shows at the right-hand end of the menu bar
4. Check **Possible Matches** for any titles the app wasn't sure of

## Folder Layout
Each system has its own folder inside the app folder: `C64`, `VIC20` and `C16`.
- **Games**: your game files (subfolders are fine)
- **Images**: pictures in `Case`, `Title` and `Screen` subfolders
- **Manuals**: manual files
- **Info**: one text file per game
- **Settings**: favourites, fast load choices and edits for that system
- **Originals**: files moved aside by Convert to PRG
- **Cache**: built fast-load tapes (safe to delete at any time)

## File Naming Convention
All supporting files must match the game filename:
- Game: `GameName.tap` (or .prg, .t64, .wav etc.)
- Images: `Images\Case\GameName.png`, `Images\Title\GameName.png`, `Images\Screen\GameName.png`
- Manual: `GameName.pdf` (or .txt, .html, .jpg etc.)
- Info: `GameName.txt`

Side, tape, part and disk numbers and (Cheat) or (Trainer) are shown after the name in brackets, so multi-part games sort together, e.g. `Elite (Tape 1) (Side A)`.

## Technical Details
- Tape signal generated at 44.1kHz, square or sine wave
- Custom fast loaders for the C64, VIC-20 and C16 / Plus/4, sent at normal speed then running the game at up to Ultra speed
- VIC-20 RAM requirements and C16 / Plus/4 machine type detected automatically by scanning each game
- Background scanning and downloading, with results cached so they only happen once

## Troubleshooting
- **The Commodore never finds the game?** Check:
  - PC volume is high (80-100%) with sound enhancements and equalisers off
  - The cable is in the headphone or line output, not Bluetooth
  - Try **Invert signal** in Settings → Tape Options
- **?LOAD ERROR or loading stops partway?** Ensure:
  - On a VIC-20, the RAM column matches your memory cartridge
  - Try **Sine wave output** or a slightly lower volume
  - Set the game's Fast Load slower or Off
- **Red border during fast load?** A block was read wrongly, press **Rewind** then **Play**. If it's always at the same place, lower the speed for that game
- **Fast Load says Unavailable?** The game has its own loader. Try **Convert to PRG**
- **Loading finishes but nothing happens?** Type `RUN` and press **RETURN** (F6 on the C16 / Plus/4)
- **No pictures or info?** Check:
  - Edit Mode is enabled
  - Internet connection
  - The **Possible Matches** window, or rename the game
- **Edit Mode features not appearing?** Ensure:
  - Edit Mode is enabled (Menu → Edit Mode → Editor On)
  - You're right-clicking on a game in the list

## Contributing
This project welcomes contributions under GPLv3 license. Feel free to:
- Report issues or suggest features
- Submit pull requests
- Fork for your own modifications

## License
GNU General Public License v3.0 - See [LICENSE](LICENSE.txt) for details.
