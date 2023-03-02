namespace router_dashboard.Data;
public class SystemdService
{
    private readonly Dictionary<string, string> _CheckProcesses = new()
    {
        { "dnsmasq.service", "DNS & DHCP Dnsmasq" },
        { "chilli.service", "CoovaChilli" },
        { "nginx.service", "Nginx" },
        { "openvpn@client.service", "OpenVPN" },
        { "gps.service", "GPS" },
        { "zabbix-proxy.service", "Zabbix Proxy" },
        { "zabbix-agent.service", "Zabbix Agent" },
        { "lte-reboot.timer", "LTE reboot" },
        { "dashboard.service", "Dashboard" },
    };
    public SystemdService()
    {
        var machine = File.ReadLines("/etc/os-release")
            .FirstOrDefault(t => t.Contains("MACHINE"))?
            .Split("=")[1]
            .Replace("\"", "");
        switch (machine)
        {
            case "yocto-arm64-imx":
            case "yocto-arm64-nanopi":
                _CheckProcesses.Add("lte-check-flow-count.timer", "Check flow count");
                break;
            case "yocto-x86-e3372":
            case "yocto-x86-qmi":
                _CheckProcesses.Add("lte-check-gateways.timer", "LTE check gateways");
                break;
        }
    }
    public List<(string Process, bool IsActive)> GetStatuses()
    {
        List<(string, bool)> statuses = new();
        foreach (var process in _CheckProcesses)
        {
            bool isActive;
            var response = $"systemctl is-active {process.Key}".Bash();
            if (process.Key.EndsWith("timer"))
            {
                var responseService = $"systemctl is-active {process.Key.Replace("timer", "service")}".Bash();
                isActive = responseService == "inactive" && response == "active";
            }
            else
            {
                isActive = response == "active";
            }
            statuses.Add((process.Value, isActive));
        }
        return statuses;
    }
}