using System;

namespace SoundCursor
{
    public static class Program
    {
        [STAThread]
        static void Main()
        {
            using var game = new SoundCursorGame();
            game.Run();
        }
    }
}
