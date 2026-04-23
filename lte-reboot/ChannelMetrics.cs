using System;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace lte_reboot;

internal static class ChannelMetrics
{
	public static (int PacketReceive, double PacketLoss, int AvgRtt) ParsePing(string pingOutput, bool isIputils)
	{
		int packetReceive = 0;
		double packetLoss = 100;
		int avgRtt = 10000;
		if (isIputils)
		{
			int.TryParse(new Regex(@"(\w+)\s" + "received").Match(pingOutput)?.Groups[1]?.Value, out packetReceive);
			double.TryParse(new Regex(@"([\d.,]+)%\s*packet\s+loss").Match(pingOutput)?.Groups[1]?.Value, out packetLoss);
			int.TryParse(new Regex("/" + @"(\d+)" + ".").Match(pingOutput)?.Groups[1]?.Value, out avgRtt);
		}
		else
		{
			int.TryParse(new Regex(@"(\w+)\s" + "packets received").Match(pingOutput)?.Groups[1]?.Value, out packetReceive);
			double.TryParse(new Regex(@"(\d+)%\s" + "packet loss").Match(pingOutput)?.Groups[1]?.Value, out packetLoss);
			int.TryParse(new Regex("/" + @"(\d+)" + ".").Match(pingOutput)?.Groups[1]?.Value, out avgRtt);
		}
		avgRtt = avgRtt == 0 ? 10000 : avgRtt;
		return (packetReceive, packetLoss, avgRtt);
	}

	public static string GetRouteWithFlush(string iface, string deviceName, ILogger logger)
	{
		var route = $"ip route show {AppConfig.Srv} dev {iface}".Bash();
		var routeCount = route.Split('\n').Length;
		if (routeCount > 1)
		{
			logger.LogError($"Device {deviceName} has {routeCount} routes. Flushing.");
			for (int i = 0; i < (routeCount - 1); i++)
				$"ip route del {AppConfig.Srv} dev {iface}".Bash();
		}
		return route;
	}

	public static int GetRouteMetric(string route)
	{
		var m = Regex.Match(route ?? string.Empty, @"\bmetric\s+(\d+)\b");
		if (m.Success && int.TryParse(m.Groups[1].Value, out var metric))
			return metric;
		return 0;
	}

	public static int ComputeState(double packetLoss, int avgRttMs, int receivedPackets, SubflowMetrics ss = null)
	{
		const int RttGoodMs = 50;
		const int RttBadMs = 200;
		const int PktsMinPerSec = 2;
		const int PktsGoodPerSec = 25;

		packetLoss = Clamp(packetLoss, 0, 100);
		avgRttMs = Math.Max(0, avgRttMs);
		receivedPackets = Math.Max(0, receivedPackets);

		if (receivedPackets == 0 || packetLoss == 100)
			return 99;

		double sLoss = packetLoss;

		double sPingRtt = Clamp(100.0 * (avgRttMs - RttGoodMs) / (RttBadMs - RttGoodMs), 0.0, 100.0);

		double sPkts;
		if (receivedPackets >= PktsGoodPerSec) sPkts = 0.0;
		else if (receivedPackets <= PktsMinPerSec) sPkts = 100.0;
		else sPkts = 100.0 * (PktsGoodPerSec - receivedPackets) / (PktsGoodPerSec - PktsMinPerSec);

		double state100;
		if (ss != null)
		{
			double sSsRtt = Clamp(100.0 * (ss.RttMs - RttGoodMs) / (RttBadMs - RttGoodMs), 0.0, 100.0);
			// variance/rtt ratio: <15% — stable, >50% — unstable
			double sVar = Clamp(100.0 * (ss.StabilityRatio - 0.15) / (0.50 - 0.15), 0.0, 100.0);
			state100 = 0.40 * sLoss + 0.15 * sPingRtt + 0.15 * sSsRtt + 0.15 * sVar + 0.15 * sPkts;
		}
		else
		{
			state100 = 0.45 * sLoss + 0.30 * sPingRtt + 0.25 * sPkts;
		}

		return Math.Max(0, Math.Min(99, (int)Math.Round(state100)));
	}

	private static double Clamp(double v, double lo, double hi) => (v < lo) ? lo : (v > hi) ? hi : v;
}
