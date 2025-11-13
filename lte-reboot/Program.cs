using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace lte_reboot;
class Program
{
    private static string _srv; 
    private static int maxRtt = 300;
    private static int maxLoss = 25;
    private const int _restartCount = 20;
    private const string _logFile = "/tmp/lte";
    static void Main(string[] args)
    {
		using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder
                .AddSystemdConsole();
        });
        ILogger logger = loggerFactory.CreateLogger("main");
		//### SRV detect ####
		if (File.Exists("/etc/openvpn/client/client.conf"))
            _srv = "cat /etc/openvpn/client/client.conf | grep \"^remote \" | awk '{{print $2}}'".Bash();
		//### Startup checks ####
		CheckMultipleEndpoints(logger);
		RecreateDeadSubflow(logger);

        if (args.Length == 2)
        {
            int.TryParse(args[0], out maxLoss);
            int.TryParse(args[1], out maxRtt);
        }
        
		bool isPingIputils = $"ping -V".Bash().Contains("iputils");
        var devices = new List<Device>();
        var devices_raw = "nmcli -f DEVICE,STATE -t device".Bash().Split('\r', '\n');

        foreach (var device_raw in devices_raw)
        {
            if (string.IsNullOrWhiteSpace(device_raw))
                continue;
            var device = new Device();
            device.Name = device_raw.Split(":")[0];
            device.State = device_raw.Split(":")[1];
            if (device.Name.Contains("cdc-wdm"))
            {
                device.Iface = $"qmicli --silent -d /dev/{device.Name} --get-wwan-iface".Bash().Replace("\n", "");
				var qmicliOutput = $"qmicli -p -d /dev/{device.Name} --nas-get-signal-info".Bash();
				if (TryParseRssi(qmicliOutput, out var rssi)) 
				{
					device.Rssi = rssi;
				}
				device.MobileMode = ParseMobileMode(qmicliOutput);
                devices.Add(device);
            }
        }
        logger.LogInformation($"Device count: {devices.Count}" + $" # server ip: {_srv}" + $" # Max loss {maxLoss}, Max rtt {maxRtt}" + " # Ping "+(isPingIputils?"iputils":"busybox"));
        devices = devices.OrderBy(m => m.Name).ToList();

        foreach (var device in devices)
        {
            switch (device.State)
            {
                case "connected":
					var ping = $"ping {_srv} -I {device.Iface} -A -w 1 -q -s 1400".Bash();
					int PacketReceive = 0;
					double PacketLoss = 100;
					int AvgRtt = 10000;
					if (isPingIputils)
					{
						int.TryParse(new Regex(@"(\w+)\s" + "received").Match(ping).Groups[1].Value, out PacketReceive);
						double.TryParse(new Regex(@"([\d.,]+)%\s*packet\s+loss").Match(ping).Groups[1].Value, out PacketLoss);
						int.TryParse(new Regex("/" + @"(\d+)" + ".").Match(ping).Groups[1].Value, out AvgRtt);
					}
					else
					{
						int.TryParse(new Regex(@"(\w+)\s" + "packets received").Match(ping).Groups[1].Value, out PacketReceive);
						double.TryParse(new Regex(@"(\d+)%\s" + "packet loss").Match(ping).Groups[1].Value, out PacketLoss);
						int.TryParse(new Regex("/" + @"(\d+)" + ".").Match(ping).Groups[1].Value, out AvgRtt);
					}
					AvgRtt = AvgRtt == 0 ? 10000 : AvgRtt;

					//calculate ip mptcp endpoint parameters
					var endpoint = $"ip mptcp endpoint | grep {device.Iface}".Bash();
					var endpointId = endpoint?.Split()?[2];
					var endpointAddr = endpoint?.Split()?.First();
					var endpointIsBackup = endpoint?.Contains("backup");
					//---------------------------------------------

					//calculate route parameters
					var route = $"ip route show {_srv} dev {device.Iface}".Bash();
					var routeCount = route.Split('\n').Count();
					if (routeCount > 1)
					{
						logger.LogError($"Device {device.Name} has {routeCount} routes. Flushing.");
						for (int i = 0; i < (routeCount - 1); i++)
						{
							$"ip route del {_srv} dev {device.Iface}".Bash();
						}
					}
					var routeMetric = GetRouteMetric(route);
					//---------------------------------------------
					var num =  Regex.Match(device.Iface, @"\d+").Value;

					var channelState = ComputeState(PacketLoss,AvgRtt,PacketReceive);

                    logger.LogInformation($"{device.Name} ({device.Iface}) state {device.State}. Receive: {PacketReceive} # Loss %: {PacketLoss} # RTT ms: {AvgRtt} # ChannelState: {channelState} # RSSI: {device.Rssi} # Mode: {device.MobileMode}");

					if (PacketLoss > maxLoss || AvgRtt > maxRtt || device.Rssi! < -80 || (device.MobileMode != "LTE" && device.MobileMode != "Unknown"))
					{
						if (!string.IsNullOrWhiteSpace(route) && routeMetric < 1100)
						{
							$"ip route del {_srv} dev {device.Iface}".Bash();
							$"ip route add {_srv} dev {device.Iface} metric {routeMetric + 1100}".Bash();
						}
						$"ip mptcp endpoint change id {endpointId} backup".Bash();
						logger.LogWarning($"{device.Name} {device.Iface} marked as BACKUP");
					}
					else
					{
						$"ip route del {_srv} dev {device.Iface}".Bash();
						$"ip route replace {_srv} dev {device.Iface} metric {channelState}{num}".Bash();

						if ((bool)endpointIsBackup)
						{
							$"ip mptcp endpoint del id {endpointId}".Bash();
							Thread.Sleep(500);
							$"ip mptcp endpoint add {endpointAddr} dev {device.Iface} subflow".Bash();
							Thread.Sleep(500);
							logger.LogWarning($"{device.Name} {device.Iface} subflow recreated");
						}
						if (string.IsNullOrWhiteSpace(route))
						{
							var refreshRouteIsUp = ConnectionUp(device.Name, logger);
							if (!refreshRouteIsUp)
							{
								logger.LogError($"Refresh route failed {device.Name} ({device.Iface})");
								ConnectionDown(device.Name,logger);
							}
							else
								logger.LogWarning($"Refresh route success {device.Name} ({device.Iface})");
						}
					}
                    break;

                case "connecting (prepare)":
                    ConnectionDown(device.Name,logger);
                    Thread.Sleep(2000);
                    var prepareIsUp = ConnectionUp(device.Name, logger);
                    if (!prepareIsUp)
                    {
                        logger.LogWarning($"Failed activation of connecting (prepare) {device.Name}");
                        ConnectionDown(device.Name,logger);
                    }
                    else
                        logger.LogWarning($"Success activation of connecting (prepare) {device.Name}");
                    break;

                case "disconnected":
                    var disconnectIsUp = ConnectionUp(device.Name, logger);
                    if (!disconnectIsUp)
                    {
                        logger.LogWarning($"Activation failed of disconnected {device.Name}");
                        ConnectionDown(device.Name,logger);
                    }
                    else
                        logger.LogWarning($"Success activation of disconnected {device.Name}");
                    break;

                case "unavailable":
                    logger.LogInformation($"{device.Name} ({device.Iface}) state {device.State}");
                    ConnectionUp(device.Name, logger);
                    break;
            }
        }
    }

	static void CheckMultipleEndpoints(ILogger logger)
	{
		var endpoints = $"ip mptcp endpoint".Bash();
		var lines = endpoints
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .ToList();
		var rx = new Regex(@"\bid\s+(\d+).*?\bdev\s+(\S+)", RegexOptions.IgnoreCase);
		var parsed = new List<(int Id, string Dev, string Line)>();
		foreach (var line in lines)
        {
            var m = rx.Match(line);
            if (!m.Success) continue;
            int id = int.Parse(m.Groups[1].Value);
            string dev = m.Groups[2].Value;
            parsed.Add((id, dev, line));
        }
		var groups = parsed.GroupBy(p => p.Dev, StringComparer.Ordinal);
		foreach (var g in groups)
        {
			if (g.Key.StartsWith("if"))
			{
				logger.LogError($"Remove orphaned endpoints dev: {g.Key}");
				foreach (var e in g)
				{
					$"ip mptcp endpoint delete id {e.Id}".Bash();
				}
			}

            if (g.Count() <= 1) continue;

            int maxId = g.Max(x => x.Id);
            logger.LogError($"{g.Key}: has {g.Count()} endpoints. Leave id={maxId}, delete rest.");

            foreach (var e in g.Where(x => x.Id != maxId).OrderBy(x => x.Id))
            {
                $"ip mptcp endpoint delete id {e.Id}".Bash();
            }
        }
	}

	static void RemoveDeadEndpoint(string iface, ILogger logger)
	{
		var endpoint = $"ip mptcp endpoint | grep {iface}".Bash();
		var rx = new Regex(@"\bid\s+(\d+).*?\bdev\s+(\S+)", RegexOptions.IgnoreCase);
		var m = rx.Match(endpoint);
		if (m.Success)
		{
			var id = m.Groups[1].Value;
			var dev = m.Groups[2].Value;
			$"ip mptcp endpoint delete id {id}".Bash();
			logger.LogWarning($"Remove dead endpoint {dev} id {id}");
		}
	}

	static void RecreateDeadSubflow(ILogger logger)
	{
		var endpointsRaw = $"ip mptcp endpoint".Bash();
		var endpoints = ParseEndpoints(endpointsRaw);
		var activeEndpoints = endpoints.Where(e => !e.IsBackup).ToList();
		var localIpInUse = GetSsIp(_srv);

		foreach (var ep in activeEndpoints)
        {
            bool inUse = localIpInUse.Contains(ep.Ip);
            if (inUse) continue;
			logger.LogError($"Endpoint id={ep.Id} ip={ep.Ip} dev={ep.Dev} flags=[{string.Join(' ', ep.Flags)}] -> {(inUse ? "in use" : " NOT in use")}");
            $"ip mptcp endpoint delete id {ep.Id}".Bash();
			Thread.Sleep(500);
            $"ip mptcp endpoint add {ep.Ip} dev {ep.Dev} subflow".Bash();
        }

	}

	static string ConnectionDown(string deviceName, ILogger logger)
    {
		RemoveDeadEndpoint(deviceName,logger);
        return $"nmcli -w 10 connection down {deviceName}-conn".Bash();
    }
    static bool ConnectionUp(string deviceName, ILogger logger)
    {
        string resetModemlog = $"{_logFile}-{deviceName}-reset";
        bool successUp = true;
        var response = $"nmcli -w 10 connection up {deviceName}-conn".Bash();
        if (response.ToLower().Contains("failed") || response.ToLower().Contains("timeout"))
        {
            successUp = false;
            if (!File.Exists(resetModemlog))
                File.WriteAllText(resetModemlog, "1");
            else
            {
                int currentCount;
                try
                {
                    currentCount = Convert.ToInt32(File.ReadAllText(resetModemlog));
                }
                catch
                {
                    File.WriteAllText(resetModemlog, "1");
                    currentCount = 1;
                }
                if (currentCount > _restartCount) //power reboot modem every {restartCount} times of reconnect
                {
                    File.WriteAllText(resetModemlog, "1");
                    $"qmicli -p -d /dev/{deviceName} --dms-set-operating-mode=reset".Bash();
                    logger.LogError($"{deviceName} POWER REBOOT");
                }
                else
                {
                    if (currentCount == 7) //sim reboot in the middle of cycle
                    {
                        $"qmicli -p -d /dev/{deviceName} --uim-sim-power-off=1".Bash();
                        Thread.Sleep(1500);
                        $"qmicli -p -d /dev/{deviceName} --uim-sim-power-on=1".Bash();
                        logger.LogError($"{deviceName} SIM REBOOT");
                    }
                    currentCount++;
                    File.WriteAllText(resetModemlog, currentCount.ToString());
                }
            }
        }
        else
        {
            File.WriteAllText(resetModemlog, "1");
        }
        return successUp;
    }

	public static bool TryParseRssi(string qmicliOutput, out int rssi)
    {
		
        rssi = 0;
        if (string.IsNullOrWhiteSpace(qmicliOutput))
            return false;

        var m = Regex.Match(qmicliOutput,
            @"\bRSSI:\s*'?\s*(?<v>[-+]?\d+)\s*dBm",
            RegexOptions.IgnoreCase);

        if (!m.Success) return false;
        return int.TryParse(m.Groups["v"].Value, out rssi);
    }

	public static string ParseMobileMode(string qmicliOutput)
    {
        if (string.IsNullOrWhiteSpace(qmicliOutput) || !qmicliOutput.Contains("Successfully"))
			return "Unknown";

		return qmicliOutput.Split('\r', '\n')[1].Replace(":", "");
    }

	static int GetRouteMetric(string route)
	{
		var m = Regex.Match(route ?? string.Empty, @"\bmetric\s+(\d+)\b");
    	if (m.Success && int.TryParse(m.Groups[1].Value, out var metric))
        	return metric;
    	return 0;
	}

	static int ComputeState(double packetLoss, int avgRttMs, int receivedPackets)
    {
        const int RttGoodMs = 50;
        const int RttBadMs  = 300;

        const int PktsMinPerSec  = 2;
        const int PktsGoodPerSec = 25;

        const double WLoss = 0.55;
        const double WRtt  = 0.30;
        const double WPkts = 0.15;

        packetLoss     = Clamp(packetLoss, 0, 100);
        avgRttMs       = Math.Max(0, avgRttMs);
        receivedPackets = Math.Max(0, receivedPackets);

        if (receivedPackets == 0 || packetLoss == 100)
            return 99;

        double sLoss = packetLoss;

        double sRtt = 100.0 * (avgRttMs - RttGoodMs) / (RttBadMs - RttGoodMs);
        sRtt = Clamp(sRtt, 0.0, 100.0);

        double sPkts;
        if (receivedPackets >= PktsGoodPerSec) sPkts = 0.0;
        else if (receivedPackets <= PktsMinPerSec) sPkts = 100.0;
        else
            sPkts = 100.0 * (PktsGoodPerSec - receivedPackets) / (PktsGoodPerSec - PktsMinPerSec);

        double state100 =
            WLoss * sLoss +
            WRtt  * sRtt  +
            WPkts * sPkts;

        int returnState = (int)Math.Round(state100);
        returnState = Math.Max(0, Math.Min(99, returnState));
        return returnState;
    }
    private static double Clamp(double v, double lo, double hi) => (v < lo) ? lo : (v > hi) ? hi : v;


	private record Endpoint(int Id, string Ip, string Dev, bool IsBackup, IReadOnlyList<string> Flags, string RawTail);

    private static List<Endpoint> ParseEndpoints(string text)
    {
        var list = new List<Endpoint>();
        if (string.IsNullOrWhiteSpace(text)) return list;

        var lineRx = new Regex(@"^\s*(?<ip>\d{1,3}(?:\.\d{1,3}){3})\s+id\s+(?<id>\d+)\s*(?<tail>.*)$",
                               RegexOptions.IgnoreCase | RegexOptions.Multiline);

        foreach (Match m in lineRx.Matches(text))
        {
            string ip = m.Groups["ip"].Value;
            int id = int.Parse(m.Groups["id"].Value);
            string tail = (m.Groups["tail"].Value ?? "").Trim();

            var devRx = new Regex(@"\bdev\s+(?<dev>\S+)", RegexOptions.IgnoreCase);
            var devMatch = devRx.Match(tail);
            if (!devMatch.Success) continue;
            string dev = devMatch.Groups["dev"].Value;

            string flagsPart = tail;
            int devIdx = flagsPart.IndexOf(devMatch.Value, StringComparison.OrdinalIgnoreCase);
            if (devIdx >= 0) flagsPart = flagsPart.Substring(0, devIdx).Trim();
            var flags = flagsPart.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                                 .Select(f => f.ToLowerInvariant())
                                 .ToList();

            bool isBackup = flags.Contains("backup");

            list.Add(new Endpoint(id, ip, dev, isBackup, flags, tail));
        }
        return list;
    }

	private static HashSet<string> GetSsIp(string srv)
    {
		var ssOut = $"ss -tHn state established dst {srv}".Bash();
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(ssOut)) return set;
        var rx = new Regex(@"^\s*\d+\s+\d+\s+(?<local>\S+)\s+(?<peer>\S+)", RegexOptions.Multiline);

        foreach (Match m in rx.Matches(ssOut))
        {
            string local = m.Groups["local"].Value;
            string ip = TrimToIp(local);
            if (!string.IsNullOrEmpty(ip))
                set.Add(ip);
        }
        return set;
    }

	private static string TrimToIp(string endpoint)
    {
        int pct = endpoint.IndexOf('%');
        int colon = endpoint.IndexOf(':');
        int cut = -1;
        if (pct >= 0 && colon >= 0) cut = Math.Min(pct, colon);
        else if (pct >= 0) cut = pct;
        else if (colon >= 0) cut = colon;
        return (cut >= 0 ? endpoint.Substring(0, cut) : endpoint).Trim();
    }
}

class Device
{
    public string Name { get; set; }
    public string State { get; set; }
    public string Iface { get; set; }
	public int Rssi { get; set; }
	public string MobileMode { get; set; }
    public override string ToString()
    {
        return $"{Name} {State} {Iface} RSSI {Rssi} Mode {MobileMode}";
    }
}


public static class ShellHelper
{
    public static string Bash(this string cmd)
    {
        string escapedArgs = cmd.Replace("\"", "\\\"");

        Process process = new Process()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"{escapedArgs} 2>&1\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };

        process.Start();
        string result = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        return result.Trim();
    }
}