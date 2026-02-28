# 2분 DPS 벤치마크 기능 명세

## 목적

DPS 오버레이에 정해진 2분간의 측정 세션을 추가한다.
버튼 클릭 후 첫 데미지 패킷 수신 시점부터 정확히 120초를 측정하고,
완료 시 결과를 JSON으로 자동 저장하며 나중에 기록을 열람할 수 있다.

---

## UI 변경

### DPS 오버레이 헤더 버튼 행

```
현재:  [TOTAL DPS]  ················  [초기화]
변경:  [TOTAL DPS]  ···  [초기화]  [2분 DPS]  [기록 확인]
```

- 세 버튼은 헤더 우측에 나란히 배치한다.
- `[2분 DPS]` 버튼은 측정 상태에 따라 텍스트와 색상이 변한다.

### 측정 상태별 버튼/UI 표시

| 상태 | `[2분 DPS]` 버튼 텍스트 | 버튼 강조 | MetaText 아래 추가 표시 |
|---|---|---|---|
| `Idle` | `2분 DPS` | 기본 | 없음 |
| `Armed` | `대기중…` | 주황색 테두리 | `⏳ 첫 패킷 대기 중` (주황) |
| `Running` | `1:35` (경과 시간) | 초록색 테두리 | `📊 2분 측정 중  1:35 / 2:00` (초록) |
| `Done` | `2분 DPS` | 기본 | 없음 |

- Armed/Running 상태에서 버튼을 다시 클릭하면 측정을 취소하고 Idle로 복귀한다.
- 경과 시간 표시 형식: `M:SS` (예: `0:05`, `1:35`, `2:00`)

---

## 측정 세션 상태 머신

```
Idle
  ↓  [2분 DPS 버튼 클릭]
Armed  ← 첫 데미지 패킷을 기다리는 상태
  ↓  [첫 AddDamage 호출]
Running  ← 120초 카운트다운
  ↓  [120초 경과]
Done  →  JSON 자동 저장 → 기록 목록에 추가 → Idle 복귀
```

- Armed 상태에서는 기존 DpsTracker에 데미지가 누적되지 않는다.
  세션 전용 내부 `DpsBenchmarkSession`에만 누적된다.
- Running 진입 시 세션 자체의 시작 시각을 고정하고, 동시에 기존 DpsTracker는 Reset한다.
  (메인 DPS 표시와 벤치마크 세션이 함께 초기화되어 같은 시점에서 출발한다.)
- Done 전환 시 세션 데이터를 스냅샷으로 고정하고 JSON을 저장한다.

---

## 신규 파일

### `Net/DpsBenchmarkSession.cs`

```
enum BenchmarkState { Idle, Armed, Running, Done }

sealed class DpsBenchmarkSession
  - State: BenchmarkState
  - StartUtc: DateTime (Running 진입 시각)
  - ElapsedSeconds: double (Running 중 실시간 갱신)
  - 내부 누적 버퍼 (DpsTracker와 동일한 로직)

  + Arm()          → Idle → Armed
  + Cancel()       → 어느 상태든 → Idle
  + OnDamage(...)  → Armed이면 Running으로 전환 후 데이터 기록
                    Running이면 데이터 기록
                    Done/Idle이면 무시
  + Tick()         → Running 중 120초 초과 체크 → Done 전환 + 결과 반환
  + GetElapsed()   → M:SS 문자열 반환
```

### `Net/DpsBenchmarkRecord.cs`

완료된 세션의 결과 데이터 (JSON 직렬화용).

```csharp
class DpsBenchmarkRecord
{
    DateTimeOffset RecordedAt;     // 측정 완료 시각
    double DurationSeconds;        // 실제 측정 시간 (≤ 120.0)
    long   TotalDamage;
    double TotalDps;
    long   HitCount;
    long   CritCount;
    long   AddHitCount;
    long   PowerCount;
    long   FastCount;

    List<BenchmarkTargetEntry> Targets;
    List<BenchmarkSkillEntry>  Skills;
    List<BenchmarkBuffEntry>   Buffs;
}

class BenchmarkTargetEntry  { ulong TargetId; long Damage; double Dps; }
class BenchmarkSkillEntry   { uint SkillType; string SkillName; long Damage;
                               double DamageRatio; long HitCount;
                               double CritRate; double AddHitRate;
                               double PowerRate; double FastRate; }
class BenchmarkBuffEntry    { uint BuffKey; string BuffName;
                               double TotalSeconds; double UptimePct; }
```

### `DpsBenchmarkStore.cs`

JSON 파일 로드/저장 담당.

- 저장 폴더: 실행 파일 위치 기준 `dps_records/`
- 파일명: `dps_20260228_142300.json` (완료 시각 기준)
- `Save(DpsBenchmarkRecord)` → JSON 직렬화 후 파일 저장
- `LoadAll()` → 폴더 내 모든 `.json` 파일을 읽어 목록 반환 (최신순 정렬)

---

## JSON 파일 형식 예시

```json
{
  "recordedAt": "2026-02-28T14:23:00+09:00",
  "durationSeconds": 120.0,
  "totalDamage": 1234567890,
  "totalDps": 10288065.75,
  "hitCount": 1500,
  "critCount": 750,
  "addHitCount": 200,
  "powerCount": 100,
  "fastCount": 80,
  "targets": [
    { "targetId": 12345, "damage": 900000000, "dps": 7500000.0 }
  ],
  "skills": [
    {
      "skillType": 101,
      "skillName": "파이어볼",
      "damage": 500000000,
      "damageRatio": 40.5,
      "hitCount": 300,
      "critRate": 55.0,
      "addHitRate": 10.0,
      "powerRate": 20.0,
      "fastRate": 15.0
    }
  ],
  "buffs": [
    {
      "buffKey": 1590198662,
      "buffName": "각성",
      "totalSeconds": 80.0,
      "uptimePct": 66.7
    }
  ]
}
```

---

## 기록 확인 창 (`DpsRecordViewerWindow.xaml/.cs`)

별도의 일반 WPF 창 (투명 오버레이 아님).

### 레이아웃

```
┌─────────────────────────────────────────────┐
│  DPS 기록 확인                         [닫기] │
├─────────────────┬───────────────────────────┤
│ 2026-02-28      │  [선택 레코드 상세]          │
│ 14:23  10,288만  │  측정 시간: 120.0s           │
│ 총 12.3억        │  DPS: 10,288만               │
│─────────────────│  총 데미지: 12.3억            │
│ 2026-02-27      │                             │
│ 20:01   8,120만  │  ▼ 대상별 데미지             │
│ 총  9.7억        │  Target 12345   9.0억        │
│─────────────────│  Target 67890   3.3억        │
│ ...             │                             │
│                 │  ▼ 스킬별 통계               │
│                 │  파이어볼  40.5%  (300타)     │
│                 │  크리:55%  추가:10%  ...      │
│                 │                             │
│                 │  ▼ 버프 가동률               │
│                 │  각성  80.0s  66.7%          │
└─────────────────┴───────────────────────────┘
```

- 좌측: 기록 목록 (최신순). 각 항목에 날짜/시간, DPS, 총 데미지 표시.
- 우측: 선택된 레코드의 상세 정보.
- 기록이 없으면 "저장된 기록이 없습니다." 표시.
- `[기록 확인]` 버튼을 누를 때마다 창을 새로 열거나 기존 창을 포커스.

---

## 기존 파일 변경

| 파일 | 변경 내용 |
|---|---|
| `DpsOverlayWindow.xaml` | 헤더에 `[2분 DPS]`, `[기록 확인]` 버튼 추가. 측정 상태 표시용 TextBlock 추가. |
| `DpsOverlayWindow.xaml.cs` | `DpsBenchmarkSession` 연결. `RefreshUi`에 세션 상태 반영. 버튼 이벤트 핸들러 추가. 완료 시 `DpsBenchmarkStore.Save` 호출. |
| `Net/PacketHandler.cs` | `AddDamage` 호출 시 세션에도 `OnDamage` 전달. |
| `App.xaml.cs` | `DpsBenchmarkSession`과 `DpsBenchmarkStore` 인스턴스 생성 후 `DpsOverlayWindow`에 주입. |

---

## 구현 순서

1. `Net/DpsBenchmarkRecord.cs` — 데이터 구조 정의
2. `Net/DpsBenchmarkSession.cs` — 세션 상태 머신
3. `DpsBenchmarkStore.cs` — JSON 파일 입출력
4. `DpsOverlayWindow.xaml` / `.cs` — UI 버튼 + 상태 표시 + 완료 처리
5. `Net/PacketHandler.cs` — `OnDamage` 연결
6. `App.xaml.cs` — 인스턴스 연결
7. `DpsRecordViewerWindow.xaml` / `.cs` — 기록 확인 창

---

## 관련 파일 목록

- `Net/DpsBenchmarkRecord.cs` (신규)
- `Net/DpsBenchmarkSession.cs` (신규)
- `DpsBenchmarkStore.cs` (신규)
- `DpsRecordViewerWindow.xaml` (신규)
- `DpsRecordViewerWindow.xaml.cs` (신규)
- `DpsOverlayWindow.xaml` (수정)
- `DpsOverlayWindow.xaml.cs` (수정)
- `Net/PacketHandler.cs` (수정)
- `App.xaml.cs` (수정)
