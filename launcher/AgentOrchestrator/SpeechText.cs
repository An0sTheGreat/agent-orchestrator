using System.Text;
using System.Text.RegularExpressions;

namespace AgentOrchestrator;

// A reply's markdown as the prose Kokoro reads (ForSpeech), and that prose in sentences for sentence-by-sentence synthesis
// (Sentences). Pure: no settings file, no engine.
internal static partial class SpeechText
{
    // One block per line, each ending like a sentence: fenced code skipped ("Code skipped." once) or read (SkipCode off), long
    // inline code dropped, links read by their text and bare URLs dropped, headings as a sentence (ReadHeadings), tables as
    // "A table with N rows." (TableSummary; else row by row), list items one by one, emoji dropped, <think> blocks dropped
    // unless ReadThinking, numbers as words (Kokoro's lexicon has no digits: sherpa-onnx 2.x skips words it lacks).
    public static string ForSpeech(string markdown, TtsSettings s)
    {
        var text = s.ReadThinking ? markdown : ThinkBlock().Replace(markdown, " ");
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var output = new List<string>();
        var paragraph = new List<string>();
        var codeSaid = false;
        void Add(string piece)
        {
            piece = Inline(piece, s);
            if (piece.Length > 0)
                output.Add(Sentence(piece));
        }
        void Flush()
        {
            if (paragraph.Count > 0)
                Add(string.Join(' ', paragraph));
            paragraph.Clear();
        }
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("```") || line.StartsWith("~~~"))
            {
                Flush();
                var fence = line[..3];
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith(fence); i++)
                    code.Add(lines[i]);
                if (!s.SkipCode)
                    code.ForEach(Add);
                else if (!codeSaid)
                {
                    output.Add("Code skipped.");
                    codeSaid = true;
                }
                continue;
            }
            if (line.StartsWith('|'))
            {
                Flush();
                var rows = new List<string>();
                for (; i < lines.Length && lines[i].TrimStart().StartsWith('|'); i++)
                    if (!TableRule().IsMatch(lines[i].Trim()))
                        rows.Add(lines[i].Trim());
                i--;
                if (s.TableSummary)
                {
                    var n = Math.Max(0, rows.Count - 1); // the first row is the header
                    output.Add($"A table with {Words(n)} {(n == 1 ? "row" : "rows")}.");
                }
                else
                    foreach (var row in rows)
                        Add(string.Join(", ", row.Trim('|').Split('|').Select(c => c.Trim()).Where(c => c.Length > 0)));
                continue;
            }
            if (Heading().Match(line) is { Success: true } heading)
            {
                Flush();
                if (s.ReadHeadings)
                    Add(heading.Groups[1].Value);
                continue;
            }
            if (line.Length == 0 || Rule().IsMatch(line))
            {
                Flush();
                continue;
            }
            line = line.TrimStart('>').Trim();
            if (ListItem().Match(line) is { Success: true } item)
            {
                Flush();
                Add(item.Groups[1].Value);
                continue;
            }
            paragraph.Add(line);
        }
        Flush();
        return string.Join('\n', output);
    }

    private static string Sentence(string piece) => piece[^1] is '.' or '!' or '?' or ':' or ';' or '…' ? piece : piece + ".";

    private static string Inline(string text, TtsSettings s)
    {
        text = InlineCode().Replace(text, m => s.SkipCode && m.Groups[1].Value.Length > 40 ? " " : m.Groups[1].Value);
        text = Image().Replace(text, " ");
        text = Link().Replace(text, "$1");
        text = Url().Replace(text, " ");
        text = Tag().Replace(text, " ");
        text = text.Replace("**", "").Replace("__", "").Replace("~~", "").Replace("*", "");
        text = Underscored().Replace(text, "$1");
        text = Number().Replace(text, m => NumberWords(m.Value));
        var kept = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
            if (!IsEmoji(rune))
                kept.Append(rune.ToString());
        return SpaceBeforePunctuation().Replace(Spaces().Replace(kept.ToString(), " "), "$1").Trim();
    }

    private static bool IsEmoji(Rune r) =>
        (Rune.GetUnicodeCategory(r) == System.Globalization.UnicodeCategory.OtherSymbol && r.Value >= 0x2100)
        || r.Value is 0xFE0F or 0x200D or 0x20E3 or (>= 0x1F3FB and <= 0x1F3FF);

    // "9,000" nine thousand; "3.5" three point five (the decimals digit by digit); "1.2.3" one point two point three; "50%".
    private static string NumberWords(string number)
    {
        var percent = number.EndsWith('%');
        var parts = number.TrimEnd('%').Replace(",", "").Split('.');
        string spoken;
        if (parts.Length == 2)
            spoken = Words(parts[0]) + " point " + string.Join(' ', parts[1].Select(d => Words(d - '0')));
        else
            spoken = string.Join(" point ", parts.Select(Words));
        return " " + spoken + (percent ? " percent" : "") + " ";
    }

    // ponytail: digits read one by one past 12 digits, and ordinals ("1st") are left to the engine, which skips them
    private static string Words(string digits) =>
        digits.Length <= 12 ? Words(long.Parse(digits)) : string.Join(' ', digits.Select(d => Words(d - '0')));

    private static readonly string[] Ones = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"];
    private static readonly string[] Tens = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];

    private static string Words(long n)
    {
        if (n < 20)
            return Ones[n];
        if (n < 100)
            return Tens[n / 10] + (n % 10 > 0 ? " " + Ones[n % 10] : "");
        if (n < 1000)
            return Ones[n / 100] + " hundred" + (n % 100 > 0 ? " " + Words(n % 100) : "");
        foreach (var (scale, name) in new[] { (1_000_000_000L, "billion"), (1_000_000L, "million"), (1000L, "thousand") })
            if (n >= scale)
                return Words(n / scale) + " " + name + (n % scale > 0 ? " " + Words(n % scale) : "");
        return "";
    }

    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
        { "e.g", "i.e", "etc", "mr", "mrs", "ms", "dr", "vs", "st", "prof", "jr", "sr", "approx", "fig" };

    // Split after . ! ? … (and closing quotes or brackets) followed by a space, and at line ends; not after an abbreviation
    // ("e.g."), an initial ("J."), or inside a number or name ("3.5", "file.txt").
    public static List<string> Sentences(string text)
    {
        var result = new List<string>();
        void Add(string piece)
        {
            if (piece.Trim() is { Length: > 0 } trimmed)
                result.Add(trimmed);
        }
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var start = 0;
            for (var i = 0; i < line.Length; i++)
            {
                if (line[i] is not ('.' or '!' or '?' or '…'))
                    continue;
                var end = i;
                while (end + 1 < line.Length && line[end + 1] is '.' or '!' or '?' or '…' or '"' or '\'' or ')' or '”' or '’')
                    end++;
                var spaceAfter = end + 1 == line.Length || char.IsWhiteSpace(line[end + 1]);
                var word = line[(line.LastIndexOf(' ', i) + 1)..i];
                if (spaceAfter && !(line[i] == '.' && (Abbreviations.Contains(word) || word is [var c] && char.IsLetter(c))))
                {
                    Add(line[start..(end + 1)]);
                    start = end + 1;
                }
                i = end;
            }
            Add(line[start..]);
        }
        return result;
    }

    [GeneratedRegex(@"<think>.*?(</think>|$)", RegexOptions.Singleline)] private static partial Regex ThinkBlock();
    [GeneratedRegex(@"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?$")] private static partial Regex TableRule();
    [GeneratedRegex(@"^#{1,6}\s+(.*?)\s*#*$")] private static partial Regex Heading();
    [GeneratedRegex(@"^([-*_]\s*){3,}$")] private static partial Regex Rule();
    [GeneratedRegex(@"^(?:[-*+]|\d+[.)])\s+(.*)$")] private static partial Regex ListItem();
    [GeneratedRegex(@"`+([^`]+)`+")] private static partial Regex InlineCode();
    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")] private static partial Regex Image();
    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")] private static partial Regex Link();
    [GeneratedRegex(@"<?\b(?:https?://|www\.)[^\s>)]+>?")] private static partial Regex Url();
    [GeneratedRegex(@"</?[a-zA-Z][^>]*>")] private static partial Regex Tag();
    [GeneratedRegex(@"(?<!\w)_(\S(?:[^_]*\S)?)_(?!\w)")] private static partial Regex Underscored();
    [GeneratedRegex(@"(?<![\d.])(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d+)*%?(?!\w)")] private static partial Regex Number();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
    [GeneratedRegex(@" ([,.;:!?])")] private static partial Regex SpaceBeforePunctuation();
}
