# OverlayTimer - Project Overview

## 목적

마비노기 모바일의 네트워크 패킷을 캡처하여, 게임 클라이언트에 직접적인 영향을 주지 않는 정보성 기능을 오버레이 형태로 제공하는 도구.

현재는 **각성 버프 타이머**와 **DPS 계산** 기능을 함께 제공한다.

---

## 기술 스택

- **언어/프레임워크**: C# / .NET 8 / WPF (`net8.0-windows`)
- **패킷 캡처**: SharpPcap + PacketDotNet (Npcap 기반)
- **빌드**: Visual Studio (OverlayTimer.sln)

---

## 관련 문서

- [DPS 기능 명세](DPS_SPEC.md)
- [패킷 분석기 명세](PACKET_ANALYZER_SPEC.md)
- [오버레이 공통 운영 메모](OVERLAY_OPERATION.md)
- [Protocol Auto-Discovery Fallback 계획](memory/protocol-probe-plan.md)

---

## 프로젝트 구조

```
OverlayTimer/
├── config.json                     # 핵심 상수 설정 파일 (빌드 출력 폴더에 복사됨)
├── AppConfig.cs                    # config.json 역직렬화 클래스들
├── App.xaml / App.xaml.cs          # 진입점. config 로드, SnifferService 시작, 창 생성
├── OverlayTimerWindow.xaml/.cs     # 투명 전체화면 오버레이 창
│
└── Net/
    ├── SnifferService.cs           # SharpPcap 장치 열기/패킷 수신 루프
    ├── DeviceSelector.cs           # 최적 네트워크 어댑터 선택
    ├── CaptureWorker.cs            # TCP 방향별 버퍼 관리 및 파싱 위임
    ├── PacketStreamParser.cs       # 프레임 마커 탐색 + 패킷 헤더 파싱
    ├── PacketHandler.cs            # 파싱된 패킷 분류 및 트리거 결정
    ├── PacketBuffStart.cs          # BuffStart 패킷 구조체 파서
    ├── SelfIdResolver.cs           # 내 캐릭터 ID 추출 (EnterWorld 패킷)
    ├── OverlayTimerTrigger.cs      # Active→Cooldown 페이즈 타이머 + UI 갱신
    ├── ITimerTrigger.cs            # 트리거 인터페이스 (On)
    └── LogHelper.cs                # 파일+콘솔 로그 (기본 비활성화)
```

---

## 데이터 흐름

```
NIC (Npcap)
  └─ SnifferService       tcp src port 16000 필터
       └─ CaptureWorker   S2C 버퍼 누적
            └─ PacketStreamParser   StartMarker~EndMarker 프레임 파싱
                 └─ PacketHandler   buffKey 필터 → 내 캐릭터 확인
                      └─ OverlayTriggerTimer   On() → Active 20s → Cooldown
                           └─ OverlayTimerWindow   SetMode/SetTime/SetProgress
```

---

## 핵심 상수 / 프로토콜 메모

| 항목 | 값 | confirmed | 설명 |
|---|---|---|---|
| 서버 포트 | `16000` | — | 캡처 필터 (`tcp src port 16000`) |
| StartMarker | `83 4E 00 00 00 00 00 00 00` | ✅ | 프레임 시작 |
| EndMarker | `1A 4F 00 00 00 00 00 00 00` | ✅ | 프레임 종료 |
| 패킷 헤더 | 9바이트 (dataType:4 + length:4 + encodeType:1) | — | |
| EnterWorldType | `110022` | ✅ | 내 캐릭터 ID 추출용 |
| ReadyToEnterWorldB | `110540` | ❌ | EnterWorld 직전 게이팅 패킷 (자동 탐색 대상) |
| BuffStart DataType | `110055` | ✅ | 각성 버프 시작 패킷 |
| BuffEnd DataType | `110056` | ✅ | 각성 버프 종료 패킷 |
| DpsAttack DataType | `20408` | ✅ | 공격 패킷 |
| DpsDamage DataType | `20937` | ✅ | 데미지 패킷 |
| lightKey | `1590198662` | — | 각성 버프 키 |
| mountainLordKey | `2024838942` | — | 각성 버프 키 (산군) |
| Active 지속시간 | 20초 | — | |
| Cooldown 선택지 | 32초 / 70초 | — | Ctrl+우클릭으로 토글 |

---

## 오버레이 조작

| 동작 | 효과 |
|---|---|
| **Ctrl 누른 채 좌클릭 드래그** | 오버레이 창 이동 |
| **Ctrl+우클릭** | 쿨다운 시간 32s↔70s 토글 |
| Ctrl 뗌 | 클릭 투과 모드 복귀 |

---

## 실행 선행 조건

- **[Npcap](https://npcap.com/)** 설치 필요 (WinPcap API 호환 모드 권장). 미설치 시 "No capture devices found" 오류 발생.
- 관리자 권한으로 실행해야 패킷 캡처가 정상 동작함.

---

## 개발 유의사항

- 로깅은 기본 비활성화(`LogHelper.Disabled = true`). 디버깅 시 `false`로 전환하면 `logs/` 폴더에 기록됨.
- 패킷 캡처에는 **Npcap**이 설치되어 있어야 함 (관리자 권한 필요).
- `OverlayTriggerTimer`가 타이머/UI 라이프사이클 전체를 담당함. `OverlayTimerWindow`는 표시만 책임짐.
- 게임 클라이언트에 어떤 패킷도 송신하지 않으며, 캡처(읽기)만 수행함.
- **dataType 번호는 게임 업데이트 시 변경될 수 있음.** 동작이 멈추면 `config.json`의 `packetTypes`, `buffKeys` 값을 재확인할 것.
- 2026-03-28 PacketAnalyzer release dump 기준 `buffStart=100055`, `buffEnd=100056`가 확인됨. `100054`는 길이가 57~97바이트로 크게 흔들리는 다른 패킷이며, 과거 shape heuristic에서 `buffStart`로 오탐될 수 있었다.
- 2026-04-25 기준 네트워크 변경 반영: `startMarker=83 4E 00 00 00 00 00 00 00`, `endMarker=1A 4F 00 00 00 00 00 00 00`, `dpsDamage=20937`를 기본값으로 기록했다. 현재 기본 설정에서는 `start/end marker`, `dpsDamage`만 `confirmed: true`이고 나머지 packetTypes는 재확인 전 `confirmed: false`로 둔다.
- 2026-04-26 기준 EnterWorld self-id 게이팅 패킷 `readyToEnterWorldB`(기본값 110540)를 `packetTypes`에 추가했다. 35-byte 고정 페이로드 shape signature로 자동 탐색됨. 자세한 분석은 `memory/protocol-probe-plan.md`의 "ReadyToEnterWorld 분석" 섹션 참고. 짝 패킷인 110539(`ReadyToEnterWorldA`)는 다른 dataType(60008)과 동일한 payload schema를 공유하므로 probe 대상에서 제외하고 `SelfIdResolver`에 hardcoded best-effort로 둔다.
- **confirmed 플래그**: `config.json`의 `protocol`과 `packetTypes` 각 항목은 `{ "confirmed": bool, "value": ... }` 형식이다. `confirmed: true`인 값은 자동 프로브 대상에서 제외되며, `confirmed: false`인 값만 런타임에 자동 탐색된다. 값을 확정했으면 `confirmed: true`로 변경하여 불필요한 프로브를 방지할 것.

---

## DPS 기능 반영 (config 기반)

- 이제 오버레이는 `각성 타이머` + `DPS` 두 기능을 지원함.
- 두 기능 모두 `config.json`에서 제어 가능함.
- 표시 On/Off: `overlays.timer.enabled`, `overlays.dps.enabled`
- 시작 위치: `overlays.timer.x`, `overlays.timer.y`, `overlays.dps.x`, `overlays.dps.y`
- 관련 dataType 설정: `packetTypes.buffStart`, `packetTypes.enterWorld`, `packetTypes.dpsAttack`, `packetTypes.dpsDamage` (각각 `{ "confirmed": bool, "value": int }` 형식)

### 트레이에서 하나만 보이게 하기

- 재시작 없이 임시로 한 개만 표시하려면 트레이 메뉴에서 숨길 쪽 항목을 클릭.
- 타이머만 숨기기: `타이머 최소화` 클릭 (다시 누르면 `타이머 복원`)
- DPS만 숨기기: `DPS 최소화` 클릭 (다시 누르면 `DPS 복원`)
- 항상 하나만 시작하려면 `config.json`에서 반대쪽 `enabled`를 `false`로 설정.
