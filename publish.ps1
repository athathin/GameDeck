# Publishes GameDeck: one self-contained .exe for 32-bit (win-x86) and one for 64-bit (win-x64), no installer.
# Any publish does both (see the PublishOtherArchitecture target in GameDeck.csproj), so this is the same as
# pressing Publish in Visual Studio.
# Output: GameDeck\bin\Release\net10.0-windows10.0.19041.0\publish\win-x86\GameDeck.exe and ...\win-x64\GameDeck.exe
# Removing GameDeck from a PC: Settings > Uninstall GameDeck.
$ErrorActionPreference = 'Stop'

dotnet publish (Join-Path $PSScriptRoot 'GameDeck\GameDeck.csproj') -p:PublishProfile=FolderProfile
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
