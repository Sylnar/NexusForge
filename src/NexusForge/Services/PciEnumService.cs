using System.Management;

namespace Sylnar.Services;

/// <summary>
/// Enumerates PCIe devices on the host and their BAR memory ranges via WMI.
/// Lets BarProbe users pick a target without hunting through Device Manager.
/// All data comes from Windows' Plug-and-Play view — no DMA / leechcore needed.
/// </summary>
public sealed class PciEnumService
{
    public sealed record PciBar(ulong Start, ulong End, ulong Size);

    public sealed record PciDevice(
        string Name,
        string PnpId,
        string Vendor,
        string Device,
        IReadOnlyList<PciBar> Bars);

    private readonly LogService _log;

    public PciEnumService(LogService log) => _log = log;

    /// <summary>
    /// Returns every PCIe device that has at least one memory BAR allocated.
    /// </summary>
    public IReadOnlyList<PciDevice> Enumerate()
    {
        var devices = new Dictionary<string, (string name, List<PciBar> bars)>();

        try
        {
            // Map: PnPDeviceID -> friendly name (from Win32_PnPEntity)
            var nameById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var pnp = new ManagementObjectSearcher(
                "SELECT DeviceID, Name FROM Win32_PnPEntity WHERE DeviceID LIKE 'PCI%'"))
            {
                foreach (ManagementObject o in pnp.Get())
                {
                    var id = (o["DeviceID"] as string) ?? "";
                    var nm = (o["Name"] as string) ?? "";
                    if (id.Length > 0)
                        nameById[id] = nm;
                }
            }

            // Walk allocated memory resources, group by device
            using var alloc = new ManagementObjectSearcher(
                "SELECT * FROM Win32_PnPAllocatedResource");
            foreach (ManagementObject o in alloc.Get())
            {
                var ant = (o["Antecedent"] as string) ?? "";
                var dep = (o["Dependent"] as string) ?? "";
                if (!ant.Contains("Win32_DeviceMemoryAddress")) continue;
                if (!dep.Contains("PCI\\\\")) continue;

                var memObj = new ManagementObject(ant);
                memObj.Get();
                var depObj = new ManagementObject(dep);
                depObj.Get();

                var pnpId = (depObj["PNPDeviceID"] as string) ?? (depObj["DeviceID"] as string) ?? "";
                if (pnpId.Length == 0) continue;

                ulong start = ToUlong(memObj["StartingAddress"]);
                ulong end   = ToUlong(memObj["EndingAddress"]);
                if (end == 0) continue;

                if (!devices.TryGetValue(pnpId, out var entry))
                {
                    var name = nameById.TryGetValue(pnpId, out var n) ? n : pnpId;
                    entry = (name, new List<PciBar>());
                    devices[pnpId] = entry;
                }
                entry.bars.Add(new PciBar(start, end, end - start + 1));
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"PciEnum WMI query failed: {ex.Message}");
            return Array.Empty<PciDevice>();
        }

        return devices
            .Select(kvp =>
            {
                var (vendor, device) = ParseVidDid(kvp.Key);
                var sortedBars = kvp.Value.bars.OrderBy(b => b.Start).ToList();
                return new PciDevice(kvp.Value.name, kvp.Key, vendor, device, sortedBars);
            })
            .OrderBy(d => d.Name)
            .ToList();
    }

    private static ulong ToUlong(object? o)
    {
        if (o == null) return 0;
        try { return Convert.ToUInt64(o); }
        catch { return 0; }
    }

    private static (string vendor, string device) ParseVidDid(string pnpId)
    {
        // PCI\VEN_xxxx&DEV_yyyy&...
        var v = ""; var d = "";
        var parts = pnpId.Split('\\', '&');
        foreach (var p in parts)
        {
            if (p.StartsWith("VEN_", StringComparison.OrdinalIgnoreCase))
                v = p.Substring(4, Math.Min(4, p.Length - 4));
            else if (p.StartsWith("DEV_", StringComparison.OrdinalIgnoreCase))
                d = p.Substring(4, Math.Min(4, p.Length - 4));
        }
        return (v, d);
    }
}
