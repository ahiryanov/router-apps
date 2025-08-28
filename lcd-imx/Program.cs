using System;
using System.IO;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

class Program
{
    // Параметры по умолчанию (перекрываются sysfs/CLI)
    static int WIDTH = 128;
    static int HEIGHT = 32;
    static bool[,] pix = new bool[WIDTH, HEIGHT];
    static string devicePath = "/dev/fb0";
    static string text = "HELLO";
    static int startX = 0, startY = 0;
    static int scale = 1;

    // шрифт
    static Dictionary<char, byte[]> font;
    static string fontPath = null;

    // упаковка
    static int strideBytes = 16;        // авто: rows→WIDTH/8, pages→WIDTH
    static int yRepeat = 2;             // повторять каждую строку N раз (для 128x64 fb на физ. 128x32 → N=2)

    // биты/порядки
    static string pageOrder = "t2b";      // t2b или b2t
    static string colOrder = "l2r";       // l2r или r2l

    static void Main(string[] args)
    {
        try
        {
            ParseArgs(args);
            ReallocPixIfNeeded();

            font = LoadFontOrDefault(fontPath);

            RenderText(startX, startY, text, scale);

            byte[] frame = PackRows();
            using var fs = new FileStream(devicePath, FileMode.Open, FileAccess.Write);
            int actualStride = strideBytes;
            fs.Write(frame, 0, frame.Length);
            fs.Flush(true);
		}
        catch (Exception ex)
        {
            Console.Error.WriteLine("[ERROR] " + ex.Message);
            Environment.ExitCode = 1;
        }
    }

    static void ReallocPixIfNeeded()
    {
        if (pix.GetLength(0) != WIDTH || pix.GetLength(1) != HEIGHT)
            pix = new bool[WIDTH, HEIGHT];
    }

    static void ParseArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                // общие
                case "--device": devicePath = args[++i]; break;

                // текст
                case "--text": text = args[++i]; break;
                case "--x": startX = int.Parse(args[++i]); break;
                case "--y": startY = int.Parse(args[++i]); break;
                case "--scale": scale = Math.Max(1, Math.Min(4, int.Parse(args[++i]))); break;
                case "--font": fontPath = args[++i]; break;

                // упаковка/раскладка
                case "--stride-bytes": strideBytes = int.Parse(args[++i]); break;
                case "--yscale": yRepeat = Math.Max(1, int.Parse(args[++i])); break;
                case "--page-order": pageOrder = args[++i]; break;
                case "--col-order": colOrder = args[++i]; break;

                default:
                    Console.WriteLine("Неизвестный ключ: " + a);
                    break;
            }
        }
    }

    static void Clear(bool value)
    {
        for (int x = 0; x < WIDTH; x++)
            for (int y = 0; y < HEIGHT; y++)
                pix[x, y] = value;
    }

    // Построчная укладка: HEIGHT строк, каждая по strideBytes байт (обычно WIDTH/8=16)
    // Внутри строки каждый байт кодирует 8 горизонтальных пикселей.
    //   rowMsbFirst=true  → бит7=левый пиксель байта, бит0=правый
    //   rowMsbFirst=false → бит0=левый, бит7=правый
    static byte[] PackRows()
    {
        int sb = strideBytes;
        int deviceLines = HEIGHT * Math.Max(1, yRepeat);
        var frame = new byte[sb * deviceLines];
        var lineBuf = new byte[sb];
        for (int y = 0; y < HEIGHT; y++)
        {
            Array.Clear(lineBuf, 0, lineBuf.Length);
            for (int x = 0; x < WIDTH; x++)
            {
                bool on = pix[x, y];
                int byteIndex = (x >> 3);
                int bitPos = x & 7;
                int b = bitPos; // LSB слева
                if (on) lineBuf[byteIndex] |= (byte)(1 << b);
			}
            // копируем строку yRepeat раз в выходной кадр
            int baseLine = y * yRepeat;
            for (int r = 0; r < yRepeat; r++)
            {
                int dstOff = (baseLine + r) * sb;
                Buffer.BlockCopy(lineBuf, 0, frame, dstOff, sb);
            }
        }
        return frame;
	}

    static void RenderText(int x, int y, string s, int sc)
    {
        int cx = x;
        foreach (char ch in s)
        {
            if (ch == '\n') { y += (5 * sc) + sc; cx = x; continue; }
            if (!font.TryGetValue(ch, out var glyph)) glyph = new byte[] { 0x1F, 0x11, 0x1F };
            DrawGlyph(cx, y, glyph, sc);
            cx += (glyph.Length * sc) + sc;
            if (cx >= WIDTH) break;
        }
    }

    static void DrawGlyph(int x0, int y0, byte[] glyph, int sc)
    {
        const int gh = 5;
        for (int col = 0; col < glyph.Length; col++)
            for (int row = 0; row < gh; row++)
                if (((glyph[col] >> row) & 1) != 0)
                    for (int dx = 0; dx < sc; dx++)
                        for (int dy = 0; dy < sc; dy++)
                        {
                            int x = x0 + col * sc + dx;
                            int y = y0 + row * sc + dy;
                            if ((uint)x < WIDTH && (uint)y < HEIGHT) pix[x, y] = true;
                        }
    }

    // ====== Шрифт 3×5 по умолчанию ======
    static Dictionary<char, byte[]> LoadFontOrDefault(string path)
    {
        var dict = new Dictionary<char, byte[]>();
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var eq = line.IndexOf('='); if (eq <= 0) continue;
                string key = line.Substring(0, eq);
                string vals = line.Substring(eq + 1);
                char ch;
                if (key.Length == 1) ch = key[0];
                else if (int.TryParse(key, out int code)) ch = (char)code;
                else continue;
                var parts = vals.Split(new[] { ' ', '	', ',' }, StringSplitOptions.RemoveEmptyEntries);
                var cols = new List<byte>();
                foreach (var p in parts)
                {
                    string q = p.Trim();
                    if (q.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) cols.Add((byte)int.Parse(q.Substring(2), NumberStyles.HexNumber));
                    else cols.Add((byte)int.Parse(q));
                }
                dict[ch] = cols.ToArray();
            }
            if (dict.Count > 0) return dict;
        }
        void Add(char c, params byte[] cols) => dict[c] = cols;
        Add(' ', 0x00, 0x00, 0x00); Add('.', 0x00, 0x00, 0x10); Add(':', 0x00, 0x14, 0x00);
        Add('-', 0x04, 0x04, 0x04); Add('_', 0x10, 0x10, 0x10); Add('/', 0x01, 0x02, 0x04);
        Add('\\',0x04, 0x02, 0x01); Add('!', 0x00, 0x1D, 0x00); Add('?', 0x02, 0x15, 0x06);
        Add('+', 0x04, 0x1F, 0x04); Add('=', 0x0A, 0x0A, 0x0A);
        Add('0', 0x0E, 0x11, 0x0E); Add('1', 0x00, 0x12, 0x1F); Add('2', 0x12, 0x19, 0x16);
        Add('3', 0x11, 0x15, 0x0A); Add('4', 0x07, 0x04, 0x1F); Add('5', 0x17, 0x15, 0x09);
        Add('6', 0x0E, 0x15, 0x09); Add('7', 0x01, 0x01, 0x1F); Add('8', 0x0A, 0x15, 0x0A);
        Add('9', 0x12, 0x15, 0x0E);
        Add('A', 0x1E, 0x05, 0x1E); Add('B', 0x1F, 0x15, 0x0A); Add('C', 0x0E, 0x11, 0x11);
        Add('D', 0x1F, 0x11, 0x0E); Add('E', 0x1F, 0x15, 0x11); Add('F', 0x1F, 0x05, 0x01);
        Add('G', 0x0E, 0x11, 0x1D); Add('H', 0x1F, 0x04, 0x1F); Add('I', 0x11, 0x1F, 0x11);
        Add('J', 0x08, 0x10, 0x0F); Add('K', 0x1F, 0x04, 0x1B); Add('L', 0x1F, 0x10, 0x10);
        Add('M', 0x1F, 0x06, 0x1F); Add('N', 0x1F, 0x0E, 0x1F); Add('O', 0x0E, 0x11, 0x0E);
        Add('P', 0x1F, 0x05, 0x02); Add('Q', 0x0E, 0x19, 0x1E); Add('R', 0x1F, 0x0D, 0x12);
        Add('S', 0x12, 0x15, 0x09); Add('T', 0x01, 0x1F, 0x01); Add('U', 0x0F, 0x10, 0x0F);
        Add('V', 0x07, 0x18, 0x07); Add('W', 0x1F, 0x0C, 0x1F); Add('X', 0x1B, 0x04, 0x1B);
        Add('Y', 0x03, 0x1C, 0x03); Add('Z', 0x19, 0x15, 0x13);
        for (char c = 'a'; c <= 'z'; c++) if (!dict.ContainsKey(c) && dict.ContainsKey(char.ToUpperInvariant(c))) dict[c] = dict[char.ToUpperInvariant(c)];
        return dict;
    }
}