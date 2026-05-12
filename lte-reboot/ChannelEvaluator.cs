using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace lte_reboot;

internal record RouterContext(
	bool IsIdle,
	double? TunnelKbps,
	string TunnelIface,
	Dictionary<string, double> PerModemKbps,
	double MaxTxKbps
);

internal record ChannelDecision(bool ShouldBackup, string Reason);

internal static class ChannelEvaluator
{
	private const int IdleThresholdKbps = 5000;
	private const double OutlierRatio = 0.20;
	private const int BadCycles = 2;
	private const int BaseGoodCycles = 3;
	private const int MaxQuarantinePenalty = 7;
	private const int HistoryWindow = 30;
	private const int HardLossPct = 25;
	private const int HardLossLoadedPct = 50;
	private const int HardRttMs = 250;
	private const int LoadedTxThresholdKbps = 1000;

	public static RouterContext BuildContext(RouterThroughput tp)
	{
		bool isIdle = tp.TunnelKbps.HasValue && tp.TunnelKbps.Value < IdleThresholdKbps;
		var perModemKbps = tp.PerModemKbps ?? new Dictionary<string, double>();
		double maxTx = perModemKbps.Values.DefaultIfEmpty(0).Max();
		return new RouterContext(isIdle, tp.TunnelKbps, tp.TunnelIface, perModemKbps, maxTx);
	}

	public static ChannelDecision Decide(
		Device device,
		double pingLossPct,
		int pingRttMs,
		RouterContext ctx,
		ChannelState history,
		ILogger logger)
	{
		ctx.PerModemKbps.TryGetValue(device.Iface, out double modemKbps);
		bool loaded = modemKbps > LoadedTxThresholdKbps;
		int lossThreshold = loaded ? HardLossLoadedPct : HardLossPct;

		string hardReason = null;
		if (device.MobileMode != "LTE" && device.MobileMode != "Unknown") hardReason = $"mode={device.MobileMode}";
		else if (pingLossPct > lossThreshold) hardReason = $"hardLoss={pingLossPct:F0}%{(loaded ? " loaded" : "")}";
		else if (!loaded && pingRttMs > HardRttMs) hardReason = $"hardRtt={pingRttMs}ms";
		else if (!loaded && pingLossPct > AppConfig.MaxLoss && pingRttMs > AppConfig.MaxRtt) hardReason = $"loss+rtt={pingLossPct:F0}%/{pingRttMs}ms";

		string softReason = null;
		if (hardReason == null && !ctx.IsIdle && !history.LastBackup)
		{
			if (modemKbps > 0 && ctx.MaxTxKbps > 0
				&& modemKbps < OutlierRatio * ctx.MaxTxKbps)
			{
				softReason = $"txOutlier {modemKbps / 1000.0:F2}/{ctx.MaxTxKbps / 1000.0:F2}Mbps";
			}
		}

		bool bad = hardReason != null || softReason != null;
		if (bad)
		{
			history.ConsecutiveGood = 0;
			history.ConsecutiveBad++;
		}
		else
		{
			history.ConsecutiveBad = 0;
			history.ConsecutiveGood++;
		}

		int badCount = history.RecentBad.Count(b => b);
		int requiredGood = BaseGoodCycles + System.Math.Min(MaxQuarantinePenalty, badCount);

		string flipReason;
		bool decision;
		if (bad && history.ConsecutiveBad >= BadCycles)
		{
			decision = true;
			flipReason = hardReason ?? softReason;
		}
		else if (!bad && history.ConsecutiveGood >= requiredGood)
		{
			decision = false;
			flipReason = "good";
		}
		else
		{
			decision = history.LastBackup;
			flipReason = bad
				? $"pending-bad ({hardReason ?? softReason}) {history.ConsecutiveBad}/{BadCycles}"
				: $"pending-good {history.ConsecutiveGood}/{requiredGood}";
		}

		history.RecentBad.Enqueue(bad);
		while (history.RecentBad.Count > HistoryWindow)
			history.RecentBad.Dequeue();

		logger.LogInformation($"decide {device.Iface} bad={bad} consec={history.ConsecutiveBad}/{history.ConsecutiveGood} badCnt={badCount}/{history.RecentBad.Count} reqGood={requiredGood} -> {(decision ? "BACKUP" : "PRIMARY")} ({flipReason})");
		history.LastBackup = decision;
		return new ChannelDecision(decision, flipReason);
	}
}
