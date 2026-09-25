using System;
using System.Collections.Generic;
using System.Text;

namespace Re2.Core.Formats;

/// <summary>Which glyph set a run of inventory text is written in.</summary>
public enum ItemCharset
{
    English,

    /// <summary>Europe's French page: accented letters, and 0x79 as the full stop.</summary>
    French,

    /// <summary>Japan's: kana as single bytes, more glyphs behind the 0xEE, 0xEF and 0xF0 prefixes.</summary>
    Japanese
}

/// <summary>The character codes the game's menu and inventory text is written in.</summary>
public static class ItemText
{
    /// <summary>Separates one name from the next in the item name list.</summary>
    public const byte NameSeparator = 0xF7;

    /// <summary>Ends a line within a message; read and written as a newline.</summary>
    public const byte LineBreak = 0xFC;

    /// <summary>Starts a new page of an examine message.</summary>
    public const byte PageBreak = 0xFD;

    /// <summary>Ends a message.</summary>
    public const byte MessageEnd = 0xFE;

    /// <summary>
    /// A glyph is one byte, or for Japanese a prefix and a byte; two-byte units are keyed
    /// <c>(prefix &lt;&lt; 8) | byte</c>.
    /// </summary>
    private sealed record Glyphs(Dictionary<int, char> ToChar, Dictionary<char, int> ToCode, bool Prefixed);

    private static readonly Dictionary<int, char> EnglishChars = new()
    {
        [0x00] = ' ',
        [0x01] = '.',
        [0x09] = '“',
        [0x0A] = '”',
        [0x18] = ',',
        [0x1A] = '!',
        [0x1B] = '?',
        [0x38] = '/',
        [0x3A] = '\'',
        [0x3B] = '-',

        // Messages wrap by hand rather than by measuring, so a line break is a character like any
        // other. Names never contain one, so carrying it here costs them nothing.
        [LineBreak] = '\n',
    };

    private static readonly Glyphs English = Build(WithAlphanumerics(EnglishChars), new() { ['"'] = 0x09 }, false);
    private static readonly Glyphs French = Build(WithAlphanumerics(BuildFrench()), new() { ['"'] = 0x19 }, false);
    private static readonly Glyphs Japanese = BuildJapanese();

    private static Glyphs For(ItemCharset charset) => charset switch
    {
        ItemCharset.French => French,
        ItemCharset.Japanese => Japanese,
        _ => English
    };

    private static Dictionary<int, char> WithAlphanumerics(Dictionary<int, char> map)
    {
        for (int i = 0; i < 10; i++) map[0x0C + i] = (char)('0' + i);
        for (int i = 0; i < 26; i++) map[0x1D + i] = (char)('A' + i);
        for (int i = 0; i < 26; i++) map[0x3D + i] = (char)('a' + i);
        return map;
    }

    /// <summary>
    /// Europe's French glyph page: its full stop is 0x79 rather than 0x01, and it adds the accented
    /// letters. Read off the retail French text; codes it never uses stay as &lt;XX&gt;.
    /// </summary>
    private static Dictionary<int, char> BuildFrench()
    {
        var map = new Dictionary<int, char>(EnglishChars);
        map.Remove(0x01);
        map[0x16] = ':';
        map[0x19] = '"';
        map[0x5F] = 'à';
        map[0x63] = 'è';
        map[0x65] = 'é';
        map[0x67] = 'ê';
        map[0x6D] = 'ô';
        map[0x6F] = 'ù';
        map[0x72] = 'Ç';
        map[0x73] = 'ç';
        map[0x78] = '«';
        map[0x79] = '.';
        map[0x7F] = '&';
        map[0x87] = 'ë';
        map[0x88] = '°';
        return map;
    }

    // Japan's two font sheets, read cell by cell: 18 glyphs to a 14-pixel row. The kana sheet
    // (menu asset 5410) serves single bytes from its top row and 0xEE xx from row 13; the kanji
    // sheet (5411) serves 0xEF xx from its top row and 0xF0 xx from row 14. '\0' is a cell that
    // holds no single character (a button icon, a two-letter ligature) or nothing at all; those
    // stay as <XX> escapes.

    /// <summary>Kana sheet from code 0x00: punctuation, digits, Latin letters, then kana.</summary>
    private const string KanaSheet =
        " .▶「」()『』“”▼012345" +
        "6789:、,\"!?⁉ABCDEFG" +
        "HIJKLMNOPQRSTUVWXY" +
        "Z[/]'ー・abcdefghijk" +
        "lmnopqrstuvwxyzあいう" +
        "えおかきくけこさしすせそたちつてとな" +
        "にぬねのはひふ敗ほまみむめもやゆよら" +       // へ is drawn with the katakana ヘ
        "りるれろわをんがぎぐげござじずぜぞだ" +
        "ぢづでどばびぶ空ぼぱぴぷぺぽぁぃぅぇ" +       // and べ with ベ
        "ぉゃゅょっアイウエオカキクケコサシス" +
        "セソタチツテトナニヌネノハヒフヘホマ" +
        "ミムメモヤユヨラリルレロワヲンガギグ" +
        "ゲゴザジズゼゾダヂヅデドバビブベボパ" +
        "ピプペポァィゥェォャュョッヴ\0\0\0\0" +
        "&…矢炎\0\0△○×□■\0上右下左使用" +
        "出弾来事開何銃力社爆小強発製刻大電先";

    /// <summary>Where 0xEE xx starts on the kana sheet: row 13.</summary>
    private const int EePage = 13 * 18;

    /// <summary>Kanji sheet from 0xEF 00. Its second block starts at row 12.</summary>
    private const string KanjiSheet =
        "書型動角気合撃長器物込石射形赤部入鉄" +
        "拳差無料体付火武品組燃放実験跡青宝重" +
        "意味図室屋写時復学化全行回分量薬連軍" +
        "内閉消録血蛇毒理破外簡真単作試記信管" +
        "計木退硫酸圧対高多様機後反切署机裏役" +
        "立中救急解日向取察警特要源装置生調一" +
        "庫車程倉超導殊金歯編古査状況待暗処水" +
        "第場研究所備姿必存者失報手告覧秘保安" +
        "戦施設宿直登方法員表成今販人関経過指" +
        "令手紙偉説家遺言傭兵文新帳始祖研究経" +
        "緯求情開\0\0\0\0\0\0\0\0\0\0\0\0\0\0" +
        "\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0" +
        "私通見危女壊探険棚押絡工家本\0逃知彼" +
        "脱君崩々並街落丈夫路働話前間俺月当口" +
        "風整列性台男絵神情\0\0\0兄離誰悪目息" +
        "地降不聞準給槽裂漏光油他術階持現板描" +
        "捜企業好違男恋新件数得\0\0\0\0\0\0\0" +
        "\0\0完\0\0\0\0\0\0\0戻\0著美像\0子穴";

    /// <summary>Every character Japan's two font sheets can draw, for a display font to cover.</summary>
    public static string JapaneseGlyphs => KanaSheet + KanjiSheet;

    /// <summary>Where 0xF0 xx starts on the kanji sheet: row 14.</summary>
    private const int F0Page = 14 * 18;

    private static Glyphs BuildJapanese()
    {
        var toChar = new Dictionary<int, char>();

        void Add(int unit, char c)
        {
            if (c != '\0') toChar[unit] = c;
        }

        // Single bytes run up to 0xED; the prefixes and the control codes sit above.
        for (int code = 0; code <= 0xED && code < KanaSheet.Length; code++) Add(code, KanaSheet[code]);
        for (int e = 0; EePage + e < KanaSheet.Length && e <= 0xFF; e++) Add(0xEE00 | e, KanaSheet[EePage + e]);
        for (int f = 0; f < KanjiSheet.Length && f <= 0xFF; f++) Add(0xEF00 | f, KanjiSheet[f]);
        for (int f = 0; F0Page + f < KanjiSheet.Length; f++) Add(0xF000 | f, KanjiSheet[F0Page + f]);

        toChar[LineBreak] = '\n';

        // Typed alternatives: へ and べ are drawn with their katakana twins, and ASCII stands in for
        // the look-alikes a keyboard does not have.
        var typed = new Dictionary<char, int>
        {
            ['へ'] = KanaSheet.IndexOf('ヘ'),
            ['べ'] = KanaSheet.IndexOf('ベ'),
            ['-'] = 0x3B,
            ['。'] = 0x01,
            ['!'] = 0x1A,
            ['?'] = 0x1B,
        };

        return Build(toChar, typed, true);
    }

    private static Glyphs Build(Dictionary<int, char> toChar, Dictionary<char, int> typed, bool prefixed)
    {
        var toCode = new Dictionary<char, int>();

        // Where a character is on the sheet twice, the first -- a single byte if there is one -- wins.
        foreach (var (unit, c) in toChar)
            if (!toCode.TryGetValue(c, out int existing) || Cost(unit) < Cost(existing) ||
                (Cost(unit) == Cost(existing) && unit < existing))
                toCode[c] = unit;

        foreach (var (c, unit) in typed) toCode[c] = unit;

        return new Glyphs(toChar, toCode, prefixed);
    }

    private static int Cost(int unit) => unit > 0xFF ? 2 : 1;

    /// <summary>Is this byte a Japanese two-byte prefix?</summary>
    private static bool IsPrefix(byte b) => b is 0xEE or 0xEF or 0xF0;

    /// <summary>Turns the game's codes into text, escaping anything with no known meaning.</summary>
    public static string Decode(ReadOnlySpan<byte> codes, ItemCharset charset = ItemCharset.English)
    {
        var glyphs = For(charset);
        var text = new StringBuilder(codes.Length);

        for (int i = 0; i < codes.Length; i++)
        {
            byte code = codes[i];

            if (glyphs.Prefixed && IsPrefix(code) && i + 1 < codes.Length)
            {
                int unit = (code << 8) | codes[i + 1];
                if (glyphs.ToChar.TryGetValue(unit, out char kanji))
                {
                    text.Append(kanji);
                    i++;
                    continue;
                }
            }

            if (glyphs.ToChar.TryGetValue(code, out char character)) text.Append(character);
            else text.Append('<').Append(code.ToString("X2")).Append('>');
        }

        return text.ToString();
    }

    /// <summary>
    /// Turns text back into the game's codes, reading <c>&lt;XX&gt;</c> as the byte it names.
    /// </summary>
    public static bool TryEncode(string text, out byte[] codes, out string error,
                                 ItemCharset charset = ItemCharset.English)
    {
        var map = For(charset).ToCode;
        var output = new List<byte>(text.Length);
        codes = Array.Empty<byte>();
        error = "";

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (c == '<')
            {
                int close = text.IndexOf('>', i);
                if (close != i + 3 ||
                    !byte.TryParse(text.AsSpan(i + 1, 2), System.Globalization.NumberStyles.HexNumber,
                                   null, out byte raw))
                {
                    error = $"{Quote(text)}: an escape must be exactly <XX> with two hex digits.";
                    return false;
                }

                output.Add(raw);
                i = close;
                continue;
            }

            if (c is '‘' or '’') c = '\'';          // curly apostrophes, from pasted text
            if (charset != ItemCharset.Japanese && c is '–' or '—') c = '-';

            if (!map.TryGetValue(c, out int unit))
            {
                error = $"{Quote(text)}: the game has no character for '{c}'.";
                return false;
            }

            if (unit > 0xFF) output.Add((byte)(unit >> 8));
            output.Add((byte)unit);
        }

        codes = output.ToArray();
        return true;
    }

    /// <summary>Enough of a string to recognise it by, for a message that may be several lines.</summary>
    private static string Quote(string text)
    {
        string flat = text.Replace('\n', ' ').Trim();
        return "\"" + (flat.Length > 40 ? flat[..40] + "..." : flat) + "\"";
    }

    /// <summary>How many bytes this text will take, or -1 when it cannot be written at all.</summary>
    public static int MeasureOrMinusOne(string text, ItemCharset charset = ItemCharset.English)
        => TryEncode(text, out var codes, out _, charset) ? codes.Length : -1;
}
