using System;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

internal static class Program
{
    private sealed class Options
    {
        public string SocketPath { get; set; } = "/run/openvpn.sock";
        public int IntervalSec { get; set; } = 1;
        public int IdleSec { get; set; } = 20;
        public int CooldownSec { get; set; } = 120;
    }

    public static async Task<int> Main(string[] args)
    {
		var opt = new Options();

        Console.WriteLine($"[watchdog] sock={opt.SocketPath}, interval={opt.IntervalSec}s, idle={opt.IdleSec}s, cooldown={opt.CooldownSec}s");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        long? lastIn = null;
        double lastInChangeAt = 0;
        double nextRestartAllowedAt = 0;

        while (!cts.IsCancellationRequested)
        {
            try
            {
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                var ep = new UnixDomainSocketEndPoint(opt.SocketPath);
                await socket.ConnectAsync(ep, cts.Token);

                using var stream = new NetworkStream(socket, ownsSocket: true);
                using var reader = new StreamReader(stream);
                using var writer = new StreamWriter(stream) { AutoFlush = true };

                await writer.WriteAsync($"bytecount {opt.IntervalSec}\n");

                Console.WriteLine("[watchdog] connected, bytecount enabled");

                lastIn = null;

                while (!cts.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync();
                    if (line == null) break;

                    if (!line.StartsWith(">BYTECOUNT:", StringComparison.Ordinal))
                        continue;

                    var payload = line.Substring(">BYTECOUNT:".Length);
                    var comma = payload.IndexOf(',');
                    if (comma <= 0) continue;

                    var inStr = payload.Substring(0, comma).Trim();
                    if (!long.TryParse(inStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var inBytes))
                        continue;

                    var now = MonotonicSeconds();
                    if (lastIn is null)
                    {
                        lastIn = inBytes;
                        lastInChangeAt = now;
                        continue;
                    }

                    if (inBytes > lastIn.Value)
                    {
                        lastIn = inBytes;
                        lastInChangeAt = now;
                        continue;
                    }

                    var idleFor = now - lastInChangeAt;
                    if (idleFor >= opt.IdleSec && now >= nextRestartAllowedAt)
                    {
                        Console.WriteLine($"[watchdog] IN idle {idleFor:F1}s -> send SIGUSR1");
                        await writer.WriteAsync("signal SIGUSR1\n");

                        nextRestartAllowedAt = now + opt.CooldownSec;

                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (FileNotFoundException)
            {
                Console.Error.WriteLine($"[watchdog] socket not found: {opt.SocketPath} (retry...)");
            }
            catch (SocketException ex)
            {
                Console.Error.WriteLine($"[watchdog] socket error: {ex.Message} (retry...)");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[watchdog] error: {ex.GetType().Name}: {ex.Message} (retry...)");
            }

            await Task.Delay(500, cts.Token).ContinueWith(_ => { }, CancellationToken.None);
        }

        Console.WriteLine("[watchdog] stopped");
        return 0;
    }

    private static double MonotonicSeconds()
        => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}

