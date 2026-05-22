using System.Collections.Concurrent;
using System.Collections.Generic;

namespace lte_reboot;

internal class ChannelState
{
	public int ConsecutiveBad;
	public int ConsecutiveGood;
	public bool LastBackup;
	public Queue<bool> RecentBad = new();
}

internal static class ChannelHistory
{
	private static readonly ConcurrentDictionary<string, ChannelState> States = new();

	public static ChannelState Get(string iface) => States.GetOrAdd(iface, _ => new ChannelState());

	public static void Reset(string iface) => States.TryRemove(iface, out _);
}
