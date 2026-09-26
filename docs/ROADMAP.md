# KiCad Observer — Roadmap (다음 마일스톤 인계)

이 문서는 "이번에 하지 않은 일이 잊히지 않도록" 하는 내구 인계 문서다(RALPLAN 승인 계획의 사용자 확정 조건). 마지막 갱신: 2026-09-27, HEAD = Stage 0~4 완료 커밋.

## (a) 이연된 회로도(Schematic) 옵저버 MVP — 다음 마일스톤 주제

스코프(addendum §39 Recommended MVP + SCH-FR-001..016 / SCH-AT-001..014 요약):

- **루트 앵커 세션**(SCH-FR-001): `.kicad_sch` 루트 하나 = 세션 하나.
- **재귀 계층 발견**(SCH-FR-002/005): 루트에서 `(property "Sheetfile" ...)` 추적으로 의존 그래프 구성, 동적 갱신(재시작 없이).
- **논리 시트 인스턴스 모델**(SCH-FR-003/004): 물리 파일 ≠ 논리 인스턴스. 공유 자식 파일의 다중 인스턴스 개별 네비게이션. **Stage 0 findings가 이 모델의 검증 데이터**다(§49-2: 인스턴스별 refres는 자식의 UUID 인스턴스 블록에서 해석, 블록 없으면 저장된 Reference로 폴백).
- **프로젝트 단위 안정 캡처**(SCH-FR-006, addendum §8): 다중 파일 저장 디바운스/병합, 누락 자식 = 명시 상태(SCH-FR-016, 크래시 아님).
- **루트 컨텍스트 렌더링**(SCH-FR-007): `kicad-cli sch export svg`를 **항상 루트에서** 1회(whole-design 기준선 — (c) 참조).
- **완전 번들 게시**(SCH-FR-008): 모든 논리 페이지 렌더+매핑 완료 후에만 게시.
- **시트 트리 UI**(SCH-FR-009~012): 논리 계층 표시, 시트 이동, 시트별 뷰포트 기억, 변경 시트 자동 점프 금지.
- **HOLD/HISTORY 스냅샷 일관성**(SCH-FR-013~015), 영향받은 시트 전파 표시.

SCH-AT-001..014 전 목록은 addendum §41. 특히 SCH-AT-003(공유 자식)/SCH-AT-013(페이지 재정렬)은 Stage 0 케이스로 이미 재현 수단이 있다(`fixtures/sch/`).

## (b) ProjectAdapter/ViewerModel 공유 코어 리팩터링 — 트리거 조건

addendum §47: "first schematic prototype proves viable" **이후에만**. 구체 트리거:

> 회로도 프로토타입이 Stage 0 findings의 매핑 규칙 위에서 스냅샷→번들 E2E 1회 완주 시 개시.

**상태(2026-09-27): 트리거 충족됨.** `watch-sch` 프로토타입이 스냅샷→루트 전체 익스포트→페이지 매핑→원자 게시 E2E를 완주(공유 자식 3인스턴스, 시트 재명명, 자식 전용 수정, 누락 자식 명시 상태 포함, 28/28 테스트). 다만 리팩터링은 여전히 **필수 아님**: PCB/스키매틱 양 파이프라인이 이미 Watch/Store/Queue/Server 코어를 실제 공유 중 — 어댑터 인터페이스 추출은 중복·불일치 증상이 실제로 보일 때 수행한다(다음 사이클 후보).

그 전에는 리팩터링 금지(구현 피드백 없는 추상화 = 투기). 현재 Watch/Store/Queue/Server 모듈 분리가 이미 어댑터 수용면을 제공한다. 권장 인터페이스(addendum §47): `ProjectAdapter(discover_dependencies/capture_inputs/validate_snapshot/create_render_plan/run_render/build_manifest)`, `ViewerModel(list_views/view_identity/default_view/preserve_view_state)`.

## (c) Stage 0 findings → 회로도 MVP 설계 입력

`docs/Sch_Phase0_Findings_v0.1.md`의 핵심(전체는 해당 문서):

- **whole-design export 확정**(증명됨): `--pages`는 10.0.4에서 자식 페이지 선택 불가(전부 루트만 플롯, 이름형 exit 1). 호출당 ~1.01s 고정(1~20쪽 동일).
- **출력 매핑 규칙**: `<root>.svg`, 자식 `<root>-<Sheetname>.svg`, 깊이 체인 `<root>-<N1>-<N2>.svg`(공백 유지). **파일명에 페이지 번호 없음**(재정렬 시 불변) → 옵저버는 시트이름 체인 + UUID 인스턴스 경로로 키잉, 파일명 단독 금지.
- **중복 Sheetname 충돌**: 같은 이름 → 같은 파일에 덮어쓰기(3쪽 설계가 2파일로, exit 0 무음) → 이상 신호로 감지해야.
- **자식 누락 = 무음 실패**: exit 0, stderr 없음, ~57KB 빈 프레임 SVG(정상 ~1.46MB 대비) → 옵저버가 Sheetfile 존재 검사 + 빈 프레임 감지로 방어 필수.
- **루트 기준 전체 재해석**: 자식만 수정해도 루트 export에 반영(§49-5) — 캐시 무효화는 루트 1회 호출로 해결.

## (d) 미해결 공개 질문

PCB 스펙 §40 + addendum 공개 항목 중 이번 실행 후에도 열려 있는 것:

1. `--pages` 자식 선택 불가는 KiCad 상위 버전에서 재테스트 필요(가능해지면 선택적 재렌더 재검토 — 이연 목록 #3).
2. Case D 3번째 인스턴스의 인스턴스 블록 누락 심볼(11개)의 중복 refdes — 어노테이션 정책 조사 필요(Stage 0 §49-2).
3. KiCad 10 네이티브 파일 형식 미검증(픽스처는 demo v20250114 기반).
4. `--drawing-sheet`/`--theme`/`--variant`의 출력 명명 영향 미측정.
5. 지연 측정이 단일 머신·데모 규모 — 큐 예산 확정 전 실보드 재측정(이연 #8).
6. 이번 실행 stall/미달: **없음**(전 스테이지 예산 내 완주).
7. PCB 스펙 §40 원문 질문 중 레이어 viewBox 정합성(§11.2)은 여전히 수동 확인 항목(Manual_Checklist).

## (e) 순서화된 다음 마일스톤 제안

1. **S-1 회로도 프로토타입**: PCB Watch/Store/Queue/Server 재사용 + 스키매틱 캡처(다중 파일 프로젝트 스냅샷) + 루트 export svg + 시트이름체인→논리 시트 매핑 + 시트 트리 뷰어. 완주 시 (b) 트리거 충족.
2. **S-2 ProjectAdapter 리팩터링**(트리거 후): PCB/스키매틱 어댑터 분리, 스케줄러 공유(addendum §43).
3. **PCB 보강**: 구리 합성·정렬 검증(§11.2/§12), 선택적 렌더(Stage 0 결과 재평가), 렌더 실패 자동 재시도(A3 이연), 비교/디프(§21), 리뷰 베이스라인(§22).
4. **관찰 확장**: RefDes 검색(§23), 넷 하이라이트팅(§24), DRC/ERC 사이드카(§25/addendum §35), 시맨틱 디프(§32 Phase 3).
5. **운영**: 설정 파일(§38), 히스토리 쿼터 정책 잔여(§27), 데스크톱 래퍼(§28), 멀티보드/워크스페이스(§40/addendum §37·§44), 실보드 성능(§36).

## 원본 이연 목록 대조(커버리지 누락 0)

계획 §7의 9개 항목 → 본 문서: #1→(a), #2→(b), #3→(e)-3, #4→(e)-3, #5→(e)-4, #6→(e)-3, #7→(e)-5, #8→(e)-5/(d)-5, #9→매 커밋 시점 정책 유지(로컬만, 원격 없음).
