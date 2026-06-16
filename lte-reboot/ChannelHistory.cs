using System.Collections.Concurrent;

namespace lte_reboot;

internal class ChannelState
{
	public int ConsecutiveHardBad;
	public int ConsecutiveSoftBad;
	public int ConsecutiveGood;
	public bool LastBackup;
	public double BadScore;
}

internal static class ChannelHistory
{
	private static readonly ConcurrentDictionary<string, ChannelState> States = new();

	public static ChannelState Get(string iface) => States.GetOrAdd(iface, _ => new ChannelState());

	public static void Reset(string iface) => States.TryRemove(iface, out _);
}
