using System;
using System.IO;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Threading;

class Program
{
	static int WIDTH = 128;
	static int HEIGHT = 32;
	static bool[,] pix = new bool[WIDTH, HEIGHT];
	static string devicePath = "/dev/fb0";
	static string text = "SZ-IMC";
	static int startX = 3, startY = 10;

	// упаковка
	static int strideBytes = 16;        // авто: rows→WIDTH/8, pages→WIDTH
	static int yRepeat = 2;             // повторять каждую строку N раз (для 128x64 fb на физ. 128x32 → N=2)
	static PSF2 font = new PSF2();
	static string fontPath = "font-14n.ttf";

	// ===== NEW: daemon/watch options =====
	static bool daemon = false;
	static string ifName = "tun1";
	static int ipSeconds = 5;
	static int statusSeconds = 5;
	static string onlineText = "DC: ONLINE";
	static string offlineText = "DC: OFFLINE";
	// graceful exit
	static volatile bool quit = false;

	static void Main(string[] args)
	{
		try
		{
			ParseArgs(args);
			font = LoadPSF2(fontPath);

			Console.CancelKeyPress += (s, e) => { quit = true; e.Cancel = true; };
			AppDomain.CurrentDomain.ProcessExit += (s, e) => { quit = true; };

			if (daemon)
			{
				RunDaemonLoop();
			}
			else
			{
				RenderText(startX, startY, text);
				WriteFb(PackRows());
			}
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine("[ERROR] " + ex.Message);
			Environment.ExitCode = 1;
		}
	}

	// ===== Daemon loop =====
	static void RunDaemonLoop()
	{
		while (!quit)
		{
			string ip = GetIPv4OfInterface(ifName);
			string host = GetSystemHostname(1000);
			if (!string.IsNullOrEmpty(ip))
			{
				Clear();
				RenderCentered(host, ip);
				WriteFb(PackRows());
			}

			SleepWithQuit(ipSeconds);
			if (quit) break;

			bool online = !string.IsNullOrEmpty(ip);
			string st = online ? onlineText : offlineText;

			Clear();
			RenderCentered(host, st);
			WriteFb(PackRows());

			SleepWithQuit(statusSeconds);
		}
	}

	// ===== Networking helpers =====
	static string GetIPv4OfInterface(string ifname)
	{
		try
		{
			foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
			{
				if (!string.Equals(ni.Name, ifname, StringComparison.Ordinal) &&
				!ni.Description.Contains(ifname) &&
				!ni.Id.Contains(ifname))
					continue;


				var props = ni.GetIPProperties();
				foreach (var ua in props.UnicastAddresses)
				{
					if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
						return ua.Address.ToString();
				}
			}
		}
		catch { }
		// Fallback: call `ip -4 addr show dev tun1`
		try
		{
			string outp = Exec("/sbin/ip", $"-4 addr show dev {ifname}");
			var m = Regex.Match(outp, "\\binet\\s+(\\d+\\.\\d+\\.\\d+\\.\\d+)");
			if (m.Success) return m.Groups[1].Value;
		}
		catch { }
		return null;
	}

	static void RenderCentered(string s1, string s2)
	{
		int w1 = ((int)font.width + 1) * s1.Length - 1;
		int x1 = Math.Max(0, (WIDTH - w1) / 2);
		int y1 = 3;
		RenderText(x1, y1, s1);

		int w2 = ((int)font.width + 1) * s2.Length - 1;
		int x2 = Math.Max(0, (WIDTH - w2) / 2);
		int y2 = 19;
		RenderText(x2, y2, s2);
	}


	static void SleepWithQuit(int seconds)
	{
		int left = seconds * 10;
		while (left-- > 0 && !quit) Thread.Sleep(100);
	}

	static void WriteFb(byte[] frame)
	{
		using var fs = new FileStream(devicePath, FileMode.Open, FileAccess.Write);
		fs.Write(frame, 0, frame.Length);
		fs.Flush(true);
	}

	struct PSF2
	{
		public uint magic, version, headerSize, flags, glyphCount, bytesPerGlyph, height, width;
		public byte[] glyphs; // подряд
	}

	static PSF2 LoadPSF2(string path)
	{
		using var fs = File.OpenRead(path);
		using var br = new BinaryReader(fs);
		var h = new PSF2
		{
			magic = br.ReadUInt32(),
			version = br.ReadUInt32(),
			headerSize = br.ReadUInt32(),
			flags = br.ReadUInt32(),
			glyphCount = br.ReadUInt32(),
			bytesPerGlyph = br.ReadUInt32(),
			height = br.ReadUInt32(),
			width = br.ReadUInt32()
		};
		if (h.magic != 0x864ab572) throw new Exception("Not TTF");
		fs.Seek(h.headerSize, SeekOrigin.Begin);
		h.glyphs = br.ReadBytes((int)(h.glyphCount * h.bytesPerGlyph));
		return h;
	}

	static void DrawCharPSF(PSF2 f, char ch, int x0, int y0)
	{
		int idx = (byte)ch; // для ASCII
		int w = (int)f.width, h = (int)f.height, bpg = (int)f.bytesPerGlyph;
		int pitch = (w + 7) / 8;
		int off = idx * bpg;
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				int byteIndex = off + y * pitch + (x >> 3);
				int bit = 7 - (x & 7); // в PSF2 биты слева направо (MSB-first)
				bool on = (f.glyphs[byteIndex] & (1 << bit)) != 0;
				int X = x0 + x, Y = y0 + y;
				if ((uint)X < WIDTH && (uint)Y < HEIGHT) pix[X, Y] = on;
			}
		}
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
				case "--font": fontPath = args[++i]; break;
				case "--x": startX = int.Parse(args[++i]); break;
				case "--y": startY = int.Parse(args[++i]); break;
				// упаковка/раскладка
				case "--stride-bytes": strideBytes = int.Parse(args[++i]); break;
				case "--yscale": yRepeat = Math.Max(1, int.Parse(args[++i])); break;
				case "--daemon": daemon = true; break;
				default:
					Console.WriteLine("Неизвестный ключ: " + a);
					break;
			}
		}
	}

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

	static void RenderText(int x, int y, string s)
	{
		int cx = x;
		int advX = (int)font.width;
		int advLn = (int)font.height;   // вертикальный интервал

		foreach (char ch in s)
		{
			if (ch == '\n') { y += advLn; cx = x; continue; }
			DrawCharPSF(font, ch, cx, y);
			cx += advX;
			if (cx >= WIDTH) break;
		}
	}

	static string GetSystemHostname(int execTimeoutMs = 1000)
	{
		// 1) /proc/sys/kernel/hostname
		if (TryReadTrimmed("/proc/sys/kernel/hostname", out var h) && !string.IsNullOrEmpty(h))
			return h;

		// 2) /etc/hostname
		if (TryReadTrimmed("/etc/hostname", out h) && !string.IsNullOrEmpty(h))
			return h;

		// 3) .NET fallbacks
		try { h = Environment.MachineName; if (!string.IsNullOrEmpty(h)) return h.Trim(); } catch { }

		return "unknown";
	}

	static bool TryReadTrimmed(string path, out string value)
	{
		try
		{
			if (File.Exists(path))
			{
				value = File.ReadAllText(path).Trim();
				if (!string.IsNullOrEmpty(value)) return true;
			}
		}
		catch { }
		value = null!;
		return false;
	}

	static void Clear()
	{
		for (int x = 0; x < WIDTH; x++)
			for (int y = 0; y < HEIGHT; y++)
				pix[x, y] = false;
	}

	static string Exec(string file, string args)
	{
		var psi = new ProcessStartInfo(file, args)
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};
		using var p = Process.Start(psi);
		string output = p.StandardOutput.ReadToEnd();
		p.WaitForExit(2000);
		return output;
	}
}