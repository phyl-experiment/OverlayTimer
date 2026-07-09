# SkillMapper — 스킬 슬롯 ↔ Key1 매퍼 명세

## 목적

마비노기 모바일 우하단 전투 UI(6 버튼)의 각 **슬롯**을 데미지 패킷에 실린
스킬 ID(`PacketAttack20389.Key1`)와 자동으로 연결한다.

스킬의 한글 이름은 OCR/LLM 없이 슬롯 번호(`스킬1` ~ `스킬6`)로만 라벨링하며,
캐릭터별로 별도 프로파일에 저장한다. 캐릭터를 갈아끼우면 새 프로파일을 만들거나
기존 프로파일을 다시 학습한다.

---

## 기술 스택 / 위치

- **언어/프레임워크**: C# / .NET 8 / WPF (`net8.0-windows`)
- **패킷 캡처**: SharpPcap + PacketDotNet (Npcap 기반) — OverlayTimer/PacketAnalyzer와 동일
- **프로젝트 위치**: `c:\OverlayTimer\SkillMapper\` (별도 프로젝트)
- **솔루션**: `OverlayTimer.sln`에 추가
- **공유 방식**: PacketAnalyzer와 동일한 MSBuild 파일 링크. **소스 복사 금지.**

---

## 캡처 방향

- **S2C만** 캡처. (C2S는 암호화되어 의미 없음.)
- 관심 패킷 타입:
  - `PacketAttack20389` → `Key1`(스킬 ID), `UserId`, `Flags`
  - `PacketEnterWorld` → 캐릭터 컨텍스트 검증용 (옵션)
- 비관심 패킷은 그냥 흘려보낸다.

---

## 핵심 설계 — 1회 캘리브레이션 + 트리거 세션

### 캘리브레이션 (1회 / 에뮬레이터 창 단위)

1. 사용자가 에뮬레이터 창을 picker로 선택.
2. 우하단 6 버튼 영역을 **사용자가 드래그로 6개 사각형 마킹**.
   - 정확한 격자 자동 추정은 1차 범위 밖. 단순 드래그-앤-드롭만 지원.
   - 좌표는 **클라이언트 좌표**(window-relative)로 저장 → 창 이동/리사이즈에 따라가게 함.
3. 슬롯 번호는 **게임 내 표기 번호 그대로** 부여. 아래 [참고 레이아웃](#참고-레이아웃-1920x1080-우하단-기준) 섹션 참조.
   - 아래줄 좌→우: `스킬1`, `스킬2`, `스킬3`
   - 윗줄 좌→우: `스킬4`, `스킬5`, `스킬6`
   - 이 순서는 게임 UI에 그려진 ①~⑥과 일치한다.
4. 결과를 `buttons.json`에 저장 (캐릭터 무관, 에뮬레이터 창 단위 고정).
5. 캘리브레이션은 한 번만. UI 위치가 바뀌었을 때만 재실행.

### 매핑 대상이 아닌 버튼

게임 UI에는 6 스킬 외에 두 개 버튼이 더 있는데, **둘 다 캘리브레이션·매핑 대상에서 제외**한다:

- **ASSIST (H)** — 우상단 작은 토글. 보조 기능이라 `Key1` 발사와 직접 대응되지 않음.
- **Space (큰 검 아이콘)** — 평타 버튼. `Key1 == 0` 으로 도착하므로 어차피 상관관계 엔진 단계에서 자동 제외됨 ([상관관계 엔진](#상관관계-엔진) 참조).

두 버튼의 좌표는 `buttons.json`에 기록하지 않는다. 이 영역에서 발생한 클릭은 6 사각형 어디에도 속하지 않으므로 자연히 무시된다.

### 학습 세션 (캐릭터 단위, 명시적 시작/끝)

1. 사용자가 캐릭터 프로파일을 **선택하거나 새로 생성** (이름은 임의 문자열).
2. `[학습 시작]` 버튼 클릭 → 세션 진입.
   - `MouseHook` 가동: 글로벌 좌클릭 감지 → 6 사각형 중 어디에 들어왔는지 판정.
   - 패킷 쪽: `PacketAttack20389` 도착마다 `(Key1, UtcNow)` 기록.
3. 사용자가 던전·필드에서 평소대로 스킬을 사용. 클릭/패킷이 누적됨.
4. `[학습 끝]` 버튼 클릭 → 세션 종료.
   - 누적 데이터를 통계적으로 매칭 (아래 "상관관계 엔진" 참조).
   - 결과를 `profiles/<캐릭터명>.json`에 저장 (덮어쓰기 또는 병합).
5. 캐릭터를 갈아끼우면 다른 프로파일로 전환 후 재학습.

> 학습 도중 끄거나 취소 가능. 누적된 데이터는 "끝" 클릭 시점에만 파일에 반영.

---

## UI 구성 (단일 창)

```
┌──────────────────────────────────────────────────────┐
│ [에뮬레이터 창 선택 ▼]   [캘리브레이션]               │
├──────────────────────────────────────────────────────┤
│ 프로파일: [메인탱커 ▼]  [+ 새 프로파일]              │
├──────────────────────────────────────────────────────┤
│  ● 학습 중  /  ○ 대기                                 │
│  [학습 시작]   [학습 끝]                              │
├──────────────────────────────────────────────────────┤
│  슬롯  │ 매핑된 Key1   │ 샘플 │ 신뢰도 │ 마지막 갱신 │
│  스킬1 │ 12345         │  18  │ 0.95   │ 12:03       │
│  스킬2 │ (미학습)      │   0  │ —      │ —           │
│  ...                                                  │
└──────────────────────────────────────────────────────┘
```

- 학습 중에는 클릭/패킷 도착 카운터를 실시간으로 표시(디버그용).
- 매핑 표는 학습이 끝난 직후 갱신.

---

## 상관관계 엔진

### 입력
- `clicks`: `[(slotIndex, clickedAtUtc), ...]`
- `attacks`: `[(key1, receivedAtUtc), ...]` — `Key1 == 0` (평타·내부타격)은 제외.

### 윈도우 매칭
- 각 클릭 `c`에 대해, `[c.time, c.time + W]` 구간 내 도착한 attack을 후보로 모은다.
- 기본 `W = 600ms`. config로 조정 가능.
- 같은 attack은 가장 가까운 click에만 한 번 귀속(시간순 그리디).

### 슬롯별 결정 규칙
- 슬롯 `s`에 매칭된 `key1`들의 빈도수를 집계.
- 1순위 후보의 비율이 다음 조건을 만족하면 확정:
  - `count(top) >= MinSamples` (기본 5)
  - `count(top) >= 2 × count(secondTop)` (압도적 다수)
- 미달이면 "미학습" 상태 유지. 다음 세션에서 누적 가능 (병합 모드일 때).

### DOT/지속피해 필터
- 동일 `Key1`이 짧은 간격으로 반복(<200ms)되면 첫 건만 후보로 사용.
- 한 클릭에 여러 attack이 도착하는 일반적인 멀티히트는 그대로 카운트.

### 평타/추가타 처리
- `Key1 == 0`은 매핑 대상 아님. 클릭이 그 시간대에 있어도 attack 후보에 포함하지 않는다.
- 결과적으로 평타 슬롯이 있다면 "미학습"으로 남는다 (의도된 동작).

---

## 파일 포맷

### `buttons.json` (에뮬레이터 창 단위, 1회 작성)

좌표는 에뮬레이터 창의 **클라이언트 영역 기준**. 슬롯 번호는 게임 내 ①~⑥ 표기와 동일.

```json
{
  "windowTitlePattern": "LDPlayer",
  "clientSize": { "w": 1920, "h": 1080 },
  "calibratedAt": "2026-04-25T13:01:00Z",
  "buttons": [
    { "slot": 1, "rect": { "x": 1390, "y":  925, "w": 80, "h": 80 } },
    { "slot": 2, "rect": { "x": 1505, "y":  945, "w": 70, "h": 70 } },
    { "slot": 3, "rect": { "x": 1620, "y":  945, "w": 70, "h": 70 } },
    { "slot": 4, "rect": { "x": 1395, "y":  720, "w": 75, "h": 75 } },
    { "slot": 5, "rect": { "x": 1510, "y":  720, "w": 75, "h": 75 } },
    { "slot": 6, "rect": { "x": 1620, "y":  725, "w": 70, "h": 70 } }
  ]
}
```

위 값은 1920×1080 / LDPlayer 기준 **참고치**다. 실제 환경(에뮬레이터, DPI, UI 스킨)에 따라
픽셀 단위로 어긋나므로 캘리브레이션 단계에서 사용자가 직접 마킹한 결과가 진실 데이터가 된다.

### `profiles/<캐릭터명>.json`

```json
{
  "character": "메인탱커",
  "lastTrainedAt": "2026-04-25T13:30:00Z",
  "mappings": {
    "스킬1": { "key1": 12345, "samples": 18, "confidence": 0.95 },
    "스킬2": { "key1": 23456, "samples":  9, "confidence": 0.82 },
    "스킬3": null,
    "스킬4": { "key1": 34567, "samples":  6, "confidence": 0.75 },
    "스킬5": null,
    "스킬6": null
  }
}
```

- `null` = 미학습.
- `samples` / `confidence`는 누적값. 다음 세션에서 같은 슬롯이 다른 `key1`을 강하게 가리키면 덮어씀.

---

## 참고 레이아웃 (1920x1080 우하단 기준)

사용자가 제공한 우하단 캡처본에서 추출한 구조. 전 캐릭터 공통 UI라서 `buttons.json` 초기값으로
이 값을 제안하고, 캘리브레이션 화면이 처음 열릴 때 가이드 박스로 그려줘도 좋다.

```
                         (윗줄)
              ┌────┐    ┌────┐    ┌────┐
              │ ④ │    │ ⑤ │    │ ⑥ │     [ASSIST]
              │가르│    │생기│    │필사│
              │ 기 │    │폭발│    │일격│
              └────┘    └────┘    └────┘
                                              ┌──────┐
              ┌────┐    ┌────┐    ┌────┐      │      │
              │ ① │    │ ② │    │ ③ │      │ Space│
              │회전│    │라이│    │어깨│      │ (평타)│
              │ 베기│    │ 스 │    │ 치기│      │      │
              └────┘    └────┘    └────┘      └──────┘
                         (아래줄)
```

- **6 스킬 버튼**: 가로 3 × 세로 2 배치. 게임 내 번호 그대로 슬롯 번호로 사용.
- 우측에 별도로 **ASSIST (H 키)** 토글과 **Space (평타)** 큰 버튼이 붙어 있음 — 매핑 대상 아님.
- 1920×1080 + 우하단 정렬일 때 6 버튼 영역은 대략 클라이언트 좌표 `x∈[1380,1700]`,
  `y∈[700, 1010]` 박스 안에 들어온다.
- 버튼은 정사각형이 아닌 원형 아이콘이지만 마킹은 외접 사각형(rect)으로 충분.
  히트박스는 마우스 후킹 단계에서 rect로 판정한다.
- 일부 슬롯은 활성 시 화염 이펙트가 외곽으로 튀어나온다. 마킹 사각형은 **이펙트가 아닌
  아이콘 원의 외접 사각형**에 맞춘다(이펙트까지 잡으면 옆 슬롯과 겹친다).

---

## 프로젝트 구조

```
SkillMapper/
├── SkillMapper.csproj             # net8.0-windows, WPF, SharpPcap
├── config.json                    # 런타임 설정 (포트, 윈도우, 매칭 W 등)
├── App.xaml / App.xaml.cs         # 진입점. 스니퍼 시작, 메인 창 생성
├── SkillMapperConfig.cs           # config.json 역직렬화
├── MainWindow.xaml/.cs            # 단일 메인 창 (캘리브레이션/세션/표)
│
├── Calibration/
│   ├── ButtonRect.cs              # 슬롯 사각형 모델
│   ├── ButtonStore.cs             # buttons.json 저장/로드
│   └── CalibrationOverlay.xaml/.cs # 드래그 캘리브레이션 오버레이 창
│
├── Hooks/
│   └── MouseHook.cs               # WH_MOUSE_LL 글로벌 후킹 (좌클릭만)
│
├── Mapping/
│   ├── ClickEvent.cs              # (slotIndex, time)
│   ├── AttackEvent.cs             # (key1, time)
│   ├── CorrelationEngine.cs       # 윈도우 매칭 + 슬롯별 다수결
│   ├── SkillProfile.cs            # 캐릭터 프로파일 모델
│   └── ProfileStore.cs            # profiles/*.json 저장/로드
│
├── Session/
│   └── TrainingSession.cs         # 시작/끝 상태 머신, 클릭/패킷 큐 누적
│
├── Net/
│   ├── SkillMapperSniffer.cs      # SharpPcap (S2C만)
│   ├── SkillMapperCaptureWorker.cs # 버퍼 → PacketStreamParser
│   └── AttackPacketSink.cs        # parsed Attack20389 → Session.OnAttack
│   # (file links from OverlayTimer)
│   ├── Capture/PacketStreamParser.cs
│   ├── Capture/DeviceSelector.cs
│   ├── Packets/IPacketParser.cs
│   ├── Packets/PacketAttack20389.cs
│   └── Logging/LogHelper.cs
│
└── Utils/
    └── WindowCoords.cs            # FindWindow / GetClientRect / ScreenToClient
```

### 파일 링크 항목 (`SkillMapper.csproj` 발췌)

```xml
<ItemGroup>
  <Compile Include="..\OverlayTimer\Net\Capture\PacketStreamParser.cs"
           Link="Net\Capture\PacketStreamParser.cs" />
  <Compile Include="..\OverlayTimer\Net\Capture\DeviceSelector.cs"
           Link="Net\Capture\DeviceSelector.cs" />
  <Compile Include="..\OverlayTimer\Net\Packets\IPacketParser.cs"
           Link="Net\Packets\IPacketParser.cs" />
  <Compile Include="..\OverlayTimer\Net\Packets\PacketAttack20389.cs"
           Link="Net\Packets\PacketAttack20389.cs" />
  <Compile Include="..\OverlayTimer\Net\Logging\LogHelper.cs"
           Link="Net\Logging\LogHelper.cs" />
</ItemGroup>
```

`PacketStreamParser`는 PacketAnalyzer 시점에서 이미 `Action<int, byte[]>` 콜백 시그니처로 일반화되어 있으므로 그대로 재사용 가능.

---

## 왜 별도 프로젝트인가

- OverlayTimer는 라이브 오버레이(타이머/DPS) 본업이 있고, 매핑은 **선택적·일회성** 도구라 본체에 들어가면 메뉴/설정만 비대해진다.
- PacketAnalyzer는 "패킷 구조 역공학"용이라 이번 도구의 UX(전역 마우스 후킹, 캘리브레이션 오버레이)와 결이 다르다.
- 셋 다 패킷 파이프라인을 공유하지만 **창과 입력 후킹이 독립**이라 별도 exe가 깔끔.

---

## OverlayTimer로 결과 환류 (후속 단계, 본 명세 범위 밖)

- 생성된 `profiles/<캐릭터명>.json`을 OverlayTimer가 읽도록 만들면, DPS 표/벤치마크 결과의 `SkillType` 컬럼을 hex 대신 `스킬1 (12345)` 형태로 표기 가능.
- 환류는 별도 작업으로 분리. 이 명세는 **매핑 생성기**까지만 다룬다.

---

## 단계별 구현 순서

| Phase | 산출물 | 핵심 검증 |
|---|---|---|
| 0 | 프로젝트 셸 + sln 추가 + 파일 링크 | 빌드 통과, OverlayTimer/PacketAnalyzer와 공존 |
| 1 | S2C 캡처 → PacketAttack20389 디코드 → 카운터 표시 | `Key1`이 표에 실시간으로 찍힘 |
| 2 | 에뮬레이터 창 picker + 클라이언트 좌표 변환 | 창 이동해도 좌표가 따라옴 |
| 3 | 드래그 캘리브레이션 오버레이 + `buttons.json` | 6 사각형 저장/로드 |
| 4 | `MouseHook` + 클릭 → 슬롯 판정 + 큐 누적 | 클릭 표시기에 정확한 슬롯 점등 |
| 5 | 학습 세션 상태 머신 (시작/끝, 큐 동기화) | 시작 후 켜지고 끝나면 멈춤 |
| 6 | `CorrelationEngine` + 프로파일 저장/병합 | 단일 스킬 반복 클릭으로 매핑 1건 확정 |
| 7 | 표/신뢰도 UI 다듬기, 다중 프로파일 전환 | 캐릭터 갈아끼우는 워크플로우 검증 |

---

## 미해결 / 추후 결정 사항

- **다중 모니터 / DPI 스케일**: 글로벌 마우스 좌표 → 에뮬레이터 클라이언트 좌표 변환 시 DPI 인지 필요. 1차 구현은 100% DPI 가정.
- **에뮬레이터 윈도우 식별**: `Process.MainWindowTitle` 부분일치로 시작. LDPlayer 다중 인스턴스 환경은 추후.
- **세션 도중 캐릭터 변경 감지**: `PacketEnterWorld` 발생 시 자동으로 학습 일시중단/경고 (옵션, Phase 7 이후).
- **병합 vs 덮어쓰기**: 기본은 "기존 매핑이 있어도 새 세션 결과가 더 강하면 덮어씀". 명시적 `[프로파일 초기화]` 버튼 제공.
