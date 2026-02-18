using SharpPcap;

namespace OverlayTimer.Net
{
    public static class DeviceSelector
    {
        public static ICaptureDevice OpenBestEthernetOrFallback(string? deviceName, int readTimeoutMs)
        {
            var list = CaptureDeviceList.New();
            if (list.Count == 0)
                throw new InvalidOperationException("No capture devices found. Is Npcap installed?");

            ICaptureDevice best;

            if (!string.IsNullOrWhiteSpace(deviceName))
            {
                // config에 이름이 지정된 경우: Description 부분 문자열로 매칭
                best =
                    list.FirstOrDefault(d => (d.Description ?? "").Contains(deviceName, StringComparison.OrdinalIgnoreCase)) ??
                    throw new InvalidOperationException($"No capture device matching \"{deviceName}\". Check config.json 'network.deviceName'.");
            }
            else
            {
                // 자동 선택: WAN Miniport 피하고, Ethernet 우선
                best =
                    list.FirstOrDefault(d => (d.Description ?? "").Contains("Ethernet", StringComparison.OrdinalIgnoreCase)) ??
                    list.FirstOrDefault(d => (d.Description ?? "").Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase)) ??
                    list.FirstOrDefault(d => !(d.Description ?? "").Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase)) ??
                    list[0];
            }

            best.Open(DeviceModes.Promiscuous, readTimeoutMs);
            return best;
        }
    }
}
