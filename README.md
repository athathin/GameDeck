# GameDeck

One library for the games from all your launchers (Steam, Epic, GOG, Ubisoft, Riot, Xbox, Rockstar, Battle.net, EA, itch.io, Amazon Games and
games you add yourself), with a desktop mode and a full-screen, controller-friendly **Big Picture** mode.

## What it does

**One library**
- Finds the games of every launcher above by itself, with their cover art, and notices games you install or uninstall. Add
  any other game by picking its program. DLC and soundtracks that Steam installs separately are kept apart from the games.
- Favorites, categories, a status (Playing, Completed, Up next, Dropped), a five-star rating and notes for each game. Pick
  several games (Ctrl / Shift + click) to change them all at once.
- Search, and filter by launcher, category, status, genre, rating, or smart filters: Never played, Played under 1 hour,
  Recently added.
- Tabs for All games, Favorites, Recently played, Stats (playtime, sessions, this year in games, your most played games, how
  many games each of your launchers has, the space your games take, with an Uninstall button for each) and Media (every
  game's screenshots in one place). A weekly recap of the week before, if you like.
- Each game's page, like GOG Galaxy's: the game's art behind the whole window, blurring into its colors as you scroll;
  playtime, achievements, size on disk, a calendar of when you played it, **where you left off** (GameDeck asks when you
  close a game), and **about the game** (description, developer, publisher, release date, Metacritic score) from the
  Steam, GOG, Epic and Microsoft stores, or from IGDB with your own free keys.
- Playtime is counted for the games you start from GameDeck, and the hours Steam, GOG Galaxy and itch.io counted are included.

**Starting games**
- Launch routines per game: close chosen apps first, start others, switch to the High performance power plan or another screen
  resolution and refresh rate, and undo it all when the game ends.
- Choose what happens when the game closes: back to GameDeck, or put the PC to sleep or turn it off (after a countdown you can
  cancel).
- Optional break reminders after every 1 to 3 hours of play.
- A quick launch box (Ctrl + Alt + G) to start any game from anywhere, and the last games you played in the tray icon's menu.
- Desktop shortcuts for single games, which still count playtime and run the launch routine.

**Big Picture mode** (F11, the Big Picture button, or the controller's Home button)
- The whole library full screen, made for the couch: Xbox, PlayStation and Nintendo controllers, or the keyboard and mouse.
- Its own settings, an on-screen keyboard for search and notes, and a file browser to add games and screenshots with the
  controller. A game menu to edit, rate, configure and uninstall games, and launch routines, all with the controller.
- A home row of shelves: Continue playing, Up next, Recently added, Favorites and your categories.
- **Now Playing**: press Home during a game to resume it or close it.
- **Controller shortcuts**: hold Home and press a button for volume, songs, Alt + Tab, the on-screen keyboard or a screenshot,
  also in games.
- Can be what GameDeck opens in, like a console, even straight from Windows' start-up.
- The controller's battery, Wi-Fi, Bluetooth and sound output at the top, a low-battery warning, and a screen saver against
  burn-in on OLED TVs.

**Also**
- Dark and light themes (or follow Windows), seven accent colors, three sizes for Big Picture.
- A duplicate finder, backups you can export and import, and an optional daily backup to a folder you choose (OneDrive, a USB
  drive...).
- Settings in a window of their own, laid out like Big Picture's.
- It can start with Windows and wait in the system tray until you need it; the tray icon's menu has your last games and Settings.

What changed in each version is in the [changelog](CHANGELOG.md).

## Download

Go to the [**latest release**](https://github.com/athathin/GameDeck/releases/latest) and download:

| Your Windows | File |
| --- | --- |
| 64-bit (almost every PC) | `GameDeck-win-x64.exe` |
| 32-bit | `GameDeck-win-x86.exe` |

There is nothing to install: put the file wherever you like (a folder you can write to, such as `Documents\GameDeck`)
and run it. Windows may show a SmartScreen warning the first time because the app isn't signed; choose
**More info > Run anyway**.

To remove GameDeck, use **Settings > System > Uninstall GameDeck** inside the app.

## Updates

Each time GameDeck starts it checks this page for a newer release. If there is one it downloads it, installs it and
starts again as the new version, the way Steam updates itself. You can turn this off in **Settings > System**.

That section also has a **Check for updates** button. An update is only made final once the new version has run for a short
while: a version that doesn't start is replaced by the previous one, and isn't offered again.

## Your data

Favorites, playtime, play sessions and settings are saved on your PC in `%AppData%\GameDeck`, along with the cover art and
the screenshots you add to a game. A copy of the last seven days is kept in `%AppData%\GameDeck\backups`, and GameDeck uses
it if one of the files is ever damaged. Errors are written to `%AppData%\GameDeck\logs`; when one happens GameDeck offers to
open a bug report that is already filled in (nothing is sent until you press Submit on GitHub, and your user name is removed
from it).

## Privacy

GameDeck has no account, no ads and no tracking. It only contacts:

- **github.com**, to look for and download updates (turn it off in Settings),
- **Steam, GOG, Epic and Microsoft's store**, to fetch cover art, background art and the games' details (Riot games' from
  the Epic store) and look up game names (turn it off with "Download cover art from the internet" in Settings),
- **steamgriddb.com**, only if you enter your own SteamGridDB key in Settings: the names of games that have no cover yet
  are sent there to look for one,
- **IGDB (Twitch)**, only if you enter your own Twitch app keys in Settings: the names of your games are sent there to fill
  in what the stores don't have.

Your library, playtime, screenshots and settings never leave your PC.

## Bugs and suggestions

Found a bug or have an idea? Please [open an issue](https://github.com/athathin/GameDeck/issues/new/choose) and pick
**Bug report** or **Suggestion**. For a bug, it helps to include:

- the GameDeck version (Settings shows it) and whether you use the 64-bit or the 32-bit file,
- whether it happens in desktop mode or Big Picture mode, and the controller you use,
- what you did, what happened and what you expected, with a screenshot if you can.

Please leave out personal information. You can only suggest changes: only the owner can change this repository.

## License

GameDeck is released under the [MIT License](LICENSE). The software and artwork it includes are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
