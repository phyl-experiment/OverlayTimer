using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace OverlayTimer.Net
{
    /// <summary>
    /// 게임 업데이트 후 변경된 StartMarker / EndMarker / packetType 값을
    /// 런타임에 자동으로 탐색하는 순수 함수 집합.
    /// WPF / 네트워크 의존 없음 — 단위 테스트 가능.
    /// </summary>
    public static class ProtocolProbe
    {
        public const int DefaultMarkerRadius = 128;
        public const int DefaultTypeRadius   = 300;

        /// <summary>마커 쌍을 "유효"로 판정하기 위한 최소 완전 프레임 수.</summary>
        private const int MinFrameScore = 2;

        private const int MaxPacketBodyLen = 4 * 1024 * 1024;

        // ------------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------------

        /// <summary>
        /// 원본 S2C 바이트에서 새 프로토콜 값을 탐색한다.
        /// 변경이 없거나 탐색 실패 시 null 반환.
        /// </summary>
        public static ProbeResult? TryDiscover(
            byte[] rawData,
            ProtocolConfig protocol,
            PacketTypesConfig types,
            int markerRadius = DefaultMarkerRadius,
            int typeRadius   = DefaultTypeRadius)
        {
            if (rawData == null || rawData.Length < 18)
                return null;

            byte[] curStart = protocol.StartMarkerBytes;
            byte[] curEnd   = protocol.EndMarkerBytes;

            // 1단계: 마커 탐색
            var (bestStart, bestEnd, score) = TryDiscoverMarkers(rawData, curStart, curEnd, markerRadius);
            if (bestStart == null || bestEnd == null || score < MinFrameScore)
                return null;

            // 2단계: 발견된 마커로 패킷 목록 추출
            var packets = ExtractPackets(rawData, bestStart, bestEnd);

            // 3단계: 패킷 타입 탐색
            int? newBuffStart  = TryFindBuffStart(packets, types.BuffStart, typeRadius);
            int? newBuffEnd    = TryFindBuffEnd(packets, types.BuffEnd, newBuffStart, typeRadius);
            int? newEnterWorld = TryFindEnterWorld(packets, types.EnterWorld, typeRadius);
            int? newDpsAttack  = TryFindDpsAttack(packets, types.DpsAttack, typeRadius);
            int? newDpsDamage  = TryFindDpsDamage(packets, types.DpsDamage, typeRadius);

            bool markerChanged = !bestStart.AsSpan().SequenceEqual(curStart)
                              || !bestEnd.AsSpan().SequenceEqual(curEnd);
            bool typeChanged   = newBuffStart.HasValue || newBuffEnd.HasValue
                              || newEnterWorld.HasValue || newDpsAttack.HasValue
                              || newDpsDamage.HasValue;

            if (!markerChanged && !typeChanged)
                return null;

            return new ProbeResult
            {
                NewStartMarker = markerChanged ? bestStart : null,
                NewEndMarker   = markerChanged ? bestEnd   : null,
                NewBuffStart   = newBuffStart,
                NewBuffEnd     = newBuffEnd,
                NewEnterWorld  = newEnterWorld,
                NewDpsAttack   = newDpsAttack,
                NewDpsDamage   = newDpsDamage,
                FramesFound    = score,
            };
        }

        // ------------------------------------------------------------------
        // Marker discovery
        // ------------------------------------------------------------------

        /// <summary>
        /// 후보 마커 바이트 배열 열거.
        /// 앞 2바이트를 LE uint16 으로 해석하고 [current, current+maxOffset] 범위를 생성.
        /// 나머지 바이트(보통 모두 0x00)는 그대로 보존한다.
        /// </summary>
        public static IEnumerable<byte[]> GenerateMarkerCandidates(byte[] current, int maxOffset)
        {
            if (current == null || current.Length < 2 || maxOffset < 0)
                yield break;

            ushort baseVal = BinaryPrimitives.ReadUInt16LittleEndian(current.AsSpan(0, 2));
            byte[] suffix  = current[2..];

            for (int delta = 0; delta <= maxOffset; delta++)
            {
                uint candidate = (uint)baseVal + (uint)delta;
                if (candidate > ushort.MaxValue)
                    break;

                var buf = new byte[current.Length];
                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0, 2), (ushort)candidate);
                suffix.CopyTo(buf, 2);
                yield return buf;
            }
        }

        /// <summary>
        /// 점수: data 안에서 (startCand → valid_headers* → endCand) 완전 시퀀스 수.
        /// 최소 1개의 유효 헤더가 있어야 프레임으로 인정한다.
        /// </summary>
        public static int ScoreMarkerPair(ReadOnlySpan<byte> data, byte[] start, byte[] end)
        {
            if (start == null || end == null
                || data.Length < start.Length + end.Length)
                return 0;

            int score      = 0;
            int searchFrom = 0;

            while (true)
            {
                int startPos = IndexOf(data, searchFrom, start);
                if (startPos < 0) break;

                int headerStart = startPos + start.Length;
                // endMarker를 미리 감지해 EndMarker 바이트가 패킷 헤더로 오인되지 않도록 한다.
                var (validCount, parsedEnd) = ParseValidHeaders(data, headerStart, end);

                if (validCount >= 1
                    && parsedEnd + end.Length <= data.Length
                    && data.Slice(parsedEnd, end.Length).SequenceEqual(end))
                {
                    score++;
                    searchFrom = parsedEnd + end.Length;
                }
                else
                {
                    searchFrom = startPos + 1;
                }
            }

            return score;
        }

        /// <summary>
        /// data 에서 최고 점수를 내는 (startMarker, endMarker) 쌍과 점수를 반환한다.
        /// 점수가 MinFrameScore 미만이면 (null, null, 0) 반환.
        /// </summary>
        internal static (byte[]? start, byte[]? end, int score) TryDiscoverMarkers(
            byte[] data,
            byte[] curStart,
            byte[] curEnd,
            int radius)
        {
            byte[]? bestStart = null;
            byte[]? bestEnd   = null;
            int     bestScore = 0;

            // Pre-filter: 데이터 안에 실제로 등장하는 start 후보만 처리
            var validStarts = new List<byte[]>();
            foreach (var sc in GenerateMarkerCandidates(curStart, radius))
            {
                if (IndexOf(data.AsSpan(), 0, sc) >= 0)
                    validStarts.Add(sc);
            }

            foreach (var startCand in validStarts)
            {
                foreach (var endCand in GenerateMarkerCandidates(curEnd, radius))
                {
                    int s = ScoreMarkerPair(data.AsSpan(), startCand, endCand);
                    if (s > bestScore)
                    {
                        bestScore = s;
                        bestStart = startCand;
                        bestEnd   = endCand;
                    }
                }
            }

            return bestScore >= MinFrameScore
                ? (bestStart, bestEnd, bestScore)
                : (null, null, 0);
        }

        // ------------------------------------------------------------------
        // Packet extraction (with known markers)
        // ------------------------------------------------------------------

        /// <summary>
        /// 올바른 마커로 data 를 파싱해 (dataType, payload) 목록을 추출한다.
        /// </summary>
        public static List<(int dataType, byte[] payload)> ExtractPackets(
            byte[] data, byte[] startMarker, byte[] endMarker)
        {
            var result = new List<(int, byte[])>();
            if (data == null || startMarker == null || endMarker == null)
                return result;

            int pos = 0;

            while (true)
            {
                int startPos = IndexOf(data.AsSpan(), pos, startMarker);
                if (startPos < 0) break;

                int cursor = startPos + startMarker.Length;
                bool frameComplete = false;

                while (cursor + 9 <= data.Length)
                {
                    // endMarker 체크
                    if (cursor + endMarker.Length <= data.Length
                        && data.AsSpan(cursor, endMarker.Length).SequenceEqual(endMarker))
                    {
                        pos = cursor + endMarker.Length;
                        frameComplete = true;
                        break;
                    }

                    int dataType = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4));
                    int length   = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor + 4, 4));
                    int encType  = data[cursor + 8];

                    if (dataType == 0 || length < 0 || length > MaxPacketBodyLen || encType > 2)
                        break;

                    if (cursor + 9 + length > data.Length)
                        break;

                    var payload = new byte[length];
                    data.AsSpan(cursor + 9, length).CopyTo(payload);
                    result.Add((dataType, payload));

                    cursor += 9 + length;
                }

                if (!frameComplete)
                    pos = startPos + 1;
            }

            return result;
        }

        // ------------------------------------------------------------------
        // Packet type heuristics
        // ------------------------------------------------------------------

        /// <summary>
        /// buffStart: payload ≥ 32, UserId ≠ 0, DurationSeconds ∈ [1, 180]
        /// </summary>
        public static int? TryFindBuffStart(
            List<(int dataType, byte[] payload)> packets, int current, int radius)
        {
            return FindTypeByHeuristic(packets, current, radius, static (_, p) =>
            {
                if (p.Length < 32) return false;
                ulong userId = BinaryPrimitives.ReadUInt64LittleEndian(p.AsSpan(0, 8));
                if (userId == 0) return false;
                float dur = BitConverter.Int32BitsToSingle(
                    BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(0x14, 4)));
                return !float.IsNaN(dur) && !float.IsInfinity(dur)
                    && dur >= 1f && dur <= 180f;
            }, minHits: 2);
        }

        /// <summary>
        /// buffEnd: payload ≥ 20, UserId ≠ 0, InstKey ≠ 0.
        /// newBuffStart が発見済みの場合 buffStart+1 を優先確認する.
        /// </summary>
        public static int? TryFindBuffEnd(
            List<(int dataType, byte[] payload)> packets, int current,
            int? newBuffStart, int radius)
        {
            // buffEnd = buffStart+1 패턴 우선 확인
            if (newBuffStart.HasValue)
            {
                int candidate = newBuffStart.Value + 1;
                if (candidate != current
                    && packets.Exists(p => p.dataType == candidate && IsBuffEndShape(p.payload)))
                    return candidate;
            }

            return FindTypeByHeuristic(packets, current, radius,
                static (_, p) => IsBuffEndShape(p));
        }

        /// <summary>
        /// enterWorld: payload ≥ 24, offset 16 의 uint64 ≠ 0
        /// </summary>
        public static int? TryFindEnterWorld(
            List<(int dataType, byte[] payload)> packets, int current, int radius)
        {
            return FindTypeByHeuristic(packets, current, radius, static (_, p) =>
            {
                if (p.Length < 24) return false;
                ulong id = BinaryPrimitives.ReadUInt64LittleEndian(p.AsSpan(16, 8));
                return id != 0;
            });
        }

        /// <summary>
        /// dpsAttack: payload == 35 (exact), UserId ≠ 0, TargetId ≠ 0
        /// </summary>
        public static int? TryFindDpsAttack(
            List<(int dataType, byte[] payload)> packets, int current, int radius)
        {
            return FindTypeByHeuristic(packets, current, radius, static (_, p) =>
            {
                if (p.Length != 35) return false;
                uint userId   = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(0, 4));
                uint targetId = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(8, 4));
                return userId != 0 && targetId != 0;
            }, minHits: 2);
        }

        /// <summary>
        /// dpsDamage: payload ≥ 39, UserId ≠ 0, TargetId ≠ 0, Damage ∈ (0, 2_095_071_572]
        /// </summary>
        public static int? TryFindDpsDamage(
            List<(int dataType, byte[] payload)> packets, int current, int radius)
        {
            return FindTypeByHeuristic(packets, current, radius, static (_, p) =>
            {
                if (p.Length < 39) return false;
                uint userId   = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(0, 4));
                uint targetId = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(8, 4));
                uint damage   = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(16, 4));
                return userId != 0 && targetId != 0
                    && damage > 0 && damage <= 2_095_071_572u;
            }, minHits: 2);
        }

        // ------------------------------------------------------------------
        // Internal helpers
        // ------------------------------------------------------------------

        private static bool IsBuffEndShape(byte[] p)
        {
            if (p.Length < 20) return false;
            ulong userId  = BinaryPrimitives.ReadUInt64LittleEndian(p.AsSpan(0, 8));
            ulong instKey = BinaryPrimitives.ReadUInt64LittleEndian(p.AsSpan(8, 8));
            return userId != 0 && instKey != 0;
        }

        private static int? FindTypeByHeuristic(
            List<(int dataType, byte[] payload)> packets,
            int current,
            int radius,
            Func<int, byte[], bool> heuristic,
            int minHits = 1)
        {
            for (int delta = 1; delta <= radius; delta++)
            {
                int candidate = current + delta;
                int hits = 0;
                foreach (var (dt, payload) in packets)
                {
                    if (dt != candidate) continue;
                    if (heuristic(dt, payload))
                    {
                        hits++;
                        if (hits >= minHits)
                            return candidate;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// 현재 위치에서 연속 유효 패킷 헤더를 파싱한다.
        /// 유효 조건: dataType ≠ 0, 0 ≤ length ≤ MaxPacketBodyLen, encodeType ≤ 2
        /// endMarker 가 지정된 경우, 해당 바이트 시퀀스를 만나면 소비하지 않고 멈춘다.
        /// </summary>
        internal static (int validCount, int endPos) ParseValidHeaders(
            ReadOnlySpan<byte> data, int start, byte[]? endMarker = null)
        {
            int pos   = start;
            int count = 0;

            while (pos + 9 <= data.Length)
            {
                // EndMarker 우선 감지 — EndMarker 바이트가 dataType=0 이 아닌 값으로
                // 읽혀 유효 패킷 헤더로 오인되는 것을 방지한다.
                if (endMarker != null
                    && pos + endMarker.Length <= data.Length
                    && data.Slice(pos, endMarker.Length).SequenceEqual(endMarker))
                    break;

                int dataType = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(pos, 4));
                int length   = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(pos + 4, 4));
                int encType  = data[pos + 8];

                if (dataType == 0 || length < 0 || length > MaxPacketBodyLen || encType > 2)
                    break;

                if (pos + 9 + length > data.Length)
                    break;

                pos += 9 + length;
                count++;
            }

            return (count, pos);
        }

        private static int IndexOf(ReadOnlySpan<byte> data, int from, byte[] pattern)
        {
            if (from >= data.Length || pattern == null || pattern.Length == 0)
                return -1;
            int idx = data[from..].IndexOf(pattern.AsSpan());
            return idx < 0 ? -1 : from + idx;
        }
    }
}
