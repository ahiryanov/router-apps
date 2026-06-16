using System.Collections.Generic;
using System.Linq;

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
	private const double OutlierRatio = 0.10;
	private const int TxOutlierCeilingKbps = 3000;
	private const int HardBadCycles = 2;
	private const int SoftBadCycles = 6;
	private const int BaseGoodCycles = 3;
	private const int MaxBadScore = 7;
	private const double BadScoreDecay = 0.6;
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
		int pingMedianRttMs,
		RouterContext ctx,
		ChannelState history)
	{
		ctx.PerModemKbps.TryGetValue(device.Iface, out double modemKbps);
		bool loaded = modemKbps > LoadedTxThresholdKbps;
		int lossThreshold = loaded ? HardLossLoadedPct : HardLossPct;

		string hardReason = null;
		if (device.MobileMode != "LTE" && device.MobileMode != "Unknown") hardReason = $"mode={device.MobileMode}";
		else if (!loaded && pingLossPct > lossThreshold) hardReason = $"hardLoss={pingLossPct:F0}%{(loaded ? " loaded" : "")}";
		else if (!loaded && pingMedianRttMs > HardRttMs) hardReason = $"hardRtt={pingMedianRttMs}ms";
		else if (!loaded && pingLossPct > AppConfig.MaxLoss && pingMedianRttMs > AppConfig.MaxRtt) hardReason = $"loss+rtt={pingLossPct:F0}%/{pingMedianRttMs}ms";

		string softReason = null;
		if (hardReason == null && !ctx.IsIdle && !history.LastBackup)
		{
			if (modemKbps > 0 && ctx.MaxTxKbps > 0
				&& modemKbps < TxOutlierCeilingKbps
				&& modemKbps < OutlierRatio * ctx.MaxTxKbps)
			{
				softReason = $"txOut {modemKbps / 1000.0:F2}/{ctx.MaxTxKbps / 1000.0:F2}Mbps";
			}
		}

		bool bad = hardReason != null || softReason != null;
		history.ConsecutiveHardBad = hardReason != null ? history.ConsecutiveHardBad + 1 : 0;
		history.ConsecutiveSoftBad = softReason != null ? history.ConsecutiveSoftBad + 1 : 0;
		if (bad)
		{
			history.ConsecutiveGood = 0;
			history.BadScore = System.Math.Min(MaxBadScore, history.BadScore + 1);
		}
		else
		{
			history.ConsecutiveGood++;
			if (!history.LastBackup)
				history.BadScore *= BadScoreDecay;
		}

		int requiredGood = BaseGoodCycles + (int)System.Math.Round(history.BadScore);

		string flipReason;
		bool decision;
		if (history.ConsecutiveHardBad >= HardBadCycles)
		{
			decision = true;
			flipReason = hardReason;
		}
		else if (history.ConsecutiveSoftBad >= SoftBadCycles)
		{
			decision = true;
			flipReason = softReason;
		}
		else if (!bad && history.ConsecutiveGood >= requiredGood)
		{
			decision = false;
			flipReason = "good";
		}
		else
		{
			decision = history.LastBackup;
			flipReason = hardReason != null
				? $"try-hard ({hardReason}) {history.ConsecutiveHardBad}/{HardBadCycles}"
				: softReason != null
					? $"try-soft ({softReason}) {history.ConsecutiveSoftBad}/{SoftBadCycles}"
					: $"try-good {history.ConsecutiveGood}/{requiredGood}";
		}

		history.LastBackup = decision;
		return new ChannelDecision(decision, flipReason);
	}
}
