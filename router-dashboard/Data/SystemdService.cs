using System.Diagnostics;

namespace router_dashboard.Data
{
    public class SystemdService
    {
        Dictionary<string,string> _checkProcesses = new()
        {
            { "dnsmasq.service", "DNS & DHCP"},
            { "chilli.service", "CoovaChilli"},
            { "openvpn@client.service", "OpenVPN"},
            { "gps.service","GPS"},
            { "zabbix-proxy.service","Zabbix Proxy"},
            { "zabbix-agent.service","Zabbix Agent"},
            { "lte-reboot.timer","LTE reboot"},
            { "lte-check-gateways.timer","LTE check gateways"}
        };

        public List<(string Process, bool IsActive)> GetStatuses()
        {
            List<(string, bool)> statuses = new();
            foreach (var process in _checkProcesses)
            {
                var response = $"systemctl is-active {process.Key}".Bash();
                statuses.Add((process.Value,response == "active"));
            }
            return statuses;
        }

    }

}
