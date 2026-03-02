# PacketAnalyzer — 패킷 분석기 명세

## 목적

마비노기 모바일 패킷 구조를 분석·역공학하기 위한 독립 오버레이 도구.
OverlayTimer와 소스 파일을 공유하되, 완전히 독립된 실행 파일로 동작한다.

---

## 기술 스택

- **언어/프레임워크**: C# / .NET 8 / WPF (`net8.0-windows`)
- **패킷 캡처**: SharpPcap + PacketDotNet (Npcap 기반)
- **프로젝트 위치**: `c:\OverlayTimer\PacketAnalyzer\`
- **솔루션**: OverlayTimer.sln (PacketAnalyzer 프로젝트 추가됨)

---

## 캡처 방향

- **S2C + C2S 모두** 캡처 (BPF 필터: `tcp port {targetPort}`)
- S2C: 프레임 파서(StartMarker/EndMarker)로 파싱 → 개별 패킷 추출
- C2S: 포맷 불명이므로 TCP 세그먼트 단위로 raw 덤프

---

## 주요 기능

### 녹화 토글
- 설정된 키(기본 F10)를 누르면 **IDLE ↔ RECORDING** 전환
- 오버레이 상태 바에 `● REC` / `○ IDLE` 표시
- RECORDING 시작 → 새 세션 생성
- RECORDING 종료 → 세션을 `dumps/` 폴더에 JSON 저장

### 세션 관리
- 앱 시작 시 `dumps/` 폴더의 기존 세션 파일 자동 로드
- 세션 목록은 최신순 정렬
- 세션 선택 → 우측 패킷 목록 표시

### 패킷 표시
- 각 패킷 행: `[HH:mm:ss.fff]  방향  타입명` 형식
- 알 수 없는 dataType: `UNKNOWN (12345)` (회색)
- C2S raw 세그먼트: 파란색 표시

### 패킷 상세 (Detail 패널)
- 알려진 타입: TypeName, 파싱된 필드 (userId, buffKey 등)
- 모든 패킷: 16진수 덤프 (16바이트씩)

---

## 프로젝트 구조

```
PacketAnalyzer/
├── PacketAnalyzer.csproj          # net8.0-windows, WPF, SharpPcap
├── config.json                    # 런타임 설정 (빌드 출력에 복사)
├── App.xaml / App.xaml.cs         # 진입점. config 로드, 스니퍼 시작, 창 생성
├── PacketAnalyzerConfig.cs        # config.json 역직렬화
├── PacketAnalyzerOverlayWindow.xaml/.cs  # 메인 오버레이 창
│
├── Description/
│   ├── IPacketDescriptor.cs       # 설명자 인터페이스
│   ├── PacketDescriptorRegistry.cs # dataType → 설명자 레지스트리
│   └── KnownDescriptors.cs        # BuffStart/End, EnterWorld, Damage, Attack 구현
│
├── Dump/
│   ├── PacketRecord.cs            # 개별 패킷 레코드 (방향, dataType, payload, 파싱 결과)
│   ├── DumpSession.cs             # 세션 (시작 시각, PacketRecord 목록)
│   ├── PacketDumper.cs            # 녹화 상태 머신 (IDLE ↔ RECORDING)
│   └── DumpStore.cs               # JSON 저장/로드 (dumps/ 폴더)
│
└── Net/
    ├── AnalyzerSniffer.cs         # SharpPcap 장치 열기/닫기 (S2C+C2S)
    └── AnalyzerCaptureWorker.cs   # 버퍼 관리, PacketStreamParser 호출
    # (file links from OverlayTimer)
    ├── Capture/PacketStreamParser.cs
    ├── Capture/DeviceSelector.cs
    ├── Packets/IPacketParser.cs + Packet*.cs
    └── Logging/LogHelper.cs
```

---

## 공유 소스 파일 (MSBuild file link)

PacketAnalyzer.csproj에서 OverlayTimer 소스 파일을 직접 링크함 (복사 아님):

| OverlayTimer 파일 | 설명 |
|---|---|
| `Net/Capture/PacketStreamParser.cs` | 프레임 마커 파싱 |
| `Net/Capture/DeviceSelector.cs` | NIC 선택 |
| `Net/Packets/IPacketParser.cs` | 파서 인터페이스 |
| `Net/Packets/PacketBuffStart.cs` | BuffStart 파서 |
| `Net/Packets/PacketBuffEnd.cs` | BuffEnd 파서 |
| `Net/Packets/PacketEnterWorld.cs` | EnterWorld 파서 |
| `Net/Packets/PacketDamage20897.cs` | Damage 파서 |
| `Net/Packets/PacketAttack20389.cs` | Attack 파서 |
| `Net/Logging/LogHelper.cs` | 로그 헬퍼 |

---

## IPacketDescriptor 인터페이스

```csharp
public interface IPacketDescriptor {
    int    DataType  { get; }
    string TypeName  { get; }
    string?  Describe(ReadOnlySpan<byte> payload);     // 사람이 읽는 필드 요약
    object?  ParsedObject(ReadOnlySpan<byte> payload); // JSON 직렬화용 객체
}
```

IPacketParser\<T\>는 변경 없이 유지. IPacketDescriptor는 PacketAnalyzer 전용으로 신규 추가.
알려진 5개 타입(BuffStart, BuffEnd, EnterWorld, Damage, Attack)은 KnownDescriptors.cs에 구현.

---

## config.json 형식

```json
{
  "network": {
    "targetPort": 16000,
    "captureFilter": null,
    "deviceName": null
  },
  "protocol": {
    "startMarker": "80 4E 00 00 00 00 00 00 00",
    "endMarker":   "12 4F 00 00 00 00 00 00 00"
  },
  "packetTypes": {
    "buffStart":  100054,
    "buffEnd":    100055,
    "enterWorld": 101059,
    "dpsAttack":  20389,
    "dpsDamage":  20897
  },
  "dumpToggleKey": "F10",
  "overlay": { "x": 200.0, "y": 200.0, "width": 750.0, "height": 550.0 }
}
```

`dumpToggleKey`: F1~F12 중 선택. 충돌 주의 (F9는 OverlayTimer 디버그 용도).

---

## dumps/ JSON 형식

파일명: `YYYY-MM-DD_HH-mm-ss.json`

```json
{
  "sessionStart": "2026-03-01T14:23:01.234Z",
  "sessionEnd":   "2026-03-01T14:25:01.234Z",
  "packets": [
    {
      "t":        "2026-03-01T14:23:01.456789Z",
      "dir":      "S2C",
      "type":     100054,
      "typeName": "BuffStart",
      "parsed":   "userId=12345678  buffKey=1590198662  duration=20.0s",
      "hex":      "785634120000000021BD4362..."
    }
  ]
}
```

---

## 오버레이 조작

| 동작 | 효과 |
|---|---|
| **토글 키 (F10)** | 녹화 시작/종료 (IDLE ↔ REC) |
| **Ctrl 누른 채 드래그** | 오버레이 이동 |
| **Ctrl + 테두리 드래그** | 오버레이 리사이즈 |
| Ctrl 뗌 | 클릭 투과 모드 복귀 (게임 클릭 통과) |

---

## 실행 선행 조건

- **[Npcap](https://npcap.com/)** 설치 필요
- **관리자 권한** 필요 (패킷 캡처)
- `config.json`의 `packetTypes` 값은 게임 업데이트 시 변경될 수 있음

---

## 데이터 흐름

```
NIC (Npcap)
  └─ AnalyzerSniffer       tcp port 16000 (S2C + C2S)
       └─ AnalyzerCaptureWorker
            ├─ S2C: PacketStreamParser  StartMarker~EndMarker 프레임 파싱
            │    └─ PacketDumper.OnParsedPacket("S2C", dataType, payload)
            └─ C2S: raw 세그먼트
                 └─ PacketDumper.OnParsedPacket("C2S", -1, rawPayload)

PacketDumper (IDLE/RECORDING 상태 머신)
  └─ RECORDING 중: DumpSession.Packets 에 추가
  └─ 종료 시: DumpStore.Save() → dumps/YYYY-MM-DD_HH-mm-ss.json

PacketAnalyzerOverlayWindow
  └─ SessionList → PacketList → Detail 패널
```
