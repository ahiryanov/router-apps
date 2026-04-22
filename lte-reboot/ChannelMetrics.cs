using System;
using System.Text.RegularExpressions;

namespace lte_reboot;

internal static class ChannelMetrics
{
	public static int GetRouteMetric(string route)
	{
		var m = Regex.Match(route ?? string.Empty, @"\bmetric\s+(\d+)\b");
		if (m.Success && int.TryParse(m.Groups[1].Value, out var metric))
			return metric;
		return 0;
	}

	public static int ComputeState(double packetLoss, int avgRttMs, int receivedPackets)
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
}
