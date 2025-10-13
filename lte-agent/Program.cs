// dotnet add package Tmds.DBus
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Tmds.DBus;

namespace NmMetricDemo
{
    [DBusInterface("org.freedesktop.NetworkManager")]
    public interface INetworkManager : IDBusObject
    {
        Task<ObjectPath> GetDeviceByIpIfaceAsync(string iface);
        Task<ObjectPath> ActivateConnectionAsync(ObjectPath connection, ObjectPath device, ObjectPath specificObject);
        Task DeactivateConnectionAsync(ObjectPath activeConnection);
    }

    // ВАЖНО: сигнатуры именно такие — без <T>, возвращают object.
    [DBusInterface("org.freedesktop.DBus.Properties")]
    public interface IProperties : IDBusObject
    {
        Task<object> GetAsync(string iface, string prop);
        Task<IDictionary<string, object>> GetAllAsync(string iface);
        Task SetAsync(string iface, string prop, object value);
    }

    [DBusInterface("org.freedesktop.NetworkManager.Settings.Connection")]
    public interface INMSettingsConnection : IDBusObject
    {
        Task<IDictionary<string, IDictionary<string, object>>> GetSettingsAsync();
        Task UpdateAsync(IDictionary<string, IDictionary<string, object>> settings);
    }

    public class Program
    {
        const string NmService = "org.freedesktop.NetworkManager";
        static readonly ObjectPath NmPath = new("/org/freedesktop/NetworkManager");

        public static async Task Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: NmMetricDemo <iface> <metric>");
                Console.WriteLine("Example: NmMetricDemo wwan3 700");
                return;
            }
			string iface = args[0];
            if (!int.TryParse(args[1], out int metric) || metric < 0)
            {
                Console.WriteLine("Metric must be a non-negative integer.");
                return;
            }

            try
			{
				await SetDefaultRouteMetricAsync(args[0], metric);
				Console.WriteLine($"OK: applied ipv4.route-metric={metric} on {args[0]}.");
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine(ex.ToString());
				Environment.Exit(1);
			}
        }

        public static async Task SetDefaultRouteMetricAsync(string iface, int metric)
        {
            var bus = Connection.System;
            await bus.ConnectAsync();

            var nm     = bus.CreateProxy<INetworkManager>(NmService, NmPath);
            var nmProp = bus.CreateProxy<IProperties>(NmService, NmPath);

            // 1) Устройство по имени интерфейса (wwanX)
            var devPath = await nm.GetDeviceByIpIfaceAsync(iface);

            // 2) Найти активное подключение, где участвует это устройство
            var activeObj  = await nmProp.GetAsync("org.freedesktop.NetworkManager", "ActiveConnections");
            var activeList = (ObjectPath[])activeObj; // тип свойства ao → массив ObjectPath

            ObjectPath? activeConnPath = null;
            ObjectPath? settingsConnPath = null;

            foreach (var acPath in activeList)
            {
                var acProps = bus.CreateProxy<IProperties>(NmService, acPath);

                var devicesObj = await acProps.GetAsync("org.freedesktop.NetworkManager.Connection.Active", "Devices");
                var acDevices  = (ObjectPath[])devicesObj; // тоже ao

                if (acDevices.Contains(devPath))
                {
                    activeConnPath = acPath;

                    var connObj = await acProps.GetAsync("org.freedesktop.NetworkManager.Connection.Active", "Connection");
                    settingsConnPath = (ObjectPath)connObj; // тип 'o' → ObjectPath
                    break;
                }
            }

            if (activeConnPath is null || settingsConnPath is null)
                throw new InvalidOperationException($"Interface {iface} is not part of any active NetworkManager connection.");

            // 3) Обновить профиль: ipv4.route-metric = <metric>
            var conn = bus.CreateProxy<INMSettingsConnection>(NmService, settingsConnPath.Value);
            var settings = await conn.GetSettingsAsync();

            if (!settings.TryGetValue("ipv4", out var ipv4Section))
            {
                ipv4Section = new Dictionary<string, object>();
                settings["ipv4"] = ipv4Section;
            }
            ipv4Section["route-metric"] = metric;

            await conn.UpdateAsync(settings); // сохраняет профиль на диск (политики polkit могут требоваться)

            // 4) Переактивировать, чтобы применилось в ядре (короткий down/up)
            await nm.DeactivateConnectionAsync(activeConnPath.Value);
            await nm.ActivateConnectionAsync(settingsConnPath.Value, devPath, ObjectPath.Root);
        }
    }
}
