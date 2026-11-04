using System.Windows.Media;

namespace AgentOrchestrator;

// The app's bundled fonts (Fonts\, inside the exe): Inter for the interface and text, Newsreader for titles and the
// headings in replies, JetBrains Mono for code. Each falls back to a Windows font. MainWindow.xaml names the same ones.
internal static class AppFonts
{
    public static readonly FontFamily Serif = new("pack://application:,,,/Fonts/#Newsreader 16pt, Georgia");
    public static readonly FontFamily Mono = new("pack://application:,,,/Fonts/#JetBrains Mono, Consolas");
}
