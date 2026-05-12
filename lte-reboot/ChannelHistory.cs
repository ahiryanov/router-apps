using System.Collections.Concurrent;
using System.Collections.Generic;

namespace lte_reboot;

internal class ChannelState
{
	public double EmaRttMs;
	public double EmaLossPct;
	public bool EmaInitialized;
	public int ConsecutiveBad;
	public int ConsecutiveGood;
	public bool LastBackup;
	public Queue<bool> RecentBad = new();
}

internal static class ChannelHistory
{
	private const double Alpha = 0.4;
	private static readonly ConcurrentDictionary<string, ChannelState> States = new();

	public static ChannelState Get(string iface) => States.GetOrAdd(iface, _ => new ChannelState());

	public static void UpdateEma(ChannelState s, double rttMs, double lossPct)
	{
		if (!s.EmaInitialized)
		{
			s.EmaRttMs = rttMs;
			s.EmaLossPct = lossPct;
			s.EmaInitialized = true;
		}
		else
		{
			s.EmaRttMs = Alpha * rttMs + (1 - Alpha) * s.EmaRttMs;
			s.EmaLossPct = Alpha * lossPct + (1 - Alpha) * s.EmaLossPct;
		}
	}

	public static void Reset(string iface) => States.TryRemove(iface, out _);
}
