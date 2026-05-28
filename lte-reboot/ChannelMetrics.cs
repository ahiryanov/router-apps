using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace lte_reboot;

internal static class ChannelMetrics
{
	public static (int PacketReceive, double PacketLoss, int MedianRtt) ParsePing(string pingOutput, bool isIputils)
	{
		int packetReceive = 0;
		double packetLoss = 100;
		if (isIputils)
		{
			int.TryParse(new Regex(@"(\w+)\s" + "received").Match(pingOutput)?.Groups[1]?.Value, out packetReceive);
			double.TryParse(new Regex(@"([\d.,]+)%\s*packet\s+loss").Match(pingOutput)?.Groups[1]?.Value, out packetLoss);
		}
		else
		{
			int.TryParse(new Regex(@"(\w+)\s" + "packets received").Match(pingOutput)?.Groups[1]?.Value, out packetReceive);
			double.TryParse(new Regex(@"(\d+)%\s" + "packet loss").Match(pingOutput)?.Groups[1]?.Value, out packetLoss);
		}
		return (packetReceive, packetLoss, MedianRtt(pingOutput));
	}

	// Медиана RTT по отдельным пакетам — гасит warm-up выбросы при пробуждении радио из RRC idle.
	private static int MedianRtt(string pingOutput)
	{
		var times = new List<double>();
		foreach (Match m in Regex.Matches(pingOutput, @"time[=<]\s*([\d.]+)"))
			if (double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
				times.Add(t);

		if (times.Count == 0)
			return 10000;

		times.Sort();
		int n = times.Count;
		double median = (n % 2 == 1) ? times[n / 2] : (times[n / 2 - 1] + times[n / 2]) / 2.0;
		return (int)Math.Round(median);
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

	public static int ComputeState(double packetLoss, int medianRttMs, int receivedPackets, SubflowMetrics ss = null)
	{
		const int RttGoodMs = 50;
		const int RttBadMs = 200;

		packetLoss = Clamp(packetLoss, 0, 100);
		medianRttMs = Math.Max(0, medianRttMs);

		if (receivedPackets == 0 || packetLoss == 100)
			return 99;

		double sLoss = packetLoss;
		double sPingRtt = Clamp(100.0 * (medianRttMs - RttGoodMs) / (RttBadMs - RttGoodMs), 0.0, 100.0);

		double state100;
		if (ss != null)
		{
			double sSsRtt = Clamp(100.0 * (ss.RttMs - RttGoodMs) / (RttBadMs - RttGoodMs), 0.0, 100.0);
			double sVar = Clamp(100.0 * (ss.StabilityRatio - 0.15) / (0.50 - 0.15), 0.0, 100.0);
			state100 = 0.55 * sLoss + 0.10 * sPingRtt + 0.25 * sSsRtt + 0.10 * sVar;
		}
		else
		{
			state100 = 0.70 * sLoss + 0.30 * sPingRtt;
		}

		return Math.Max(0, Math.Min(99, (int)Math.Round(state100)));
	}

	private static double Clamp(double v, double lo, double hi) => (v < lo) ? lo : (v > hi) ? hi : v;
}
