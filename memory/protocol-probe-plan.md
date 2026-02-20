# Protocol Auto-Discovery Fallback — 구현 계획

## 목적
게임 업데이트 후 StartMarker, EndMarker, packetType 값이 변경되었을 때,
런타임에 자동으로 새 값을 탐색하고 config.json을 갱신하는 fallback 메커니즘.

## 핵심 제약 (탐색 전략 근거)
- 값은 항상 양의 방향으로만 증가
- 증가분은 세 자리 정수 이하 (< 1000)
- 타입별 증가분은 일정하지 않음

## 현재 값 (기준점)
| 항목 | 현재 값 |
|------|---------|
| StartMarker | `80 4E 00 00 00 00 00 00 00` (앞 2바이트 = LE uint16) |
| EndMarker | `12 4F 00 00 00 00 00 00 00` |
| buffStart | 100054 |
| buffEnd | 100055 |
| enterWorld | 101059 |
| dpsAttack | 20389 |
| dpsDamage | 20897 |

---

## 아키텍처

```
CaptureWorker (S2C 스트림 수신)
  │  _probeBuffer: rolling 128KB window (원본 bytes 보존)
  │  _totalS2cBytes, _framesFound 카운터
  │
  ├── 조건①: _totalS2cBytes >= 64KB && _framesFound == 0
  │     → 마커 탐색: ProtocolProbe.TryDiscover(probeBuffer, protocol, types)
  │
  └── 조건②: _framesFound >= 50 && _recognizedPackets == 0
        → 타입 탐색: ProtocolProbe.TryDiscoverTypes(sampleFrames, types)

발견 성공 → OnProbeSuccess 콜백
  → App.xaml.cs: AppConfig 갱신 → config.json 저장 → CaptureWorker 재시작
```

---

## 신규 파일

### `Net/ProbeResult.cs`
```csharp
public sealed class ProbeResult {
    public byte[]? NewStartMarker { get; init; }
    public byte[]? NewEndMarker   { get; init; }
    public int? NewBuffStart   { get; init; }
    public int? NewBuffEnd     { get; init; }
    public int? NewEnterWorld  { get; init; }
    public int? NewDpsAttack   { get; init; }
    public int? NewDpsDamage   { get; init; }
    public int  FramesFound    { get; init; }  // 신뢰도 지표
}
```

### `Net/ProtocolProbe.cs` (순수 함수, WPF 의존 없음)
- `GenerateMarkerCandidates(byte[] current, int maxOffset)`
  - 앞 2바이트를 LE uint16으로 해석
  - [current, current+maxOffset] 범위의 후보 byte[] 열거
- `ScoreMarkerPair(ReadOnlySpan<byte> data, byte[] start, byte[] end) → int`
  - 해당 마커 쌍으로 파싱 시뮬레이션
  - 유효 헤더 조건: dataType≠0, 0≤length≤4MB, encodeType∈{0,1,2}
  - 연속 유효 패킷 수를 점수로 반환 (false positive 방지)
- `TryDiscoverMarkers(byte[] data, byte[] curStart, byte[] curEnd, int radius) → (byte[], byte[])?`
  - 최고 점수 후보 쌍 반환 (점수가 MinValidFrames=2 미만이면 null)
- `TryDiscoverPacketTypes(IReadOnlyList<(int dt, byte[] payload)> packets, PacketTypesConfig cur, int radius) → PacketTypesConfig?`
  - buffStart: payload≥32, DurationSeconds∈[1,180], UserId≠0
  - enterWorld: payload≥24, 오프셋 16의 uint64≠0
  - dpsAttack: payload==35 (exact), UserId≠0
  - dpsDamage: payload≥39, Damage∈(0, 2_095_071_572]
- `TryDiscover(byte[] rawData, ProtocolConfig, PacketTypesConfig, int markerRadius=128, int typeRadius=300) → ProbeResult?`

### `OverlayTimer.Tests/OverlayTimer.Tests.csproj`
- TargetFramework: net8.0-windows
- xUnit + coverlet
- ProjectReference to OverlayTimer

### `OverlayTimer.Tests/ProtocolProbeTests.cs`

---

## 기존 파일 수정

### `Net/PacketStreamParser.cs`
- `public int FramesFound { get; private set; }` 추가
- 프레임 완료 시 (EndMarker 발견) 증가

### `Net/CaptureWorker.cs`
- `_probeBuffer` (byte[], 64KB rolling)
- `_probeBufferPos`, `_totalS2cBytes`
- `_framesFoundAtLastProbe` 체크
- `public Action<ProbeResult>? OnProbeSuccess` 콜백
- `TryTriggerProbe()` 내부 메서드: 임계치 초과 시 ProtocolProbe 호출

### `App.xaml.cs`
- `OnProbeSuccess(ProbeResult r)` 핸들러
  - AppConfig 필드 갱신
  - `config.Save()`
  - CaptureWorker 재시작 (Dispose → new → Start)

---

## 단위 테스트 시나리오

| # | 시나리오 | 검증 |
|---|----------|------|
| 1 | 마커 미변경 (현재 값 그대로) | null 반환 OR FramesFound 동일 |
| 2 | StartMarker +1 | 정확한 새 마커 반환 |
| 3 | 마커 +50 | 성공 |
| 4 | 마커 +100 (radius=128) | 성공 |
| 5 | 패킷 타입 +22 (enterWorld 실사례) | 타입 탐색 성공 |
| 6 | 패킷 타입 +156 (dpsDamage 실사례) | radius=300 내 성공 |
| 7 | 마커+타입 동시 변경 | ProbeResult 전체 필드 정확 |
| 8 | radius 부족 (증분 > radius) | null 반환 |
| 9 | 무작위 노이즈 데이터 | null 반환 (false positive 없음) |
| 10 | 프레임 경계 잘림 (partial) | 크래시 없음, graceful |
| 11 | buffStart+buffEnd 동시 탐색 | 두 타입 모두 정확 |
| 12 | 빈 데이터 [] | null 반환 |

---

## 구현 순서

1. `Net/ProbeResult.cs` 생성
2. `Net/ProtocolProbe.cs` 생성 (핵심 로직)
3. `Net/PacketStreamParser.cs` — FramesFound 추가
4. `Net/CaptureWorker.cs` — probe buffer + 실패 감지
5. `App.xaml.cs` — 재시작 핸들러
6. `OverlayTimer.Tests/` 프로젝트 생성 + 테스트 작성
7. `OverlayTimer.sln` — 테스트 프로젝트 추가
8. 빌드 확인

---

## 설계 원칙
- **false positive 방지**: 마커 쌍 점수 = 연속 유효 헤더 수. 노이즈는 낮은 점수
- **비파괴적**: 탐색 실패 시 기존 config 유지
- **WPF 비의존**: ProtocolProbe는 순수 C# — 테스트 용이
- **기존 코드 최소 수정**: Parser에 카운터 1개, Worker에 buffer+콜백만 추가
- **Known limitation**: 마커 탐색은 앞 2바이트(LE uint16) 증가를 전제로 함. 패턴이 바뀌면 자동 탐색 실패 후 수동 갱신 필요
