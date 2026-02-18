using SharpPcap;

namespace OverlayTimer.Net
{
    public static class DeviceSelector
    {
        public static ICaptureDevice OpenBestEthernetOrFallback(int readTimeoutMs)
        {
            var list = CaptureDeviceList.New();
            if (list.Count == 0)
                throw new InvalidOperationException("No capture devices found. Is Npcap installed?");

            // WAN Miniport 피하고, Ethernet 우선
            var best =
                list.FirstOrDefault(d => (d.Description ?? "").Contains("Ethernet", StringComparison.OrdinalIgnoreCase)) ??
                list.FirstOrDefault(d => (d.Description ?? "").Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase)) ??
                list.FirstOrDefault(d => !(d.Description ?? "").Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase)) ??
                list[0];

            best.Open(DeviceModes.Promiscuous, readTimeoutMs);
            return best;
        }
    }

}
