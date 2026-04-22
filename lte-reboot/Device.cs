namespace lte_reboot;

class Device
{
	public string Name { get; set; }
	public string State { get; set; }
	public string Iface { get; set; }
	public int Rssi { get; set; }
	public string MobileMode { get; set; }
	public string Operator { get; set; }
	public override string ToString()
	{
		return $"{Name} {State} {Iface} RSSI {Rssi} Mode {MobileMode} OP {Operator}";
	}
}
