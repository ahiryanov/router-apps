// oledtext.cs — универсальный рендер в framebuffer /dev/fb0 для SSD1306/SSD1307
// ОБНОВЛЕНО: исправлены ошибки компиляции (StartsWith, ), добавлены
//  - автоопределение геометрии из /sys/class/graphics/fb0/virtual_size
//  - ключи --width/--height
//  - построчная и страничная укладки, stride, offset в байтах и в строках
//  - режим explore и тестовая сетка для подбора формата
// Работает на .NET/Mono без Win-библиотек.

using System;
using System.IO;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

class Program
{
    // Параметры по умолчанию (перекрываются sysfs/CLI)
    static int WIDTH = 128;
    static int HEIGHT = 32; // если fb говорит 64, но матрица физически 32 — можно оставить 32
    const int PAGE_HEIGHT = 8; // для page layout

    static bool[,] pix = new bool[WIDTH, HEIGHT];

    // общие опции
    static string devicePath = "/dev/fb0";
    static string outFile = null;
    static bool dryRun = false;
    static bool dumpAnsi = false; static bool dumpHex = false;
    static string dumpPbm = null;
    static bool invert = false;
    static bool mirrorX = false, mirrorY = false, rotate180 = false;
    static bool clearBefore = true;

    // текст
    static string text = "HELLO";
    static int startX = 0, startY = 0;
    static int scale = 1;

    // шрифт
    static Dictionary<char, byte[]> font;
    static string fontPath = null;

    // упаковка
    enum Layout { Rows, Pages }
    static Layout layout = Layout.Rows; // типично для fbdev
    static int strideBytes = -1;        // авто: rows→WIDTH/8, pages→WIDTH
    static int offsetBytes = 0;         // смещение записи в /dev/fb0
    static int offsetLines = 0;         // для rows: добавляет offsetBytes += offsetLines*stride
    static int yRepeat = 1;             // повторять каждую строку N раз (для 128x64 fb на физ. 128x32 → N=2)

    // биты/порядки
    static bool reverseBits = false;      // глобальный реверс в каждом байте
    static bool rowMsbFirst = true;       // для построчной: MSB=левый пиксель байта
    static string pageOrder = "t2b";      // t2b или b2t
    static string colOrder = "l2r";       // l2r или r2l

    // диагностика/перебор
    static bool testGrid = false;
    static bool explore = false;

    static void Main(string[] args)
    {
        try
        {
            // Попробовать считать геометрию из sysfs (до ParseArgs, чтобы буфер был верного размера)
            AutoReadGeometryFromSysfs();

            ParseArgs(args);
            ReallocPixIfNeeded();

            font = LoadFontOrDefault(fontPath);
            if (clearBefore) Clear(false);

            if (testGrid) DrawTestGrid();
            else RenderText(startX, startY, text, scale);

            if (rotate180) { mirrorX = !mirrorX; mirrorY = !mirrorY; }
            if (mirrorX || mirrorY) MirrorBuffer(mirrorX, mirrorY);

            if (explore) { RunExplorer(); return; }

            byte[] frame = Pack();

            if (dumpAnsi) DumpAnsi();
            if (dumpHex) DumpHex(frame);
            if (!string.IsNullOrEmpty(dumpPbm)) SavePBM(dumpPbm);
            if (!string.IsNullOrEmpty(outFile)) File.WriteAllBytes(outFile, frame);

            if (!dryRun)
            {
                using var fs = new FileStream(devicePath, FileMode.Open, FileAccess.Write);
                int actualStride = GetStrideBytes();
                int extraOffset = (layout == Layout.Rows && offsetLines > 0) ? offsetLines * actualStride : 0;
                int totalOffset = offsetBytes + extraOffset;
                if (totalOffset > 0) fs.Seek(totalOffset, SeekOrigin.Begin);
                fs.Write(frame, 0, frame.Length);
                fs.Flush(true);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[ERROR] " + ex.Message);
            Environment.ExitCode = 1;
        }
    }

    static void AutoReadGeometryFromSysfs()
    {
        try
        {
            string p = "/sys/class/graphics/fb0/virtual_size";
            if (File.Exists(p))
            {
                var s = File.ReadAllText(p).Trim(); // формат: "128,64" или "128,32"
                var parts = s.Split(',');
                if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
                {
                    if (w > 0 && h > 0)
                    {
                        WIDTH = w; HEIGHT = h;
                        // если знаем, что матрица физически 32 по высоте (SSD1306 128×32),
                        // можно принудительно оставить HEIGHT=32, но драйвер может быть на 64.
                        // Тогда используйте --height 32 и при необходимости --offset-lines 0 или 32.
                    }
                }
            }
        }
        catch { /* молча продолжаем со значениями по умолчанию */ }
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
                case "--out": outFile = args[++i]; break;
                case "--dry-run": dryRun = true; break;
                case "--dump-ansi": dumpAnsi = true; break;
                case "--dump-hex": dumpHex = true; break;
                case "--dump-pbm": dumpPbm = args[++i]; break;
                case "--invert": invert = true; break;
                case "--mirror-x": mirrorX = true; break;
                case "--mirror-y": mirrorY = true; break;
                case "--rotate-180": rotate180 = true; break;
                case "--clear": clearBefore = true; break;
                case "--no-clear": clearBefore = false; break;
                case "--width": WIDTH = int.Parse(args[++i]); ReallocPixIfNeeded(); break;
                case "--height": HEIGHT = int.Parse(args[++i]); ReallocPixIfNeeded(); break;

                // текст
                case "--text": text = args[++i]; break;
                case "--x": startX = int.Parse(args[++i]); break;
                case "--y": startY = int.Parse(args[++i]); break;
                case "--scale": scale = Math.Max(1, Math.Min(4, int.Parse(args[++i]))); break;
                case "--font": fontPath = args[++i]; break;
                case "--test-grid": testGrid = true; break;

                // упаковка/раскладка
                case "--layout":
                    var v = args[++i].ToLowerInvariant();
                    layout = v.StartsWith("p") ? Layout.Pages : Layout.Rows; break;
                case "--stride-bytes": strideBytes = int.Parse(args[++i]); break;
                case "--offset": offsetBytes = int.Parse(args[++i]); break;
                case "--offset-lines": offsetLines = int.Parse(args[++i]); break;
                case "--y-repeat": yRepeat = Math.Max(1, int.Parse(args[++i])); break;
                case "--yscale": yRepeat = Math.Max(1, int.Parse(args[++i])); break;
                case "--reverse-bits": reverseBits = true; break;
                case "--no-reverse-bits": reverseBits = false; break;
                case "--row-msb-first": rowMsbFirst = true; break;
                case "--row-lsb-first": rowMsbFirst = false; break;
                case "--page-order": pageOrder = args[++i]; break;
                case "--col-order": colOrder = args[++i]; break;

                // перебор
                case "--explore": explore = true; break;

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

    static void MirrorBuffer(bool mx, bool my)
    {
        if (mx)
        {
            for (int x = 0; x < WIDTH / 2; x++)
            {
                int xr = WIDTH - 1 - x;
                for (int y = 0; y < HEIGHT; y++)
                { var t = pix[x, y]; pix[x, y] = pix[xr, y]; pix[xr, y] = t; }
            }
        }
        if (my)
        {
            for (int y = 0; y < HEIGHT / 2; y++)
            {
                int yr = HEIGHT - 1 - y;
                for (int x = 0; x < WIDTH; x++)
                { var t = pix[x, y]; pix[x, y] = pix[x, yr]; pix[x, yr] = t; }
            }
        }
    }

    static int GetStrideBytes()
    {
        return (layout == Layout.Rows)
            ? (strideBytes > 0 ? strideBytes : (WIDTH + 7) / 8)
            : (strideBytes > 0 ? strideBytes : WIDTH);
    }

    static byte[] Pack()
    {
        if (layout == Layout.Rows) return PackRows();
        return PackPages();
    }

    // Построчная укладка: HEIGHT строк, каждая по strideBytes байт (обычно WIDTH/8=16)
    // Внутри строки каждый байт кодирует 8 горизонтальных пикселей.
    //   rowMsbFirst=true  → бит7=левый пиксель байта, бит0=правый
    //   rowMsbFirst=false → бит0=левый, бит7=правый
    static byte[] PackRows()
    {
        int sb = GetStrideBytes();
        int deviceLines = HEIGHT * Math.Max(1, yRepeat);
        var frame = new byte[sb * deviceLines];
        var lineBuf = new byte[sb];
        for (int y = 0; y < HEIGHT; y++)
        {
            Array.Clear(lineBuf, 0, lineBuf.Length);
            for (int x = 0; x < WIDTH; x++)
            {
                bool on = pix[x, y];
                if (invert) on = !on;
                int byteIndex = (x >> 3);
                int bitPos = x & 7;
                if (rowMsbFirst)
                {
                    int b = 7 - bitPos; // MSB слева
                    if (on) lineBuf[byteIndex] |= (byte)(1 << b);
                }
                else
                {
                    int b = bitPos; // LSB слева
                    if (on) lineBuf[byteIndex] |= (byte)(1 << b);
                }
            }
            // при необходимости реверсим биты побайтно
            if (reverseBits)
            {
                for (int i = 0; i < sb; i++) lineBuf[i] = BitReverse(lineBuf[i]);
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

    // «Страничная» укладка: (HEIGHT/8) страниц × WIDTH колонок, как у сырого SSD1306/1307
    static byte[] PackPages()
    {
        int pages = HEIGHT / PAGE_HEIGHT;
        int sb = GetStrideBytes(); // обычно WIDTH
        var frame = new byte[sb * pages];
        for (int p = 0; p < pages; p++)
        {
            int pageIndex = pageOrder.StartsWith("b", StringComparison.OrdinalIgnoreCase) ? (pages - 1 - p) : p;
            int y0 = p * PAGE_HEIGHT;
            for (int x = 0; x < WIDTH; x++)
            {
                int xc = colOrder.StartsWith("r", StringComparison.OrdinalIgnoreCase) ? (WIDTH - 1 - x) : x;
                byte b = 0;
                for (int k = 0; k < PAGE_HEIGHT; k++)
                {
                    int y = y0 + k;
                    bool on = pix[xc, y];
                    if (invert) on = !on;
                    if (on) b |= (byte)(1 << k); // бит k = строка внутри страницы
                }
                if (reverseBits) b = BitReverse(b);
                int idx = pageIndex * sb + x;
                if (idx >= 0 && idx < frame.Length) frame[idx] = b;
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

    static void DrawTestGrid()
    {
        for (int x = 0; x < WIDTH; x++) pix[x, 0] = true; // верхняя граница
        for (int y = 0; y < HEIGHT; y++) pix[0, y] = true; // левая граница
        for (int x = 0; x < WIDTH; x += 8) for (int y = 0; y < HEIGHT; y++) pix[x, y] = (y % 2 == 0);
        for (int y = 0; y < HEIGHT; y += 8) for (int x = 0; x < WIDTH; x++) pix[x, y] = (x % 2 == 0);
        RenderText(2, 2, "0,0", 1);
    }

    static void DumpAnsi()
    {
        for (int y = 0; y < HEIGHT; y++)
        {
            var sb = new StringBuilder(WIDTH);
            for (int x = 0; x < WIDTH; x++) sb.Append(pix[x, y] ? '#' : '.');
            Console.WriteLine(sb.ToString());
        }
    }

    static void DumpHex(byte[] frame)
    {
        int sb = GetStrideBytes();
        Console.WriteLine($"HEX dump, len={frame.Length}, stride={sb}, layout={layout}, size={WIDTH}x{HEIGHT}");
        for (int i = 0; i < frame.Length; i += 16)
        {
            var sbuf = new StringBuilder();
            sbuf.Append($"{i:X4} : ");
            for (int j = 0; j < 16 && i + j < frame.Length; j++)
                sbuf.Append(frame[i + j].ToString("X2")).Append(' ');
            Console.WriteLine(sbuf.ToString());
        }
    }

    static void SavePBM(string path)
    {
        int rowBytes = (WIDTH + 7) / 8;
        byte[] pbm = new byte[rowBytes * HEIGHT];
        for (int y = 0; y < HEIGHT; y++)
        {
            for (int x = 0; x < WIDTH; x++)
            {
                int byteIndex = y * rowBytes + (x >> 3);
                int bit = 7 - (x & 7);
                if (pix[x, y]) pbm[byteIndex] |= (byte)(1 << bit);
            }
        }
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);
        var header = Encoding.ASCII.GetBytes($"P4{WIDTH} {HEIGHT}");
        bw.Write(header);
        bw.Write(pbm);
    }

    static void RunExplorer()
    {
        // Рисуем сетку поверх всего буфера, чтобы глазом понимать ориентацию
        if (!testGrid) { Clear(false); DrawTestGrid(); }

        var profiles = new List<(string note, Action setup)>();
        profiles.Add(("rows, MSB-first, stride=16", () => { layout=Layout.Rows; rowMsbFirst=true; reverseBits=false; strideBytes=16; offsetLines=0; }));
        profiles.Add(("rows, LSB-first, stride=16", () => { layout=Layout.Rows; rowMsbFirst=false; reverseBits=false; strideBytes=16; offsetLines=0; }));
        profiles.Add(("rows, MSB-first + reverse-bits, stride=16", () => { layout=Layout.Rows; rowMsbFirst=true; reverseBits=true; strideBytes=16; offsetLines=0; }));
        profiles.Add(("rows, MSB-first, stride=16, offset-lines=32", () => { layout=Layout.Rows; rowMsbFirst=true; reverseBits=false; strideBytes=16; offsetLines=32; yRepeat=1; }));
        profiles.Add(("rows, MSB-first, stride=16, yRepeat=2 (для физ. 32 на fb 64)", () => { layout=Layout.Rows; rowMsbFirst=true; reverseBits=false; strideBytes=16; offsetLines=0; yRepeat=2; }));
        profiles.Add(("pages, t2b, l2r", () => { layout=Layout.Pages; reverseBits=false; strideBytes=WIDTH; }));

        int idx = 0;
        foreach (var p in profiles)
        {
            idx++;
            Console.WriteLine($"[EXPLORE {idx}] {p.note}");
            p.setup();
            var frame = Pack();
            if (dumpHex) { Console.WriteLine($"-- HEX ({p.note}) --"); DumpHex(frame); }
            if (!dryRun)
            {
                using var fs = new FileStream(devicePath, FileMode.Open, FileAccess.Write);
                int actualStride = GetStrideBytes();
                int extraOffset = (layout == Layout.Rows && offsetLines > 0) ? offsetLines * actualStride : 0;
                int totalOffset = offsetBytes + extraOffset;
                if (totalOffset > 0) fs.Seek(totalOffset, SeekOrigin.Begin);
                fs.Write(frame, 0, frame.Length); fs.Flush(true);
            }
            System.Threading.Thread.Sleep(150);
        }
    }

    static byte BitReverse(byte b) => BitReverseTable[b];
    static readonly byte[] BitReverseTable = CreateBitReverseTable();
    static byte[] CreateBitReverseTable()
    {
        var t = new byte[256];
        for (int i = 0; i < 256; i++)
        { int v=i, r=0; for (int k=0;k<8;k++){ r=(r<<1)|(v&1); v>>=1; } t[i]=(byte)r; }
        return t;
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

/*
СБОРКА:
  mcs oledtext.cs -out:oledtext.exe
  sudo mono oledtext.exe --text "IIIIIIII" --x 20 --y 10 --scale 3 --layout rows --dump-ansi
ИЛИ dotnet:
  dotnet build -c Release
  sudo dotnet run -- --text "IIIIIIII" --x 20 --y 10 --scale 3 --layout rows

ПРОФИЛИ ДЛЯ SSD1307 (fbset показывает 128x64x1, name=Solomon SSD1307):
  # 1) Построчно, MSB first, stride=16, рисуем верхние 32 строки
  --layout rows --row-msb-first --stride-bytes 16 --width 128 --height 32

  # 2) Если «ломает» каждые 8px — LSB first
  --layout rows --row-lsb-first --stride-bytes 16 --width 128 --height 32

  # 3) Видна нижняя половина — запишите туда
  --layout rows --row-msb-first --stride-bytes 16 --width 128 --height 32 --offset-lines 32

  # 4) Текст «сплюснут» по вертикали (fb=64, физ=32): дублируем строки
  --layout rows --row-lsb-first --stride-bytes 16 --width 128 --height 32 --y-repeat 2
  # (при необходимости попробуйте MSB-first вместо LSB-first)

  # 5) Режим подбора с сеткой
  --test-grid --explore --width 128 --height 32 --dump-hex

СОБЕРИТЕ ДАННЫЕ:
  fbset -s
  cat /sys/class/graphics/fb0/{name,virtual_size}
Если line_length отличается от 16 — укажите --stride-bytes. Если экран физически 128×32, а fb=128×64, то --height 32 и --y-repeat 2 обычно дают правильные пропорции.
*/
