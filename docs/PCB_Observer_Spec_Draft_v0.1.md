# PCB Observer — Specification Draft

**Status:** Draft v0.1  
**Purpose:** Human-side, read-only, near-real-time observation of agent-driven KiCad PCB work  
**Primary target:** KiCad PCB projects edited by an autonomous coding/EDA agent  
**Core principle:** The agent keeps working exactly as before; the observer watches independently.

---

## 1. Overview

PCB Observer is a local, read-only observation tool for KiCad PCB projects.

Its purpose is not to replace KiCad, control the agent, edit the PCB, or decide whether the design is electrically correct. Instead, it provides a separate graphical window in which a human can watch the PCB evolve while an agent edits the project.

The intended workflow is:

```text
Human
  │
  │ assigns PCB work
  ▼
Agent / existing workflow
  │
  │ edits and saves KiCad files normally
  ▼
Original KiCad project
  │
  ├──────────────► KiCad / CLI / DRC / Git
  │                  existing workflow
  │
  └──────────────► PCB Observer
                      read-only
                      │
                      ├─ detect changes
                      ├─ acquire stable snapshots
                      ├─ render independently
                      └─ display in a live viewer
```

The observer must not require the agent to:

- call an observer-specific API;
- generate screenshots;
- emit progress events;
- change its save cadence;
- use a special file format;
- change its KiCad CLI workflow;
- pause while the observer renders.

The observer should therefore function as a **non-invasive spectator mode** for PCB development.

---

# 2. Product Goal

The core user experience is:

> The agent works on the PCB normally, while a separate window on another monitor updates whenever a stable new PCB state is saved. The user can zoom into an area, keep that viewport fixed, compare against previous observed states, and understand whether the displayed image is current or lagging—without disturbing the agent or opening the main KiCad editor.

The most important improvement over the current workflow is not automated design verification.

It is changing the review model from:

```text
agent works
→ work finishes
→ human opens KiCad
→ human inspects result
```

to:

```text
agent works
→ human can continuously observe the saved PCB state
→ human notices suspicious changes early
→ final review still happens in KiCad
```

---

# 3. Scope

## 3.1 In Scope

PCB Observer should eventually provide:

- read-only monitoring of a `.kicad_pcb` project;
- detection of saved PCB changes;
- stable snapshot capture;
- independent rendering through KiCad CLI or another authoritative KiCad-compatible renderer;
- near-real-time browser or desktop viewing;
- pan and zoom;
- layer visibility control;
- front-side / back-side viewing;
- viewport preservation across updates;
- live / hold / history modes;
- previous-current comparison;
- bounded snapshot history;
- status indication showing what version is currently displayed;
- later: reference designator search;
- later: net/object highlighting;
- later: semantic change summaries;
- later: optional per-snapshot DRC or other verification metadata.

## 3.2 Explicitly Out of Scope

The observer is not:

- a PCB editor;
- an autorouter;
- a replacement for KiCad;
- a replacement for final engineering review;
- an agent controller;
- an agent debugger;
- a source-control replacement;
- a manufacturing approval tool;
- a guarantee that a displayed state is electrically correct;
- a guarantee that every intermediate agent operation has been captured.

No Move / Route / Delete / Place / Edit operations should exist in the observer.

The absence of editing functionality is intentional and should remain a core architectural rule.

---

# 4. Fundamental Design Principles

## 4.1 Read-Only With Respect to the Original Project

The observer must never intentionally modify the original project.

It must not:

- save the PCB;
- refill zones in the original file;
- run a formatter against the original file;
- modify project settings;
- create `.gitignore` entries;
- add observer metadata inside the project;
- modify design rules;
- automatically commit to Git.

Any operation that can mutate PCB data must operate only on an observer-owned copy.

## 4.2 Agent Independence

The observer must not become part of the agent's critical path.

If the observer:

- crashes;
- hangs;
- fails to render;
- loses its browser connection;
- runs out of cache space;

the agent's PCB editing process must continue normally.

## 4.3 Observer Failure Must Degrade Visibility, Not Work

The failure model is:

```text
observer failure
→ user temporarily loses live visibility
→ original PCB work continues unaffected
```

It must never become:

```text
observer failure
→ agent cannot save
→ project becomes locked
→ PCB work stops
```

## 4.4 Preserve the Human's Point of View

Automatic updates must update the content, not steal the user's visual context.

By default, a new snapshot must preserve:

- viewport center in PCB coordinates;
- zoom level;
- enabled layers;
- side orientation;
- selected object or watched area where possible;
- comparison mode.

The viewer should not automatically zoom-to-fit on every update.

## 4.5 Be Honest About Freshness

The observer must never imply that a rendered view is current when a newer source state is waiting or rendering.

The UI must distinguish at least:

- latest source change detected;
- latest stable snapshot captured;
- latest render completed;
- currently displayed snapshot.

---

# 5. Terminology

## Source Project

The original KiCad project being edited by the agent.

## Source State

The state of the relevant project files on disk at a specific moment.

## Observed Snapshot

A stable, immutable copy of the source state captured by PCB Observer.

## Render Bundle

A complete set of graphical output and metadata generated from one specific observed snapshot.

A render bundle may contain:

- SVG layers;
- preview image;
- transform metadata;
- object index;
- render configuration;
- optional analysis results.

## Observation Sequence

A monotonically increasing local sequence number assigned when stable snapshots are captured.

Example:

```text
Snapshot 140
Snapshot 141
Snapshot 142
...
```

This is distinct from content hashes.

## Content Hash

A hash derived from snapshot content.

If the PCB transitions:

```text
A → B → A
```

the first and last state may have the same content hash but different observation sequence numbers.

## LIVE

The viewer follows the newest completed render bundle automatically.

## HOLD

The currently displayed snapshot remains fixed while observation and background capture continue.

## HISTORY

The user is intentionally viewing an older snapshot.

---

# 6. High-Level Architecture

```text
                      ┌─────────────────────┐
                      │ Agent / EDA process │
                      └──────────┬──────────┘
                                 │ normal save
                                 ▼
                      ┌─────────────────────┐
                      │ Original KiCad      │
                      │ project             │
                      └──────────┬──────────┘
                                 │ read-only
                                 ▼
                    ┌─────────────────────────┐
                    │ Source Watcher          │
                    │ + Stability Detector    │
                    └──────────┬──────────────┘
                               │
                               ▼
                    ┌─────────────────────────┐
                    │ Immutable Snapshot      │
                    │ Store                   │
                    └───────┬─────────┬───────┘
                            │         │
                 render     │         │ metadata / diff
                            ▼         ▼
                  ┌────────────┐   ┌──────────────┐
                  │ KiCad CLI  │   │ Analyzer     │
                  │ Renderer   │   │ optional     │
                  └──────┬─────┘   └──────┬───────┘
                         │                │
                         └───────┬────────┘
                                 ▼
                      ┌─────────────────────┐
                      │ Render Bundle Store │
                      └──────────┬──────────┘
                                 │
                       local HTTP + SSE
                                 │
                                 ▼
                      ┌─────────────────────┐
                      │ Browser Viewer      │
                      │ / Desktop Wrapper   │
                      └─────────────────────┘
```

---

# 7. Source Monitoring

## 7.1 File-Watcher Events Are Hints, Not Transactions

A filesystem change event does not mean that a complete PCB state is ready.

The source may be:

- being rewritten in place;
- replaced using a temporary file;
- saved several times quickly;
- changed together with project or rule files;
- temporarily parseable even though the agent intends another immediate save.

Therefore:

> A file watcher event means “check again”, not “capture this exact instant”.

## 7.2 Proposed Capture Flow

```text
filesystem event
      │
      ▼
short debounce
      │
      ▼
attempt stable read
      │
      ├─ unstable → retry later
      │
      ▼
syntactic / structural sanity check
      │
      ├─ invalid → keep previous good state
      │
      ▼
copy into observer-owned snapshot area
      │
      ▼
assign sequence + content hash
      │
      ▼
enqueue rendering
```

## 7.3 Initial Timing Policy

Initial tuning values, subject to measurement:

- debounce after source activity: approximately 300–700 ms;
- retry interval after unstable read: approximately 250–1000 ms;
- maximum time between capture attempts during continuous activity: approximately 2 s;
- rendering queue policy: current render + newest pending snapshot only.

These are initial engineering values, not contractual performance guarantees.

## 7.4 Stable Read

A stable read should include a combination of:

1. inspect file metadata;
2. read complete file;
3. inspect metadata again;
4. reject if size / modification state changed during read;
5. optionally repeat and compare;
6. verify the document is structurally parseable.

A simplistic check such as “parentheses count matches” is insufficient.

The parser or validator must understand enough of the syntax to avoid being fooled by:

- strings;
- escaping;
- comments or future syntax variants.

## 7.5 Stable Does Not Mean Semantically Complete

PCB Observer cannot know, without agent cooperation, whether a save represents the end of one logical editing operation.

For example:

```text
save 1: move U301
save 2: reroute nearby tracks
save 3: refill zone
```

All three may be individually valid PCB states.

Therefore the system guarantees:

> The displayed snapshot was captured from a stable and parseable source state.

It does **not** guarantee:

> The displayed snapshot represents a completed agent task.

---

# 8. Snapshot Isolation

## 8.1 Snapshot Before Rendering

Renderers and analyzers should not directly consume the actively changing original file.

Instead:

```text
original
  ↓
stable capture
  ↓
immutable snapshot
  ↓
render / analyze
```

This prevents a render from accidentally mixing data from two source states.

## 8.2 Observer Storage Location

Default observer data should live outside the source project.

Example:

```text
%LOCALAPPDATA%/
  PCBObserver/
    projects/
      <project-id>/
        snapshots/
        renders/
        metadata/
        logs/
```

or equivalent platform-native user cache/data location.

Reasons:

- no untracked Git files;
- no accidental agent discovery;
- no project recursion effects;
- no source-tree pollution;
- no need to modify `.gitignore`.

## 8.3 Snapshot Immutability

Once a snapshot is accepted:

- do not modify it;
- do not overwrite it;
- do not refill zones in-place;
- do not update metadata inside the snapshot.

Derived artifacts belong in separate locations.

---

# 9. Which Project Files Should Be Captured?

This should be divided into tiers.

## 9.1 Minimum Display Snapshot

Required for basic PCB viewing:

- `.kicad_pcb`;
- enough project/render configuration to reproduce the intended display;
- observer-side render configuration metadata.

## 9.2 Extended Verification Snapshot

Needed only when features such as DRC or design-rule-sensitive analysis are enabled:

- `.kicad_pcb`;
- `.kicad_pro`;
- `.kicad_dru`, if present;
- other explicitly required project configuration.

The initial product should avoid promising perfect whole-project transactional consistency across multiple files.

If multiple files are captured, the observer should recheck their state before finalizing the snapshot.

---

# 10. Rendering Strategy

## 10.1 Primary Rendering Principle

Use KiCad itself, preferably via `kicad-cli`, as the primary graphics source for the initial implementation.

Do not begin by building a custom PCB renderer.

Reasons:

- lower implementation risk;
- closer correspondence with KiCad geometry;
- faster MVP;
- fewer custom interpretation errors.

## 10.2 SVG as the Preferred First Output

SVG is attractive because it provides:

- scalable zoom;
- browser-native rendering;
- low-cost panning;
- potential layer composition;
- later object-overlay possibilities.

The initial implementation should validate SVG behavior against the actual target KiCad version before hard-coding assumptions.

## 10.3 Rendering Must Be Reproducible

Each render bundle should record:

- input snapshot sequence;
- input content hash;
- KiCad version;
- selected layer list;
- theme or rendering mode;
- front/back orientation;
- zone policy;
- renderer version;
- observer version.

This is necessary so historical renders can be interpreted correctly.

---

# 11. Layer Model

## 11.1 Initial Layer Priorities

Recommended first-class layers:

- `F.Cu`
- internal copper layers
- `B.Cu`
- `Edge.Cuts`
- `F.Silkscreen`
- `B.Silkscreen`

Second priority:

- `F.Fab`
- `B.Fab`
- `F.Courtyard`
- `B.Courtyard`

Manufacturing-specific mask/paste layers can be added later if actually useful for observation.

## 11.2 Copper Layer Composition

Do not assume that separately exported SVGs can always be naïvely stacked.

The implementation must test:

- transparent backgrounds;
- drill holes;
- clipping;
- line widths;
- per-layer coordinate consistency;
- page bounds;
- board-outside objects;
- internal layer alignment.

If robust multilayer stacking cannot be guaranteed initially, the first version may support:

- one active copper layer;
- several auxiliary layers;
- instant switching between copper layers.

Correctness is preferred over visually impressive but misleading composition.

---

# 12. Coordinate System

The viewer must reason in PCB coordinates, not image pixel coordinates.

This enables the same viewport to survive:

- rerendering;
- canvas-size changes;
- layer changes;
- board geometry changes;
- comparisons between snapshots.

The viewer state should conceptually contain:

```text
center_x_mm
center_y_mm
zoom
orientation
enabled_layers
```

not:

```text
scroll_x_pixels
scroll_y_pixels
```

unless pixel values are purely temporary UI implementation details.

---

# 13. Front / Back Viewing

Two separate concepts must exist:

1. **which layers are visible**
2. **which side the board is being viewed from**

Example:

- front-side view + `B.Cu` visible
- back-side view + `B.Cu` visible

These are not equivalent.

When changing orientation, the same geometric transform must apply to:

- PCB rendering;
- diff overlays;
- markers;
- object selections;
- hit testing;
- annotations.

---

# 14. Zone Handling

Zone behavior is a significant correctness issue.

A saved PCB may contain:

- zone definitions;
- previously calculated filled copper;
- recent routing changes;
- zone fills that have not yet been recomputed.

The observer must not silently pretend that “stored” and “recalculated” are the same.

## 14.1 Default Mode: Stored State

The default viewer should display the state represented by the captured snapshot.

Suggested UI language:

```text
Zone display: stored state
Fill freshness: not independently verified
```

## 14.2 Optional Mode: Recalculated Preview

A future mode may create a derived copy and refill/recalculate zones there.

This must be shown as:

```text
Recalculated preview
Not written back to source project
```

The recalculated result must never silently replace the stored-state view.

---

# 15. Render Queue and Freshness

## 15.1 Do Not Render Every Snapshot Sequentially

Suppose:

```text
Snapshot 142 rendering
Snapshot 143 captured
Snapshot 144 captured
Snapshot 145 captured
```

The renderer should normally continue 142, then jump directly to 145.

It should not accumulate an ever-growing render queue.

## 15.2 Queue Policy

Recommended default:

```text
1 currently running render
+
1 newest pending snapshot
```

If a newer pending snapshot appears, replace the older pending request.

## 15.3 Do Not Cancel Every Active Render

If a render is cancelled whenever a new source save occurs, rapid editing may prevent any render from ever completing.

Default behavior:

- finish current render;
- publish it if still newer than the currently displayed render;
- immediately render the newest pending snapshot.

## 15.4 Never Regress the Display

If an older render finishes after a newer one, it must not replace the newer display.

Display updates must follow snapshot sequence, not process completion order.

---

# 16. Atomic Render Bundles

A display state may involve multiple files.

For example:

```text
F.Cu.svg
In1.Cu.svg
In2.Cu.svg
B.Cu.svg
Edge.Cuts.svg
metadata.json
```

The UI must never display mixed generations such as:

```text
F.Cu     = snapshot 145
In1.Cu   = snapshot 145
In2.Cu   = snapshot 142
B.Cu     = snapshot 142
```

## 16.1 Publication Rule

Render everything into a temporary bundle.

Only after the entire required bundle is complete:

```text
temporary bundle
     ↓
atomic publish
     ↓
visible to viewer
```

The browser should switch bundles as one logical operation.

---

# 17. Viewer UX

## 17.1 Primary Layout

The PCB should dominate the available space.

Example:

```text
┌────────────────────────────────────────────────────────────┐
│ PCB Observer · inverter_control.kicad_pcb                  │
│ LIVE · displaying #142 · newer snapshot #145 rendering    │
│ [Layers] [Front] [Hold] [Compare] [Latest]                │
├────────────────────────────────────────────────────────────┤
│                                                            │
│                                                            │
│                    PCB VIEWPORT                            │
│                                                            │
│                                                            │
├────────────────────────────────────────────────────────────┤
│ source change 22:41:06 · captured 22:41:07 · render 22:41:08 │
└────────────────────────────────────────────────────────────┘
```

The viewer should not evolve into a dense CAD interface.

## 17.2 Required Interactions

MVP:

- pan;
- zoom;
- zoom to board;
- layer toggle;
- front/back orientation;
- hold;
- return to latest;
- select previous snapshot;
- inspect freshness state.

## 17.3 Viewport Persistence

On update:

```text
PCB content changes
human viewport does not
```

Unless the user explicitly activates a future `follow changes` mode.

---

# 18. LIVE / HOLD / HISTORY Behavior

## 18.1 LIVE

- newest completed render automatically becomes visible;
- viewport is preserved;
- UI shows pending newer capture/render states.

## 18.2 HOLD

- display remains on current snapshot;
- watcher continues;
- stable snapshot capture continues;
- renderer may continue;
- UI indicates newer snapshots are available.

Example:

```text
HOLD · viewing #142 · 4 newer snapshots available
```

## 18.3 HISTORY

- user intentionally selects an older snapshot;
- system clearly indicates that the view is historical;
- live observation continues in the background;
- one action returns to the latest available state.

---

# 19. Freshness and Status Model

Avoid a single generic green `Up to date` badge.

Recommended state components:

```text
source_last_event
latest_captured_snapshot
latest_completed_render
currently_displayed_snapshot
```

Possible display states:

### Synchronized

```text
LIVE · #145
No newer source state pending
```

### Render Behind

```text
LIVE · displaying #142
Snapshot #145 captured
Rendering latest state…
```

### Capture Pending

```text
Source changed
Waiting for stable file state…
```

### Hold

```text
HOLD · displaying #142
Latest rendered: #145
```

### Error With Last-Known-Good Display

```text
Displaying #142
New snapshot could not be rendered
Retrying automatically
```

---

# 20. Failure Handling

## 20.1 Capture Failure

If source data cannot be captured safely:

- do not blank the viewer;
- keep the previous valid render;
- mark state as waiting/error;
- retry later.

## 20.2 Render Failure

If KiCad CLI fails:

- preserve previous valid render;
- associate failure with the attempted snapshot;
- expose concise error information;
- retry according to policy;
- avoid repetitive pop-up spam.

## 20.3 Browser Disconnect

After reconnection, the browser must request current server state.

It must not assume it received every update event while disconnected.

## 20.4 Observer Restart

The observer should be able to recover:

- monitored project identity;
- latest valid snapshot;
- latest valid render;
- user settings where appropriate.

It must not require modifying the original project to accomplish this.

---

# 21. Comparison and Change Visualization

Change visualization should be developed gradually.

## 21.1 Phase A — Visual Comparison

Initial comparison modes:

- current;
- previous;
- side-by-side;
- synchronized pan/zoom;
- optional overlay;
- optional visual-difference mask.

If the system uses pixel/SVG visual differences only, call them:

> visual differences

Do not label them as:

> routing changes  
> electrical changes  
> copper connectivity changes

unless semantic analysis actually supports those claims.

## 21.2 Phase B — Component-Level Semantic Diff

Later:

```text
U103: moved
R218: rotated
C245: added
R314: removed
```

Object identity should prefer stable internal identifiers where possible.

RefDes alone should not be treated as perfect identity.

When matching is uncertain:

```text
Possible object replacement / identity uncertain
```

is better than a false confident statement.

## 21.3 Phase C — Routing / Via / Zone Diff

Later semantic categories may include:

- track geometry changed;
- via added / removed / moved;
- zone parameters changed;
- stored zone fill changed;
- board outline changed.

Avoid interpreting segment-count changes as engineering meaning by themselves.

One continuous route can be represented by different segment decompositions.

---

# 22. Human Review Baseline

One particularly useful concept is a **review baseline**.

The user may mark:

```text
Reviewed snapshot: #120
```

Then return later and compare:

```text
Reviewed #120
vs
Current #148
```

This is often more useful than comparing only:

```text
#147 vs #148
```

because the user may have been away while many small saves occurred.

Possible later UI:

```text
[Compare with previous]
[Compare with reviewed baseline]
[Set current as reviewed]
```

Setting a review baseline is observer-local metadata and does not modify the PCB project.

---

# 23. RefDes Search and Object Navigation

A later version should support:

```text
U103
R218
C245
```

and move the viewport to the corresponding component.

This does not initially require full electrical connectivity analysis.

A lightweight object index may contain:

```text
uuid
reference
value
footprint
x
y
rotation
side
bounding_box
```

The primary value is navigation.

---

# 24. Net Highlighting

Net support should initially be defined narrowly.

Possible feature:

> Highlight graphical objects assigned to net `GND`.

Do not initially claim:

> Verify all GND objects are electrically connected.

The former is an indexing/display function.

The latter is connectivity analysis and belongs to a different validation subsystem.

UI terminology must preserve this distinction.

---

# 25. DRC and Validation Integration

DRC is not part of the first live-view critical path.

If added later:

- run against observer-owned snapshots only;
- tie every result to an exact snapshot;
- never display a stale result as if it belongs to the current snapshot.

Example:

```text
Current view: #145
Latest DRC: #142
```

not simply:

```text
DRC PASS
```

Similarly, these states must remain separate:

- successfully captured;
- successfully parsed;
- successfully rendered;
- DRC clean;
- custom rule clean;
- engineer approved.

They are not equivalent.

---

# 26. Observation History

The history represents:

> states successfully observed by PCB Observer

It is not guaranteed to contain:

- every filesystem mutation;
- every agent command;
- every unsaved edit;
- every transient KiCad state.

Use terminology such as:

- observation history;
- captured snapshots;
- observed state timeline.

Avoid:

- complete edit history;
- every agent action.

---

# 27. History Storage Policy

Unlimited history is undesirable.

Separate:

```text
source snapshots
render cache
analysis metadata
```

Recommended behavior:

- keep a bounded recent snapshot history;
- use an overall storage quota;
- protect currently displayed snapshots;
- protect bookmarked/reviewed snapshots;
- allow old render outputs to be regenerated;
- use content hashes for deduplication where useful;
- keep observation events even when identical content reappears.

No `restore to source project` button should be provided.

Exporting a historical snapshot as a separate file may be considered later, but should not overwrite the active project automatically.

---

# 28. Local Server Model

Recommended first implementation:

```text
Python backend
+
local HTTP server
+
browser UI
+
SSE for status/update notification
```

Possible later desktop wrapper:

- native window;
- system tray;
- always-on-top;
- multi-monitor support.

The browser-based version should remain usable even if a desktop wrapper is later added.

---

# 29. Communication Model

For the first version, server-to-viewer updates are mostly one-way.

Suitable model:

```text
HTTP
  ├─ current state
  ├─ snapshots
  ├─ render assets
  └─ settings

SSE
  └─ “new state available” notifications
```

The event stream should carry identifiers and state updates, not large SVG payloads.

The browser then loads the completed render bundle through ordinary local HTTP requests.

---

# 30. Resource Policy

The observer must prioritize non-interference.

## 30.1 Rendering Concurrency

Default:

```text
max active KiCad CLI render jobs = 1
```

Potentially configurable later.

## 30.2 Pending Queue

Default:

```text
newest pending snapshot only
```

## 30.3 Timeouts

If an observer-owned rendering process hangs:

- terminate only that observer-owned subprocess;
- retain the last good display;
- retry or skip according to policy.

Never terminate:

- user KiCad process;
- agent process;
- unrelated CLI jobs.

## 30.4 CPU Priority

If practical on the platform, observer rendering may run below normal interactive priority.

This is an optimization, not an MVP requirement.

---

# 31. Security Model

Default assumptions:

- local-only tool;
- no cloud upload;
- bind to loopback only;
- no arbitrary command execution exposed through the browser;
- no arbitrary filesystem browsing through the UI.

The server should only expose:

- explicitly registered source projects;
- observer-owned generated assets;
- defined read-only metadata.

PCB-derived strings must be treated as untrusted display data.

Do not blindly inject:

- reference strings;
- values;
- footprint names;
- text labels;

as raw HTML.

SVG handling should also be designed carefully if SVG is directly embedded into the DOM.

---

# 32. Suggested Development Phases

## Phase 0 — Renderer Feasibility

Goal:

> Prove that a representative KiCad PCB can be rendered reliably enough for live observation.

Validate:

- KiCad CLI invocation;
- render latency;
- SVG output;
- coordinate consistency;
- front/back handling;
- layer alignment;
- board-outside objects;
- drill handling;
- zone appearance;
- large-board performance.

Deliverable:

```text
manual command
→ stable SVG output
→ viewer can pan/zoom correctly
```

No watcher required yet.

---

## Phase 1 — Live Observer MVP

Goal:

> A reliable window that follows saved PCB changes without interfering with the editor or agent.

Required:

- source watcher;
- debounce;
- stable capture;
- immutable snapshot;
- render queue;
- single complete render bundle;
- local server;
- browser viewer;
- pan;
- zoom;
- layer control;
- viewport persistence;
- LIVE;
- HOLD;
- return to latest;
- freshness/status display;
- last-known-good behavior;
- bounded recent history.

This phase should already be useful in daily work.

---

## Phase 2 — Better Human Observation

Add:

- previous/current comparison;
- synchronized side-by-side view;
- visual diff;
- review baseline;
- snapshot bookmarks;
- RefDes search;
- component navigation;
- front/back shortcuts;
- polished multi-monitor workflow.

---

## Phase 3 — Semantic Change Awareness

Add:

- component move/rotation/add/remove detection;
- via change detection;
- track geometry change detection;
- zone parameter/fill distinction;
- compact change summaries;
- selectable change markers.

Example:

```text
Changes since reviewed snapshot #120

Components
  2 moved
  1 added

Routing
  14 track objects changed
  4 vias added

Zones
  F.Cu GND stored fill changed
```

This remains descriptive, not judgmental.

---

## Phase 4 — Optional Validation Sidecar

Possible future modules:

- snapshot-specific DRC;
- connectivity inspection;
- custom PCB assertions;
- design-intent rules;
- geometric clearance analysis;
- power-path checks;
- semantic Git review reports.

These modules should remain optional and should not turn PCB Observer into a mandatory part of the agent workflow.

---

# 33. MVP Functional Requirements

## FR-001 — Project Selection

The user shall be able to select or launch PCB Observer against a KiCad PCB project.

## FR-002 — Read-Only Source Access

PCB Observer shall not modify the selected source project.

## FR-003 — Automatic Change Detection

PCB Observer shall detect source PCB changes without requiring agent-side integration.

## FR-004 — Stable Snapshot Capture

PCB Observer shall avoid accepting a source file that is actively changing or structurally incomplete.

## FR-005 — Immutable Snapshot

Every accepted state used for rendering shall be represented by an observer-owned immutable snapshot.

## FR-006 — Automatic Rendering

A newly accepted snapshot shall be rendered automatically.

## FR-007 — Latest-State Queueing

Rendering shall prefer the newest pending snapshot rather than building an unbounded FIFO queue.

## FR-008 — Atomic Display Update

The viewer shall not combine render assets from different snapshots into one displayed state.

## FR-009 — Viewport Preservation

The viewer shall preserve PCB-coordinate viewport and zoom across live updates.

## FR-010 — Layer Visibility

The user shall be able to enable or disable supported layers.

## FR-011 — Orientation

The user shall be able to view the board from the front or back orientation.

## FR-012 — LIVE Mode

The viewer shall automatically follow newer completed render bundles while in LIVE mode.

## FR-013 — HOLD Mode

The user shall be able to freeze the displayed snapshot without stopping background observation.

## FR-014 — History

The user shall be able to select at least a bounded set of recently captured snapshots.

## FR-015 — Return to Latest

The user shall be able to return from HOLD/HISTORY to the newest available rendered snapshot with one action.

## FR-016 — Freshness Status

The viewer shall show whether:

- source changes are pending capture;
- a newer snapshot is captured;
- rendering is in progress;
- the displayed snapshot is older than the latest available state.

## FR-017 — Last-Known-Good Display

Capture or render failures shall not erase the last successfully displayed PCB.

## FR-018 — Independent Shutdown

Stopping PCB Observer shall not stop or modify the agent, KiCad, or the source project.

---

# 34. MVP Non-Functional Requirements

## NFR-001 — Source Integrity

Under normal and failure conditions, no observer operation shall intentionally write to the source project.

## NFR-002 — Graceful Failure

Observer failure shall affect visibility only.

## NFR-003 — Bounded Resource Use

Render concurrency and storage usage shall be bounded.

## NFR-004 — No Unbounded Backlog

Sustained source activity shall not create an indefinitely growing render queue.

## NFR-005 — UI Continuity

Automatic updates shall not reset the user's viewport.

## NFR-006 — Explicit Staleness

The UI shall not present an older snapshot as current when a newer source state is known to exist.

## NFR-007 — Local First

The MVP shall operate without uploading PCB data to an external service.

## NFR-008 — Recoverability

A failed render shall not prevent later snapshots from being observed.

---

# 35. Acceptance Tests

## AT-001 — Repeated Saves

Given repeated valid PCB saves, the observer eventually displays the latest successfully rendered state automatically.

## AT-002 — Slow/Partial Write

When a source file is observed during an incomplete write, the observer does not publish the incomplete state as a valid display.

## AT-003 — Atomic Replace Save Pattern

If the editor replaces the PCB file via temporary-file rename, monitoring continues after replacement.

## AT-004 — Render Slower Than Saves

If source changes arrive faster than rendering completes, the observer does not accumulate an unbounded queue and eventually catches up to the newest state.

## AT-005 — Multi-Layer Bundle Integrity

A visible render never contains layer assets from different snapshots.

## AT-006 — Viewport Stability

When a new snapshot is displayed, an existing zoomed-in viewport remains centered on the same PCB coordinate.

## AT-007 — HOLD Behavior

While HOLD is active, the displayed PCB remains unchanged but new snapshots continue to be captured/rendered.

## AT-008 — Latest Recovery

Exiting HOLD/HISTORY moves directly to the newest completed render.

## AT-009 — Render Error

If rendering a new snapshot fails, the previous successful display remains visible and the error state is shown.

## AT-010 — Source Read-Only Operation

The observer can perform its primary viewing workflow without creating or modifying files inside the monitored project directory.

## AT-011 — Observer Crash

Terminating the observer unexpectedly does not interfere with the agent's ability to continue editing and saving the project.

## AT-012 — Browser Reconnection

After closing/reopening the viewer, it reloads the actual current observer state instead of assuming prior events were received.

## AT-013 — Front/Back Alignment

All visible PCB layers and overlays remain spatially aligned after switching board orientation.

## AT-014 — Historical Identification

While viewing a historical snapshot, the UI clearly indicates that the user is not looking at the latest state.

---

# 36. Initial Performance Targets

These are provisional targets to be refined after real-board measurements.

For a representative production-scale board:

- source-change detection should feel immediate;
- stable snapshot capture should normally begin within roughly one second after write activity settles;
- a typical completed update should appear within a few seconds;
- viewer pan/zoom should remain interactive independent of background rendering;
- continuous saves should not increase display lag without bound.

Measure separately:

- source file size;
- footprint count;
- track/via object count;
- zone complexity;
- number of copper layers;
- SVG size;
- KiCad CLI render time;
- browser parse/render time.

Do not use PCB file size alone as the performance predictor.

---

# 37. Suggested MVP Technology Stack

This is a proposal, not a hard requirement.

## Backend

Python

Responsibilities:

- watcher;
- stable capture;
- hashing;
- snapshot lifecycle;
- KiCad CLI process control;
- queue management;
- cache cleanup;
- local HTTP API;
- SSE.

Possible server framework:

- lightweight ASGI/HTTP framework.

## Frontend

TypeScript or vanilla modern JavaScript.

Responsibilities:

- SVG/image display;
- pan/zoom;
- layer state;
- LIVE/HOLD/HISTORY;
- status presentation;
- comparison UI.

## Transport

- HTTP for state/assets;
- SSE for update notification.

## Renderer

- target-version `kicad-cli`;
- exact supported command line verified during Phase 0.

---

# 38. Configuration Draft

Possible observer-side configuration:

```yaml
project:
  pcb: "D:/work/project/control.kicad_pcb"

capture:
  debounce_ms: 500
  retry_ms: 500
  continuous_activity_capture_attempt_ms: 2000

render:
  max_parallel_jobs: 1
  copper_mode: "single-active"
  orientation: "front"
  layers:
    - F.Cu
    - In1.Cu
    - In2.Cu
    - B.Cu
    - Edge.Cuts
    - F.Silkscreen
    - B.Silkscreen

history:
  max_snapshots: 200
  max_storage_gb: 5

server:
  bind: "127.0.0.1"
  port: 8765
```

The exact format can change.

No configuration file should be required inside the monitored KiCad project.

---

# 39. Data Model Draft

## Snapshot

```json
{
  "sequence": 145,
  "captured_at": "...",
  "source_path": "...",
  "content_hash": "...",
  "files": {},
  "capture_status": "valid"
}
```

## Render Bundle

```json
{
  "snapshot_sequence": 145,
  "content_hash": "...",
  "renderer": {
    "kind": "kicad-cli",
    "version": "..."
  },
  "settings_hash": "...",
  "created_at": "...",
  "orientation": "front",
  "layers": {},
  "status": "complete"
}
```

## Viewer State

```json
{
  "mode": "live",
  "displayed_snapshot": 145,
  "center_mm": [82.4, 47.2],
  "zoom": 3.1,
  "orientation": "front",
  "visible_layers": [
    "F.Cu",
    "Edge.Cuts",
    "F.Silkscreen"
  ]
}
```

---

# 40. Open Questions

These should be answered through implementation experiments rather than assumptions.

## Rendering

- How reliably can target KiCad versions export individually composable SVG layers?
- Are layer SVG viewBoxes and origins perfectly consistent?
- How are drill holes represented?
- How are board-outside objects handled?
- What is the most useful visual theme for observation?
- How expensive is rerendering on a large multi-zone board?

## Snapshot Consistency

- Which exact project files are required for faithful rendering?
- Can project-relative dependencies change rendering?
- What save patterns do the actual agent/KiCad workflows use?
- How often do temporary replace/rename patterns occur?

## Zones

- How useful is stored-fill viewing in practice?
- Is an optional recalculated preview worth the added complexity?
- Can fill staleness be detected reliably enough to show a stronger warning?

## Browser Rendering

- Is direct SVG DOM embedding safe and fast enough for large files?
- Is `<img>`-style SVG display sufficient for MVP?
- Should layers be separate SVG elements or precomposed by the backend?

## History

- What is the real disk cost on typical boards?
- How many snapshots are useful before history becomes noise?
- Should every stable snapshot be retained, or only snapshots that produced distinct visual/semantic changes?

## Multi-Board Projects

- Should one observer session support multiple `.kicad_pcb` files?
- If so, should they share one timeline or separate tabs?

---

# 41. Product Philosophy

The observer should not attempt to be smarter than the source of truth.

Its job is to make ongoing PCB work visible.

The most important principles are:

1. **The agent should not have to know the observer exists.**
2. **The source project must remain untouched.**
3. **A displayed state must belong to one coherent captured snapshot.**
4. **The user's viewport should remain stable while the PCB changes.**
5. **The observer must clearly expose when it is behind the source.**
6. **Failure must preserve the last known-good view rather than interrupt work.**
7. **Final engineering review still belongs in KiCad.**
8. **Advanced semantic analysis should be added as a sidecar, not allowed to distort the simple live-observation core.**

---

# 42. One-Sentence Product Definition

> **PCB Observer is a read-only spectator window for agent-driven KiCad work: the agent edits normally, while the human watches stable saved PCB states update independently, with preserved viewpoint, explicit freshness, and zero intentional modification of the source project.**

---

# 43. Recommended Immediate Next Step

Before implementing the full watcher, build a small Phase-0 prototype that does only this:

```text
known .kicad_pcb
      ↓
copy to temp snapshot
      ↓
kicad-cli render
      ↓
open in a browser viewer
      ↓
verify pan/zoom/layer alignment manually
```

Measure:

- render time;
- output size;
- layer alignment;
- front/back behavior;
- zone behavior;
- memory/CPU use.

If this renderer path is solid, proceed directly to the live watcher architecture described above.

If it is not, change only the rendering backend while preserving the rest of the architecture.

That separation is important: **watching, snapshot isolation, freshness, history, and human viewport behavior should not depend on one specific rendering technology.**
