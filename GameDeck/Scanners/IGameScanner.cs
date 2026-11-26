using GameDeck.Models;

namespace GameDeck.Scanners;

public interface IGameScanner
{
    List<Game> Scan();
}
