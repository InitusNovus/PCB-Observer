# Manual Checklist — 사람+브라우저 전용 검증 항목

자율 실행이 증명하지 못한 것들(자동화는 전부 헤드리스로 대체 검증됨). 각 항목은 아래 재현 절차로 직접 확인한다.

## 재현 준비 (공통)

```powershell
Copy-Item "C:/Program Files/KiCad/10.0/share/kicad/demos/complex_hierarchy" "$env:TEMP/PCBObserverE2E2" -Recurse -Force
dotnet run --project C:/Dev/PCB-Observer/src/PcbObserver -- watch "$env:TEMP/PCBObserverE2E2/complex_hierarchy.kicad_pcb" --port 8765
# 콘솔에 출력된 실제 URL을 브라우저로 연다 (8765 점유 시 자동 폴백됨)
```

저장 시뮬레이션(관찰 중 다른 탭에서) — **과거/현재 렌더가 눈에 보이게 하려면 두 보드를 교대로**:
```powershell
$A = "C:/Program Files/KiCad/10.0/share/kicad/demos/complex_hierarchy/complex_hierarchy.kicad_pcb"
$B = "C:/Program Files/KiCad/10.0/share/kicad/demos/pic_programmer/pic_programmer.kicad_pcb"
Copy-Item $B "$env:TEMP/PCBObserverE2E2/complex_hierarchy.kicad_pcb" -Force   # 다음엔 $A로 교대
```

2026-09-27 갱신: 초기 뷰가 **보드 중심**에 센터링됨. 줌/팬/Front-Back/레이어 선택은 **브라우저 재오픈 후에도 복원**(localStorage). HISTORY 행은 `#seq · 해시8자리 · 캡처시각`을 표시해 동일 내용 번들도 구분 가능. 레이어 12종(구리 라디오 + Edge/Silk/Mask/Paste/Fab/Cmts 체크박스). 렌더 비용 조절: `--layers F.Cu,B.Cu,Edge.Cuts` 식으로 축소 가능.

| # | 항목 | 절차 | 통과 기준 |
|---|------|------|----------|
| AT-006 | 줌인 뷰포트 안정성 | 줌인(휠)+팬 후 교대 저장 반복 | 새 번들 표시 시 뷰포트(중심/배율) 유지, 리셋 없음 |
| AT-007 | HOLD 동작 | HOLD 클릭 → 저장 시뮬레이션 2회 | 화면 고정 + "HOLD · displaying #x · latest #y" 배지, 관찰은 계속(콘솔 캡처 로그) |
| AT-008 | Latest 복귀 | HOLD 상태에서 Latest 클릭 | 즉시 최신 완료 번들로 전환 |
| AT-013 | Front/Back 정렬 | 줌인 상태에서 Back 클릭 | 모든 보이는 레이어가 수평 미러로 함께 반전(층간 어긋남 없음). §11.2 경고: 개별 SVG 스택 정합성은 아직 미검증 병기 |
| AT-014 | 과거 뷰 배지 | HISTORY 열기 → **다른 해시**의 과거 번들 선택 | "HISTORY · displaying #x · latest #y" 배지 + 선택한 번들 화면 전환(해시/시각으로 구분) |
| AT-012(수동) | 브라우저 재접속 | 뷰어 탭 닫기 → 재오픈 | 재접속 즉시 현재 상태 조회 + **줌/팬/레이어 선택 복원**(Reset view 시 기본값) |
| FR-009 뷰어 반쪽 | 뷰포트 보존 | AT-006과 동일 | 동일 |
| FR-012 뷰어 반쪽 | LIVE 자동 추종 | 저장 시뮬레이션 후 손대지 않기 | 몇 초 내 "LIVE · #N" 배지 + 화면 갱신 |
| FR-013 뷰어 반쪽 | HOLD UI | AT-007과 동일 | 동일 |
| FR-015 뷰어 반쪽 | Latest 1-클릭 | AT-008과 동일 | 동일 |
| AT-005 뷰어 반쪽 | 1-틱 전환 | 저장 시뮬레이션 | 레이어들이 혼합 세대(일부 옛 번들)로 뜨는 프레임 없음 |
| 신규 | 레이어 토글 | Mask/Paste/Fab/Cmts 체크박스 토글 | 즉시 오버레이 반영(번들에 해당 레이어가 렌더된 경우) |
| 실보드 성능(§36) | 양산 보드 | 실제 `.kicad_pcb`로 watch | 체감 렌더 지연/메모리 기록 — 데모 측정치(스키매틱 1.01s/호출, PCB 12레이어 수 초)와 비교 |
## Schematic 뷰어 (watch-sch) — 브라우저 전용

재현: `dotnet run --project C:/Dev/PCB-Observer/src/PcbObserver -- watch-sch <임시 복사한 fixtures/sch/case_d_shared/root.kicad_sch>` 후 출력 URL 오픈.

| 항목 | 절차 | 통과 기준 |
|---|---|---|
| SCH-FR-011 시트별 뷰포트 | Channel A 줌인+팬 → Channel B 클릭(기본 뷰) → 다시 A | A의 줌/팬 기억 복원 |
| SCH-FR-012 무자동점프 | Channel A 보는 중 루트 파일 저장 | 시트 유지 + 배지 LIVE 갱신 |
| SCH-FR-004 공유 인스턴스 | Channel A/B/C 각각 클릭 | 3개가 개별 페이지로 표시 |
| 누락 자식 플래그 | mcu.kicad_sch 삭제 후 루트 저장 | 페이지 목록에 missing 플래그 |

## 자동으로 이미 증명된 것(참고 — 재확인 불필요)

- AT-001/003/004/009/010/011/002, FR-001..008/016/017/018, NFR-002/003/004/006/007/008: 단위 테스트 30개 + 헤드리스 E2E(교체 저장, 누락 CLI 오류·복구, 소스 무변경, 강제 종료 후 편집, 포트 폴백) + 레드팀 7 시나리오.
- 매니페스트 기반 번들 완전성: 구 5레이어 번들(1–15)이 신바이너리에서도 전부 complete (2026-09-27 실측).
