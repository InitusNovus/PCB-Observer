# Manual Checklist — 사람+브라우저 전용 검증 항목

자율 실행이 증명하지 못한 것들(자동화는 전부 헤드리스로 대체 검증됨). 각 항목은 아래 재현 절차로 직접 확인한다.

## 재현 준비 (공통)

```powershell
Copy-Item "C:/Program Files/KiCad/10.0/share/kicad/demos/complex_hierarchy" "$env:TEMP/PCBObserverE2E2" -Recurse -Force
dotnet run --project C:/Dev/PCB-Observer/src/PcbObserver -- watch "$env:TEMP/PCBObserverE2E2/complex_hierarchy.kicad_pcb" --port 8765
# 콘솔에 출력된 실제 URL을 브라우저로 연다 (8765 점유 시 자동 폴백됨)
```

저장 시뮬레이션(관찰 중 다른 탭에서):
```powershell
Copy-Item "C:/Program Files/KiCad/10.0/share/kicad/demos/pic_programmer/pic_programmer.kicad_pcb" "$env:TEMP/PCBObserverE2E2/complex_hierarchy.kicad_pcb" -Force
```

| # | 항목 | 절차 | 통과 기준 |
|---|------|------|----------|
| AT-006 | 줌인 뷰포트 안정성 | 줌인(휠)+팬 후 위 저장 시뮬레이션 반복 | 새 번들 표시 시 뷰포트(중심/배율) 유지, 리셋 없음 |
| AT-007 | HOLD 동작 | HOLD 클릭 → 저장 시뮬레이션 2회 | 화면 고정 + "HOLD · displaying #x · latest #y" 배지, 관찰은 계속(콘솔 캡처 로그) |
| AT-008 | Latest 복귀 | HOLD 상태에서 Latest 클릭 | 즉시 최신 완료 번들로 전환 |
| AT-013 | Front/Back 정렬 | 줌인 상태에서 Back 클릭 | 모든 보이는 레이어가 수평 미러로 함께 반전(층간 어긋남 없음). §11.2 경고: 개별 SVG 스택 정합성은 아직 미검증 병기 |
| AT-014 | 과거 뷰 배지 | HISTORY 열기 → 과거 번들 선택 | "HISTORY · displaying #x · latest #y" 배지로 과거임이 명시 |
| AT-012(수동) | 브라우저 재접속 | 뷰어 탭 닫기 → 재오픈 | 재접속 즉시 현재 상태 조회(끊긴 사이 이벤트 누락 가정 없음, 구독-선행 계약) |
| FR-009 뷰어 반쪽 | 뷰포트 보존 | AT-006과 동일 | 동일 |
| FR-012 뷰어 반쪽 | LIVE 자동 추종 | 저장 시뮬레이션 후 손대지 않기 | 몇 초 내 "LIVE · #N" 배지 + 화면 갱신 |
| FR-013 뷰어 반쪽 | HOLD UI | AT-007과 동일 | 동일 |
| FR-015 뷰어 반쪽 | Latest 1-클릭 | AT-008과 동일 | 동일 |
| AT-005 뷰어 반쪽 | 1-틱 전환 | 저장 시뮬레이션 | 레이어들이 혼합 세대(일부 옛 번들)로 뜨는 프레임 없음 |
| 실보드 성능(§36) | 양산 보드 | 실제 `.kicad_pcb`로 watch | 체감 렌더 지연/메모리 기록 — 데모 측정치(1.01s/호출, PCB 5레이어 수 초)와 비교 |

## 자동으로 이미 증명된 것(참고 — 재확인 불필요)

- AT-001/003/004/009/010/011/002, FR-001..008/016/017/018, NFR-002/003/004/006/007/008: 단위 테스트 19개 + 헤드리스 E2E(교체 저장 3사이클, 누락 CLI 오류·복구, 소스 디렉터리 무변경, 강제 종료 후 편집).
