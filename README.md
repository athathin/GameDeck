# GameDeck

One library for the games from all your launchers (Steam, Epic, GOG, Ubisoft, Riot, Xbox, Rockstar, Battle.net, EA, itch.io, Amazon Games and
games you add yourself), with a desktop mode and a full-screen, controller-friendly **Big Picture** mode.

## Download

Go to the [**latest release**](https://github.com/athathin/GameDeck/releases/latest) and download:

| Your Windows | File |
| --- | --- |
| 64-bit (almost every PC) | `GameDeck-win-x64.exe` |
| 32-bit | `GameDeck-win-x86.exe` |

There is nothing to install: put the file wherever you like (a folder you can write to, such as `Documents\GameDeck`)
and run it. Windows may show a SmartScreen warning the first time because the app isn't signed; choose
**More info > Run anyway**.

To remove GameDeck, use **Settings > Uninstall GameDeck** inside the app.

## Updates

Each time GameDeck starts it checks this page for a newer release. If there is one it downloads it, installs it and
starts again as the new version, the way Steam updates itself. You can turn this off in **Settings**.

Settings also has a **Check for updates** button. An update is only made final once the new version has run for a short
while: a version that doesn't start is replaced by the previous one, and isn't offered again.

## Your data

Favorites, playtime, play sessions and settings are saved on your PC in `%AppData%\GameDeck`. A copy of the last seven
days is kept in `%AppData%\GameDeck\backups`, and GameDeck uses it if one of the files is ever damaged. Errors are written
to `%AppData%\GameDeck\logs`; when one happens GameDeck offers to open a bug report that is already filled in (nothing
is sent until you press Submit on GitHub, and your user name is removed from it).

## Privacy

GameDeck has no account, no ads and no tracking. It only contacts:

- **github.com**, to look for and download updates (turn it off in Settings),
- **Steam, Epic and Microsoft's store**, to fetch cover art and look up game names (turn it off with
  "Download cover art from the internet" in Settings),
- **steamgriddb.com**, only if you enter your own SteamGridDB key in Settings: the names of games that have no cover yet
  are sent there to look for one.

Your library, playtime and settings never leave your PC.

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
