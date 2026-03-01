using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using PacketDotNet;
using SharpPcap;

namespace OverlayTimer.Net
{
    public static class DeviceSelector
    {
        public sealed record DeviceCandidate(
            int Index,
            ICaptureDevice Device,
            string NicLabel,
            int StaticScore,
            string StaticReason);

        public sealed record DeviceProbeScore(
            DeviceCandidate Candidate,
            long ArrivalPackets,
            long TcpPayloadPackets,
            long TcpPayloadBytes,
            int DynamicScore,
            string DynamicReason);

        private static readonly string[] EthernetKeywords =
        [
            "ethernet",
            "eth",
            "enp",
            "ens",
            "en0",
            "en1",
            "realtek",
            "intel"
        ];

        private static readonly string[] WifiKeywords =
        [
            "wi-fi",
            "wifi",
            "wireless",
            "wlan",
            "wlp"
        ];

        private static readonly string[] VirtualKeywords =
        [
            "virtual",
            "vmware",
            "virtualbox",
            "hyper-v",
            "vethernet",
            "docker",
            "wsl",
            "zerotier"
        ];

        private static readonly string[] VpnKeywords =
        [
            "vpn",
            "tunnel",
            "tap",
            "tun",
            "wireguard",
            "tailscale"
        ];

        private static readonly string[] LoopbackKeywords =
        [
            "loopback",
            "npcap loopback"
        ];

        private static readonly string[] WanMiniportKeywords =
        [
            "wan miniport"
        ];

        private static readonly string[] BluetoothKeywords =
        [
            "bluetooth"
        ];

        public static IReadOnlyList<DeviceCandidate> GetCandidates()
        {
            var list = CaptureDeviceList.New();
            if (list.Count == 0)
                throw new InvalidOperationException("No capture devices found. Is Npcap installed?");

            var nicByGuid = BuildNicMapByGuid();
            return list
                .Select((device, index) => BuildCandidate(index, device, nicByGuid))
                .ToList();
        }

        public static DeviceCandidate SelectCandidate(
            IReadOnlyList<DeviceCandidate> candidates,
            string? deviceName)
        {
            if (candidates.Count == 0)
                throw new InvalidOperationException("No capture devices found. Is Npcap installed?");

            if (!string.IsNullOrWhiteSpace(deviceName))
            {
                return candidates.FirstOrDefault(c => MatchConfiguredDevice(c, deviceName)) ??
                       throw new InvalidOperationException(
                           $"No capture device matching \"{deviceName}\". Check config.json 'network.deviceName'.");
            }

            return candidates
                .OrderByDescending(c => c.StaticScore)
                .ThenBy(c => c.Index)
                .First();
        }

        public static ICaptureDevice OpenCandidate(DeviceCandidate candidate, int readTimeoutMs)
        {
            try
            {
                candidate.Device.Open(DeviceModes.Promiscuous, readTimeoutMs);
            }
            catch
            {
                try { candidate.Device.Close(); } catch { /* ignore */ }
                candidate.Device.Open(DeviceModes.Promiscuous, readTimeoutMs);
            }

            return candidate.Device;
        }

        public static ICaptureDevice OpenBestEthernetOrFallback(string? deviceName, int readTimeoutMs)
        {
            var candidates = GetCandidates();
            LogCandidates(candidates);

            var selected = SelectCandidate(candidates, deviceName);
            var reason = string.IsNullOrWhiteSpace(deviceName)
                ? $"auto staticScore={selected.StaticScore}"
                : $"config.deviceName contains \"{deviceName}\"";

            LogHelper.Write(
                $"[DeviceSelector] Selected: name=\"{selected.Device.Name}\" desc=\"{selected.Device.Description ?? "(null)"}\" " +
                $"mappedNic=\"{selected.NicLabel}\" reason=\"{reason}; {selected.StaticReason}\"");

            OpenCandidate(selected, readTimeoutMs);
            LogHelper.Write("[DeviceSelector] Device opened in promiscuous mode.");
            return selected.Device;
        }

        public static DeviceProbeScore ProbeCandidateTraffic(
            DeviceCandidate candidate,
            string filter,
            TimeSpan duration,
            int readTimeoutMs = 500)
        {
            if (duration <= TimeSpan.Zero)
                duration = TimeSpan.FromSeconds(1);

            long arrivals = 0;
            long tcpPayloadPackets = 0;
            long tcpPayloadBytes = 0;

            PacketArrivalEventHandler handler = (_, e) =>
            {
                Interlocked.Increment(ref arrivals);

                try
                {
                    var raw = e.GetPacket();
                    var parsed = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
                    var tcp = parsed.Extract<TcpPacket>();
                    if (tcp?.PayloadData == null || tcp.PayloadData.Length == 0)
                        return;

                    Interlocked.Increment(ref tcpPayloadPackets);
                    Interlocked.Add(ref tcpPayloadBytes, tcp.PayloadData.Length);
                }
                catch
                {
                    // Ignore per-packet parsing errors during probe.
                }
            };

            bool opened = false;
            try
            {
                OpenCandidate(candidate, readTimeoutMs);
                opened = true;
                candidate.Device.Filter = filter;
                candidate.Device.OnPacketArrival += handler;
                candidate.Device.StartCapture();

                Thread.Sleep(duration);
            }
            finally
            {
                if (opened)
                {
                    try { candidate.Device.StopCapture(); } catch { /* ignore */ }
                    try { candidate.Device.OnPacketArrival -= handler; } catch { /* ignore */ }
                    try { candidate.Device.Close(); } catch { /* ignore */ }
                }
            }

            int dynamicScore = candidate.StaticScore;
            var dynamicReasons = new List<string> { $"static={candidate.StaticScore}" };

            int arrivalBonus = (int)Math.Min(arrivals, 40);
            dynamicScore += arrivalBonus;
            if (arrivalBonus > 0) dynamicReasons.Add($"+arrivals={arrivalBonus}");

            int payloadPacketBonus = (int)Math.Min(tcpPayloadPackets * 120, 1200);
            dynamicScore += payloadPacketBonus;
            if (payloadPacketBonus > 0) dynamicReasons.Add($"+tcpPayloadPkts={payloadPacketBonus}");

            int payloadByteBonus = (int)Math.Min(tcpPayloadBytes / 512, 80);
            dynamicScore += payloadByteBonus;
            if (payloadByteBonus > 0) dynamicReasons.Add($"+tcpPayloadBytes={payloadByteBonus}");

            if (tcpPayloadPackets > 0)
            {
                dynamicScore += 80;
                dynamicReasons.Add("+hasPayload=80");
            }

            if (arrivals == 0)
            {
                dynamicScore -= 150;
                dynamicReasons.Add("-noTraffic=150");
            }

            return new DeviceProbeScore(
                candidate,
                arrivals,
                tcpPayloadPackets,
                tcpPayloadBytes,
                dynamicScore,
                string.Join(", ", dynamicReasons));
        }

        public static void LogCandidates(IReadOnlyList<DeviceCandidate> candidates)
        {
            LogHelper.Write($"[DeviceSelector] Found {candidates.Count} capture devices.");
            foreach (var c in candidates.OrderBy(x => x.Index))
            {
                LogHelper.Write(
                    $"[DeviceSelector] #{c.Index}: name=\"{c.Device.Name}\" desc=\"{c.Device.Description ?? "(null)"}\" " +
                    $"mappedNic=\"{c.NicLabel}\" staticScore={c.StaticScore} reason=\"{c.StaticReason}\"");
            }
        }

        private static DeviceCandidate BuildCandidate(
            int index,
            ICaptureDevice device,
            IReadOnlyDictionary<Guid, NetworkInterface> nicByGuid)
        {
            string name = device.Name ?? string.Empty;
            string desc = device.Description ?? string.Empty;
            string text = $"{name} {desc}".ToLowerInvariant();

            int score = 0;
            var reasons = new List<string>();

            if (ContainsAny(text, EthernetKeywords))
            {
                score += 100;
                reasons.Add("+ethernet");
            }

            if (ContainsAny(text, WifiKeywords))
            {
                score += 70;
                reasons.Add("+wifi");
            }

            if (ContainsAny(text, VirtualKeywords))
            {
                score -= 80;
                reasons.Add("-virtual");
            }

            if (ContainsAny(text, VpnKeywords))
            {
                score -= 80;
                reasons.Add("-vpn/tunnel");
            }

            if (ContainsAny(text, LoopbackKeywords))
            {
                score -= 220;
                reasons.Add("-loopback");
            }

            if (ContainsAny(text, WanMiniportKeywords))
            {
                score -= 200;
                reasons.Add("-wan-miniport");
            }

            if (ContainsAny(text, BluetoothKeywords))
            {
                score -= 120;
                reasons.Add("-bluetooth");
            }

            NetworkInterface? nic = null;
            if (TryExtractGuid(name, out var guid) && nicByGuid.TryGetValue(guid, out var mapped))
                nic = mapped;

            string nicLabel = nic == null
                ? "(unmapped)"
                : $"{nic.Name}/{nic.NetworkInterfaceType}/{nic.OperationalStatus}";

            if (nic != null)
            {
                if (nic.OperationalStatus == OperationalStatus.Up)
                {
                    score += 60;
                    reasons.Add("+nic-up");
                }
                else
                {
                    score -= 40;
                    reasons.Add("-nic-down");
                }

                switch (nic.NetworkInterfaceType)
                {
                    case NetworkInterfaceType.Ethernet:
                    case NetworkInterfaceType.Ethernet3Megabit:
                    case NetworkInterfaceType.FastEthernetFx:
                    case NetworkInterfaceType.FastEthernetT:
                    case NetworkInterfaceType.GigabitEthernet:
                        score += 80;
                        reasons.Add("+nic-ethernet");
                        break;
                    case NetworkInterfaceType.Wireless80211:
                        score += 55;
                        reasons.Add("+nic-wifi");
                        break;
                    case NetworkInterfaceType.Loopback:
                        score -= 220;
                        reasons.Add("-nic-loopback");
                        break;
                    case NetworkInterfaceType.Tunnel:
                    case NetworkInterfaceType.Ppp:
                        score -= 120;
                        reasons.Add("-nic-tunnel/ppp");
                        break;
                }

                var nicText = $"{nic.Name} {nic.Description}".ToLowerInvariant();
                if (ContainsAny(nicText, VirtualKeywords))
                {
                    score -= 80;
                    reasons.Add("-nic-virtual");
                }
            }

            if (score == 0)
                reasons.Add("neutral");

            return new DeviceCandidate(
                index,
                device,
                nicLabel,
                score,
                string.Join(", ", reasons));
        }

        private static bool MatchConfiguredDevice(DeviceCandidate c, string configured)
        {
            var needle = configured.Trim();
            if (needle.Length == 0)
                return false;

            bool Match(string? s) =>
                !string.IsNullOrWhiteSpace(s) &&
                s.Contains(needle, StringComparison.OrdinalIgnoreCase);

            return Match(c.Device.Name)
                || Match(c.Device.Description)
                || Match(c.NicLabel);
        }

        private static bool ContainsAny(string text, string[] keywords)
        {
            for (int i = 0; i < keywords.Length; i++)
            {
                if (text.Contains(keywords[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static IReadOnlyDictionary<Guid, NetworkInterface> BuildNicMapByGuid()
        {
            try
            {
                var map = new Dictionary<Guid, NetworkInterface>();
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (!Guid.TryParse(nic.Id, out var guid))
                        continue;
                    map[guid] = nic;
                }

                return map;
            }
            catch
            {
                return new Dictionary<Guid, NetworkInterface>();
            }
        }

        private static bool TryExtractGuid(string deviceName, out Guid guid)
        {
            guid = default;
            if (string.IsNullOrWhiteSpace(deviceName))
                return false;

            int open = deviceName.IndexOf('{');
            int close = deviceName.IndexOf('}', open + 1);
            if (open < 0 || close <= open)
                return false;

            var raw = deviceName.Substring(open + 1, close - open - 1);
            return Guid.TryParse(raw, out guid);
        }
    }
}
