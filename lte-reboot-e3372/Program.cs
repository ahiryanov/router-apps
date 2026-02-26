using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
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

		var devices_raw = Enumerable.Range(1, 8)
			.Where(i => Directory.Exists($"/sys/class/net/lte{i}"))
			.Select(i => $"lte{i}")
			.ToArray();
		var devices = new List<Device>();
		foreach (var deviceName in devices_raw)
		{
			if (string.IsNullOrWhiteSpace(deviceName))
				continue;
			var device = new Device();
			device.Iface = deviceName;
			var num = Regex.Match(device.Iface, @"\d+").Value;
			device.IpAddr = $"192.168.8.10{num}";
			device.State = $"/etc/zabbix/huawei/servicestatus.sh {device.IpAddr} | awk -F'[<>]' '/ConnectionStatus/{{print $3}}'".Bash() == "901" ? "connected" : "disconnected";
			if (device.State == "connected")
			{
				int rssi;
				int.TryParse($"/etc/zabbix/huawei/signal2.sh {device.IpAddr} | sed -nE 's/.*<rssi>.*(-[0-9]+).*/\\1/p'".Bash(), out rssi);
				device.Rssi = rssi;

				device.Operator = $"/etc/zabbix/huawei/fullopsos.sh {device.IpAddr} | awk -F'[<>]' '/FullName/{{print $3}}'".Bash();
			}
			devices.Add(device);
		}
		devices = devices.OrderBy(m => m.Iface).ToList();
		logger.LogInformation($"Device count: {devices.Count()}" + $" # server ip: {_srv}" + $" # Max loss {maxLoss}, Max rtt {maxRtt}" + " # Ping " + (isPingIputils ? "iputils" : "busybox"));

		foreach (var device in devices)
		{
			//calculate ip mptcp endpoint parameters
			var endpoint = $"ip mptcp endpoint | grep {device.Iface}".Bash();
			var endpointId = !string.IsNullOrWhiteSpace(endpoint) ? endpoint.Split()[2] : null;
			var endpointIsBackup = endpoint?.Contains("backup");

			switch (device.State)
			{
				case "connected":
					//calculate route parameters
					var route = $"ip route show {_srv} dev {device.Iface}".Bash();
					var routeCount = route.Split('\n').Count();
					if (routeCount > 1)
					{
						logger.LogError($"Device {device.Iface} has {routeCount} routes. Flushing.");
						for (int i = 0; i < (routeCount - 1); i++)
						{
							$"ip route del {_srv} dev {device.Iface}".Bash();
						}
					}

					if (string.IsNullOrWhiteSpace(route))
					{
						$"ip route add {_srv} via 192.168.8.1 dev {device.Iface} metric 2000".Bash();	
					}

					var routeMetric = GetRouteMetric(route);
				
					var ping = $"ping {_srv} -I {device.Iface} -A -w 1 -q -s 1400".Bash();
					int PacketReceive = 0;
					double PacketLoss = 100;
					int AvgRtt = 10000;
					if (isPingIputils)
					{
						int.TryParse(new Regex(@"(\w+)\s" + "received").Match(ping)?.Groups[1]?.Value, out PacketReceive);
						double.TryParse(new Regex(@"([\d.,]+)%\s*packet\s+loss").Match(ping)?.Groups[1]?.Value, out PacketLoss);
						int.TryParse(new Regex("/" + @"(\d+)" + ".").Match(ping)?.Groups[1]?.Value, out AvgRtt);
					}
					else
					{
						int.TryParse(new Regex(@"(\w+)\s" + "packets received").Match(ping)?.Groups[1]?.Value, out PacketReceive);
						double.TryParse(new Regex(@"(\d+)%\s" + "packet loss").Match(ping)?.Groups[1]?.Value, out PacketLoss);
						int.TryParse(new Regex("/" + @"(\d+)" + ".").Match(ping)?.Groups[1]?.Value, out AvgRtt);
					}
					AvgRtt = AvgRtt == 0 ? 10000 : AvgRtt;

					var num = Regex.Match(device.Iface, @"\d+").Value;

					var channelState = ComputeState(PacketLoss, AvgRtt, PacketReceive);

					logger.LogInformation($"{device.Iface} {device.State}. Receive: {PacketReceive} # Loss %: {PacketLoss} # RTT ms: {AvgRtt} # State: {channelState} # {device.Operator} # RSSI: {device.Rssi}");

					if (PacketLoss > maxLoss || AvgRtt > maxRtt || device.Rssi < -80)
					{
						if (!string.IsNullOrWhiteSpace(route) && routeMetric < 1100)
						{
							$"ip route del {_srv} dev {device.Iface}".Bash();
							$"ip route add {_srv} via 192.168.8.1 dev {device.Iface} metric {routeMetric + 1100}".Bash();
						}
						$"ip mptcp endpoint change id {endpointId} backup".Bash();
						logger.LogWarning($"{device.Iface} {device.Iface} marked as BACKUP");
					}
					else
					{
						$"ip route del {_srv} dev {device.Iface}".Bash();
						$"ip route replace {_srv} via 192.168.8.1 dev {device.Iface} metric {channelState}{num}".Bash();

						if ((bool)endpointIsBackup)
						{
							$"ip mptcp endpoint del id {endpointId}".Bash();
							Thread.Sleep(500);
							$"ip mptcp endpoint add {device.IpAddr} dev {device.Iface} subflow".Bash();
							Thread.Sleep(500);
							logger.LogWarning($"{device.Iface} subflow recreated with RealIp {device.IpAddr} - no backup");
						}
						if (string.IsNullOrWhiteSpace(endpoint))
						{
							$"ip mptcp endpoint add {device.IpAddr} dev {device.Iface} subflow".Bash();
							logger.LogWarning($"{device.Iface} subflow recreated with RealIp {device.IpAddr} - no endpoint");
						}
						
					}
					break;

				case "disconnected":
					$"ip mptcp endpoint del id {endpointId}".Bash();
					logger.LogInformation($"{device.Iface} {device.State}. # {device.Operator} # RSSI: {device.Rssi}");
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

			var num = Regex.Match(ep.Dev, @"\d+").Value;
			var realModemIp = $"192.168.8.10{num}";
			logger.LogError($"Endpoint id={ep.Id} ip={ep.Ip} realIP={realModemIp} dev={ep.Dev} flags=[{string.Join(' ', ep.Flags)}] -> {(inUse ? "in use" : " NOT in use")}");
			$"ip mptcp endpoint delete id {ep.Id}".Bash();
			Thread.Sleep(500);
			$"ip mptcp endpoint add {realModemIp} dev {ep.Dev} subflow".Bash();
		}

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
		const int RttBadMs = 300;

		const int PktsMinPerSec = 2;
		const int PktsGoodPerSec = 25;

		const double WLoss = 0.55;
		const double WRtt = 0.30;
		const double WPkts = 0.15;

		packetLoss = Clamp(packetLoss, 0, 100);
		avgRttMs = Math.Max(0, avgRttMs);
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
			WRtt * sRtt +
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
	public string Iface { get; set; }
	public string State { get; set; } //901 - connected, 902 - disconnected
	public int? Rssi { get; set; }
	public string IpAddr { get; set; }
	public string Operator { get; set; }
	public override string ToString()
	{
		return $"{Iface} {State} IP {IpAddr} RSSI {Rssi} OP {Operator}";
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