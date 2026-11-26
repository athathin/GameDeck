# Changelog

What changed in each version of GameDeck. GameDeck installs new versions by itself when it starts (see the
[README](README.md#updates)); the newest version is always on the [releases page](https://github.com/athathin/GameDeck/releases/latest).

## 1.4.0 (26 Nov 2026)

New:
- **Where you left off**: after a session, GameDeck asks; the note shows on the game's page and in Continue playing.
- **Game pages**: achievements, size on disk, a play-history calendar, and two columns on wide screens.
- **Stats**: This year in games, and the space your games take. A Largest first sort.
- **Playtime** from Steam, GOG Galaxy and itch.io, and **genres** (with a Genre filter).
- **When the game closes**: go back to GameDeck, sleep or turn off the PC, after a countdown you can cancel.
- **Launch routines** can set the screen's resolution and refresh rate, and can be set up with the controller in Big Picture.
- **DLC and soundtracks** that Steam installs separately stay out of the library (a filter shows them); a game's page lists its own.
- **Big Picture**: a home row of shelves, a status bar (battery, Wi-Fi, Bluetooth, sound), a screen saver, LT / RT to scroll.
- Break reminders, a low-battery warning, a daily backup to a folder you choose, seven accent colors, a Media filter by game.

Changed and fixed:
- Cover art for Riot games. Xbox games such as the Minecraft Launcher start again.
- The same on / off switches everywhere in Big Picture.
- Big Picture opens on the screen the desktop window is on.
- Newer controller support (SDL 2.31): no more false "empty" batteries.
- A daily check for updates while GameDeck runs.
- The desktop Media tab fills its rows.

## 1.3.0 (26 Nov 2026)

New:
- **Media tab** in both modes: every game's screenshots in one place, newest first. Open one, show it in its folder, or go to its game.
- **Now Playing**: during a game started from GameDeck, the controller's Home button brings up Big Picture with Resume and Close game. A banner shows the running game, and its page says Resume instead of Play. A game that doesn't close when asked gets a choice: go back to it, close it anyway, or keep waiting.
- **Smart filters**: Never played, Played under 1 hour and Recently added.
- **Desktop shortcuts** for games (from the game's menu). They start the game through GameDeck, so playtime and launch routines still work.
- **Big Picture settings** redone as a full screen with sections (General, Library, Appearance, Controller, Sound, Screenshots, Quick launch, System), switches and buttons.
- **Open GameDeck in Big Picture mode**, like a console. With "Launch when Windows starts" the PC starts straight into Big Picture.
- **Screenshots with the controller**: add pictures to a game's page and choose the screenshots folder without a mouse.
- **Show in folder** for screenshots.

Changed and fixed:
- Big Picture always opens on the main display.
- A game's page shows only its own Game Bar captures (Portal no longer shows Portal 2's).
- Clean-up: no compiler warnings, less duplicated code.

## 1.2.0 (24 Nov 2026)

- Controller shortcuts: hold Home and press a button for volume, songs, Alt + Tab, the on-screen keyboard or a screenshot, also in games. Their list is in the Power menu.
- A text cursor and Shift / Caps Lock on Big Picture's keyboard.
- Big Picture sound volume.
- Choose where the controller's screenshots are saved.

## 1.1.0 (15 Nov 2026)

- Continue playing (Big Picture and desktop), a weekly summary and Big Picture sounds.
- Add games and write notes with the controller; screenshots on the game page.

## 1.0.18 and earlier (15 Nov 2026)

- 1.0.18: Big Picture has all the desktop filters (Hidden games, Other launcher, categories, saved sort order).
- 1.0.17: fixed-size Big Picture tiles with an even selection ring.
- 1.0.14 to 1.0.16: the mouse wheel scrolls Big Picture panels; a cleaner selection ring at any display scale.
- 1.0.12 and 1.0.13: a purple selection ring on game cards in both modes.
- 1.0.8 to 1.0.11: the mouse pointer hides in Big Picture until the mouse moves; button hints follow the controller or the keyboard; a refresh button on the Stats tab.
- 1.0.7: ratings and notes, swap the A and B buttons, Big Picture opens on the game played last, optional SteamGridDB covers.
- 1.0.6: crash log and error window, safer updates with rollback and release notes, daily backups, Follow Windows theme, Big Picture size, itch.io and Amazon Games.
- 1.0.4 and 1.0.5: a light theme, and Check for updates in Settings.
- 1.0.1 to 1.0.3: faster cover lookups, the mouse in Big Picture, updates that leave no old files behind.
- 1.0.0: the first release.
