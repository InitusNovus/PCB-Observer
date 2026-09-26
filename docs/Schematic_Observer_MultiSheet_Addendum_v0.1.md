# Schematic Observer — Multi-Sheet / Hierarchy Addendum

**Status:** Draft v0.1  
**Companion document:** `PCB_Observer_Spec_Draft_v0.1.md`  
**Purpose:** Extend the PCB Observer concept to KiCad schematics without turning the observer into an editor or changing the agent workflow.

---

## 1. Why the Schematic Side Needs a Separate Design

The basic idea is the same as PCB Observer:

> The agent edits KiCad normally. A separate, read-only observer watches stable saved states and renders them for the human in near real time.

However, schematics are structurally different from a PCB.

A PCB usually has one primary `.kicad_pcb` document whose layers are multiple views of the same geometric design.

A KiCad schematic can instead be a **hierarchical design made from multiple `.kicad_sch` files**, and the important unit for the user is not always the physical file.

The observer therefore needs to distinguish:

```text
physical schematic file
        ≠
logical schematic sheet instance
```

This distinction should drive the entire schematic observer architecture.

---

# 2. Core KiCad Hierarchy Model

KiCad schematic files use `.kicad_sch`.

A hierarchical sheet in a parent schematic contains, among other information:

- a sheet name;
- a referenced schematic file name;
- a UUID;
- hierarchical pins;
- project/sheet-instance information.

KiCad also represents a logical sheet instance using an **instance path composed of UUIDs through the hierarchy**.

This matters because the same child schematic file can be instantiated more than once.

Example:

```text
root.kicad_sch
│
├─ "Power"
│    └─ power.kicad_sch
│
├─ "Channel A"
│    └─ channel.kicad_sch
│
└─ "Channel B"
     └─ channel.kicad_sch
```

There are only three physical schematic files:

```text
root.kicad_sch
power.kicad_sch
channel.kicad_sch
```

but there are four logical sheet instances:

```text
/
 /Power
 /Channel A
 /Channel B
```

`Channel A` and `Channel B` can share `channel.kicad_sch` while still having different instance context such as references and page identity.

Therefore:

> **The observer must use logical sheet instance paths as its primary sheet identity, not source filenames.**

---

# 3. Product Definition

Schematic Observer is a read-only, local, near-real-time viewer for a KiCad schematic hierarchy.

It shall:

- watch the root schematic and its transitive child-sheet dependencies;
- capture stable project-level schematic snapshots;
- render logical sheet instances through KiCad CLI;
- present the hierarchy as a navigable tree;
- show which logical sheets are affected by a saved source-file change;
- preserve the user's current sheet, pan, and zoom during updates;
- support LIVE / HOLD / HISTORY semantics compatible with PCB Observer;
- never modify the original schematic project.

It shall not:

- edit symbols or wires;
- open or drive the KiCad GUI;
- require the agent to emit progress messages;
- infer agent intent from file activity;
- treat a child `.kicad_sch` file as if it were necessarily one unique logical sheet;
- silently mix different project snapshot generations in one historical view.

---

# 4. Shared Core With PCB Observer

The schematic implementation should reuse the same observer infrastructure wherever possible.

Shared concepts:

```text
source watcher
stable capture
immutable snapshot store
content hashes
observation sequence
render queue
render bundle
local HTTP server
SSE notifications
LIVE / HOLD / HISTORY
last-known-good display
bounded history
review baseline
error/status model
```

The main schematic-specific additions are:

```text
hierarchy resolver
dependency graph
logical sheet instance model
multi-page renderer
sheet tree UI
project-level snapshot consistency
affected-sheet propagation
```

A desirable long-term architecture is:

```text
Observer Core
├─ capture / history / server / UI state
│
├─ PCB Adapter
│    ├─ pcb snapshot model
│    └─ pcb renderer
│
└─ Schematic Adapter
     ├─ hierarchy resolver
     ├─ sheet-instance model
     └─ schematic renderer
```

The PCB and schematic modes should share infrastructure without pretending that their document models are identical.

---

# 5. Root Schematic as the Session Anchor

A schematic observer session should be anchored to a **root schematic**.

Example:

```text
project/
├─ inverter.kicad_pro
├─ inverter.kicad_sch      ← root
├─ power.kicad_sch
├─ resolver.kicad_sch
└─ diagnostics.kicad_sch
```

The initial implementation should not simply watch every `.kicad_sch` in the directory and call each one a page.

Files may be:

- unused;
- old copies;
- templates;
- unrelated schematics;
- child sheets;
- roots of another design.

The root determines the logical design hierarchy.

For ambiguous repositories containing multiple independent root schematics, treat them as separate observer targets/tabs rather than merging them into one artificial hierarchy.

---

# 6. Dependency Graph

## 6.1 Graph Discovery

Starting from the root schematic:

```text
root
  ↓ parse hierarchical sheet references
child files
  ↓ parse their hierarchical sheet references
grandchildren
  ↓
...
```

Build the transitive dependency graph.

Conceptually:

```text
Physical file graph:

root.kicad_sch
├─ power.kicad_sch
├─ channel.kicad_sch
└─ monitor.kicad_sch
     └─ adc_frontend.kicad_sch
```

Separately build the logical instance tree:

```text
Logical sheet tree:

/
├─ Power
├─ Channel A
├─ Channel B
└─ Monitor
     ├─ ADC A
     └─ ADC B
```

These are related but not interchangeable.

## 6.2 Dynamic Hierarchy

The agent may:

- add a child sheet;
- remove a child sheet;
- rename a referenced file;
- move a sheet in the hierarchy;
- instantiate one source file multiple times.

Therefore the dependency graph cannot be calculated once at startup and treated as immutable.

After a stable change that can affect hierarchy:

1. capture the candidate project state;
2. rebuild or incrementally update the hierarchy;
3. update watched source files/directories;
4. identify added/removed logical instances;
5. render from the new resolved root design.

## 6.3 Missing Dependency

A parent may temporarily reference a child file that does not yet exist.

The observer should not crash.

Example UI:

```text
Monitor
└─ ADC Frontend
   └─ source missing: adc_frontend.kicad_sch
```

The last known-good project render may remain visible while the new hierarchy is incomplete.

---

# 7. Physical Files vs Logical Sheet Instances

This is the central schematic-specific rule.

Consider:

```text
root.kicad_sch

Sheet UUID A
  name: LEFT_CHANNEL
  file: channel.kicad_sch

Sheet UUID B
  name: RIGHT_CHANNEL
  file: channel.kicad_sch
```

A file watcher sees one physical file:

```text
channel.kicad_sch
```

The observer hierarchy contains two affected logical instances:

```text
/LEFT_CHANNEL
/RIGHT_CHANNEL
```

Therefore a source-file change produces an **affected instance set**.

Example:

```text
Source event:
  channel.kicad_sch changed

Affected logical sheets:
  /LEFT_CHANNEL
  /RIGHT_CHANNEL
```

This should be visible to the user.

Do not report merely:

```text
channel.kicad_sch changed
```

as though that fully describes the visual impact.

---

# 8. Stable Project-Level Snapshot Capture

PCB Observer can often reason primarily about one PCB file.

Schematic Observer must capture a coherent **dependency set**.

At minimum:

```text
root schematic
+
all reachable child schematic files
+
project configuration needed by rendering
```

Potential supporting inputs may include:

- `.kicad_pro`;
- drawing-sheet configuration;
- project text variables;
- other files proven necessary by actual renderer tests.

## 8.1 Project-Level Debounce

A hierarchy may be saved across several files in quick succession.

Example:

```text
23:10:00.000 root.kicad_sch saved
23:10:00.180 power.kicad_sch saved
23:10:00.410 channel.kicad_sch saved
```

Rendering on the first event would create unnecessary mixed intermediate views.

The schematic observer should therefore debounce across the **whole watched dependency set**, not independently per file.

Initial policy:

```text
any watched schematic changes
        ↓
project activity window opens
        ↓
wait for short quiet period
        ↓
fingerprint dependency set
        ↓
copy dependency set
        ↓
fingerprint again
        ↓
if stable → accept snapshot
if changed → retry
```

## 8.2 No False Transaction Claim

Even this does not guarantee that the captured set represents one intentional agent transaction.

The agent may deliberately save:

```text
root
then child
then another child
```

with long pauses between them.

The observer can guarantee only:

> The captured dependency set was stable during acquisition.

It cannot guarantee:

> The agent considered this hierarchy-wide edit logically complete.

That limitation is inherent when remaining independent of the agent.

---

# 9. Snapshot Identity

A schematic project snapshot should contain:

```text
observation sequence
root file identity
dependency graph
logical sheet-instance tree
physical file hashes
project/render configuration hash
capture timestamp
```

Suggested conceptual structure:

```json
{
  "sequence": 87,
  "root": "inverter.kicad_sch",
  "files": {
    "inverter.kicad_sch": "...hash...",
    "power.kicad_sch": "...hash...",
    "channel.kicad_sch": "...hash..."
  },
  "instances": [
    {
      "instance_path": "/...",
      "sheet_name": "Power",
      "source_file": "power.kicad_sch",
      "page": "2"
    }
  ]
}
```

The exact schema may differ.

The important point is that **file identity, logical instance identity, and page/export identity remain separate concepts**.

---

# 10. Rendering Through the Root Schematic

KiCad 10's `kicad-cli sch export svg` renders schematic SVG output and exports each sheet in the design to its own file.

It also supports selecting pages with `--pages`, with the root schematic supplied as the input file.

This suggests an important rendering rule:

> **Render logical sheets in project context by invoking schematic export from the root design, rather than rendering child files independently whenever instance context matters.**

Why?

Because rendering a shared child file directly risks losing or changing project-instance context.

The observer's logical source of truth is:

```text
root schematic + hierarchy instance
```

not simply:

```text
child file on disk
```

---

# 11. Page Number Is an Export Selector, Not the Primary Identity

KiCad sheet instances have page identifiers, and CLI page selection uses page values.

However, the observer should not build its durable identity model around display page numbers.

Page numbers can be:

- strings;
- reordered;
- changed during editing;
- presentation-oriented rather than structural identity.

Primary identity should be:

```text
logical instance path / UUID path
```

Associated metadata may include:

```text
current page label
sheet name
source filename
```

If the CLI requires page numbers for selective rendering, the observer can map:

```text
instance_path
    ↓
current page selector
    ↓
kicad-cli --pages
```

If that mapping becomes ambiguous or cannot be trusted, the safe fallback is to render all pages from the root.

---

# 12. MVP Rendering Strategy: Whole-Design Export First

The first implementation should favor correctness and simplicity.

On each accepted schematic project snapshot:

```text
root snapshot
      ↓
kicad-cli sch export svg
      ↓
all logical pages rendered
      ↓
validate output set
      ↓
publish one schematic render bundle
```

Advantages:

- matches KiCad's native hierarchy traversal;
- avoids premature incremental-render complexity;
- easier snapshot consistency;
- easy to reason about LIVE/HOLD/HISTORY;
- shared child instances are handled in root context.

Only after performance is measured should the implementation optimize toward selective page rendering.

---

# 13. Future Incremental Rendering

For larger projects, rerendering every page after every save may become expensive.

A later version may calculate dirty logical sheets.

Example:

```text
power.kicad_sch changed
      ↓
logical instances using power.kicad_sch marked dirty
      ↓
render those instances/pages
```

However, change propagation is not always purely local.

Changes to:

- hierarchy structure;
- project variables;
- annotation/instance information;
- page ordering;
- shared-sheet instance data;
- root project settings;

may affect more than one directly edited file.

Therefore dirty-page optimization should use conservative invalidation.

Possible classes:

```text
Local schematic content change
→ dirty all instances of that physical file

Hierarchy change
→ rebuild tree
→ dirty affected subtree / possibly all pages

Project-wide configuration change
→ dirty all pages

Unknown change category
→ dirty all pages
```

Correctness is preferred over an aggressive but wrong incremental cache.

---

# 14. Schematic Render Bundle

One accepted project snapshot should produce a bundle representing the complete logical hierarchy.

Example:

```text
snapshot-0087/
├─ manifest.json
├─ root.svg
├─ power.svg
├─ channel-a.svg
├─ channel-b.svg
├─ monitor.svg
└─ thumbnails/
```

The filenames above are illustrative only.

The manifest should map each render artifact to its actual logical sheet instance.

Example:

```json
{
  "snapshot": 87,
  "sheets": [
    {
      "instance_path": "/",
      "page": "1",
      "name": "Root",
      "source_file": "inverter.kicad_sch",
      "asset": "..."
    },
    {
      "instance_path": "/<uuid-a>",
      "page": "3",
      "name": "Channel A",
      "source_file": "channel.kicad_sch",
      "asset": "..."
    },
    {
      "instance_path": "/<uuid-b>",
      "page": "4",
      "name": "Channel B",
      "source_file": "channel.kicad_sch",
      "asset": "..."
    }
  ]
}
```

Never infer logical identity later from generated SVG filenames alone.

---

# 15. Atomicity of a Schematic View

A key UX rule:

> While viewing snapshot `#87`, navigating between sheets should normally stay within snapshot `#87`.

Do not create:

```text
Root        = snapshot 87
Power       = snapshot 87
Channel A   = snapshot 88
Channel B   = snapshot 86
```

and present that as one coherent historical project state.

For MVP, publish the design bundle only when all required page renders are available.

This makes LIVE/HOLD/HISTORY much easier to trust.

---

# 16. Potential Later Optimization: Priority Rendering

If whole-project rendering becomes too slow, introduce priority rendering without losing explicit version identity.

Possible order:

```text
1. currently viewed logical sheet
2. logically affected sheets
3. remaining sheets
```

The UI may then say:

```text
Snapshot #91 captured

Current sheet: #91 ready
3 / 11 sheets rendered
Project bundle #90 still last complete bundle
```

This distinction is important.

A fast preview for one page is acceptable.

Pretending that the whole project is already at #91 is not.

This optimization should be deferred until real performance data shows it is necessary.

---

# 17. Viewer Layout

Schematic observation benefits from a hierarchy-centric UI.

Recommended structure:

```text
┌──────────────────────────────────────────────────────────────┐
│ Schematic Observer · inverter                              │
│ LIVE · snapshot #87 · all sheets current                   │
├─────────────────┬────────────────────────────────────────────┤
│ SHEETS          │                                            │
│                 │                                            │
│ ▾ / Root        │                                            │
│   ├ Power       │             SCHEMATIC                      │
│   ├ Channel A ● │               VIEW                         │
│   ├ Channel B ● │                                            │
│   ▾ Monitor     │                                            │
│     ├ ADC A     │                                            │
│     └ ADC B     │                                            │
│                 │                                            │
├─────────────────┴────────────────────────────────────────────┤
│ source activity · captured · render status                  │
└──────────────────────────────────────────────────────────────┘
```

The sheet tree is not just a file browser.

It represents **logical schematic instances**.

---

# 18. Sheet Tree Information

Each tree node may show:

- sheet name;
- page label;
- hierarchy depth;
- changed-since-baseline marker;
- render pending marker;
- missing dependency marker;
- shared-source indicator, if useful.

The physical source filename should be available in details/tooltip, but should not dominate the normal navigation UI.

Example:

```text
Channel A
Page 4
Source: channel.kicad_sch
Instance: /<root-uuid>/<sheet-uuid-a>
```

---

# 19. Do Not Auto-Jump to the Changed Sheet

The same human-observation principle from PCB mode applies.

Suppose the user is inspecting:

```text
Power
```

while the agent edits:

```text
Resolver
```

The viewer should mark Resolver as changed but leave the user's current view alone.

Example:

```text
Power          ← currently viewing
Resolver  ●    ← new render available
Diagnostics ●
```

A later optional feature may provide:

```text
Follow newest changed sheet
```

but it should default to OFF.

---

# 20. Overview / Mosaic Mode

Because a schematic can span many pages, a project overview can be more useful than the PCB equivalent.

A useful later mode is:

```text
┌────────────┬────────────┬────────────┐
│ Root       │ Power      │ Resolver   │
│ thumbnail  │ thumbnail  │ thumbnail  │
├────────────┼────────────┼────────────┤
│ Channel A  │ Channel B  │ Diag       │
│ thumbnail  │ thumbnail  │ thumbnail  │
└────────────┴────────────┴────────────┘
```

Changed sheets can be visually marked.

This gives the user a passive "control room" view while the agent works across several schematic pages.

The observer can update thumbnails in the background while keeping the current detail view fixed.

---

# 21. Breadcrumb Navigation

For deep hierarchies, show logical location explicitly.

Example:

```text
Root / Drive / Gate Driver / Phase U
```

Useful actions:

- go to parent;
- go to root;
- select sibling;
- open source metadata;
- compare same logical instance across snapshots.

Do not replace this with raw filesystem paths.

Hierarchy is the user's conceptual navigation model.

---

# 22. LIVE / HOLD / HISTORY Semantics for Schematics

## LIVE

- follow newest complete schematic render bundle;
- remain on the same logical sheet instance if it still exists;
- preserve pan/zoom for that sheet;
- mark changed sheets in the tree.

If the currently viewed instance was deleted:

- remain on last view briefly with a clear removed marker, or
- navigate to the closest existing parent.

Do not silently jump to an unrelated page.

## HOLD

HOLD freezes the **project snapshot**, not just the current SVG.

Example:

```text
HOLD · project snapshot #87
latest complete snapshot #92
```

While held, switching from `Power` to `Resolver` should show `Resolver` from #87, not #92.

This preserves coherent historical inspection.

## HISTORY

Selecting historical snapshot #61 means:

```text
all sheet navigation
→ project hierarchy as captured in #61
```

Even if sheets have since been added or removed.

This is particularly valuable for hierarchy edits.

---

# 23. Per-Sheet Viewport Memory

A schematic user often jumps among pages.

Store viewport state per logical instance:

```text
/Power
  center
  zoom

/Channel A
  center
  zoom

/Monitor/ADC A
  center
  zoom
```

When the user returns to a sheet, restore the previous location.

If the sheet layout changes drastically, the user can explicitly choose:

```text
Fit sheet
```

but automatic fit-on-update should not be the default.

---

# 24. Change Propagation Model

Separate these concepts:

```text
source file changed
logical sheet affected
render changed
semantic schematic content changed
```

They are related but not identical.

Example:

```text
channel.kicad_sch saved
```

may affect:

```text
Channel A
Channel B
Channel C
Channel D
```

because all are instances of the same physical file.

A root hierarchy edit may affect:

```text
parent sheet
child identity
page ordering
instance references
```

even if some child file bytes are unchanged.

The observer should model this explicitly.

---

# 25. Project-Level Change Summary

A useful summary is not:

```text
3 files changed
```

but:

```text
Since reviewed snapshot #71

Logical sheets affected:
  Power
  Channel A
  Channel B

Physical source files changed:
  power.kicad_sch
  channel.kicad_sch

Hierarchy:
  no structural changes
```

This is much closer to what the human wants to know.

---

# 26. Shared Sheet Awareness

A shared child file deserves explicit representation.

Possible optional UI:

```text
Channel A
  source: channel.kicad_sch
  shared source ×2

Channel B
  source: channel.kicad_sch
  shared source ×2
```

This helps explain why one source save causes multiple pages to update.

However, shared-file indicators should not clutter the normal view.

They are diagnostic metadata, not the primary navigation model.

---

# 27. Hierarchy Changes

The observer should detect changes such as:

```text
sheet added
sheet removed
sheet renamed
sheet moved to another parent
source file changed
shared sheet instantiated again
hierarchical pin set changed
```

For the first semantic-diff version, it is enough to report structure:

```text
Hierarchy changes:
  + /Diagnostics/Thermal
  - /Legacy ADC
  renamed: /Power/Driver → /Power/Gate Driver
```

Do not attempt to infer why the agent made the change.

---

# 28. Rendering and Annotation Context

A child schematic should not automatically be treated as independently renderable truth.

The logical appearance or displayed instance information can depend on project hierarchy and instance context.

Therefore:

- render through the root where practical;
- identify outputs by logical instance;
- do not cache solely by child-file hash if output can vary by instance;
- include relevant project and hierarchy context in render cache keys.

A safe cache key may conceptually depend on:

```text
root project state
logical instance path
source file content
render settings
KiCad version
```

rather than:

```text
channel.kicad_sch hash only
```

---

# 29. Render Cache

A shared source file may generate multiple logical rendered outputs.

Example:

```text
channel.kicad_sch
   ├─ Channel A render
   └─ Channel B render
```

Even if geometry is mostly identical, do not deduplicate rendered pages until it has been proven that instance-specific output is equivalent.

Premature deduplication risks displaying the wrong references or context.

Raw source snapshots can still be deduplicated by content hash where appropriate.

---

# 30. SVG Output Mapping

KiCad CLI exports multiple SVG files for a hierarchical design.

The observer must establish a reliable mapping from each generated artifact to:

```text
logical instance path
sheet name
page label
source file
snapshot
```

Do not depend permanently on incidental filename formatting unless it is documented and validated for the target KiCad version.

During Phase 0, explicitly test:

- output naming;
- page ordering;
- duplicate/shared sheet instances;
- unusual page labels;
- spaces/non-ASCII sheet names;
- nested hierarchy;
- renamed sheets;
- multiple instances of one source file.

If robust output mapping cannot be established from filenames alone, use the parsed hierarchy plus export sequencing/metadata or another verified mapping mechanism.

---

# 31. Schematic-Specific Comparison

Visual comparison should operate per logical instance.

Examples:

```text
Current /Power vs previous /Power
Current /Power vs reviewed-baseline /Power
```

Project-level comparison should first answer:

```text
which logical sheets changed?
```

then let the user open one changed sheet.

This is better than placing every page into one giant diff.

---

# 32. Visual Diff

Initial visual diff may detect changed pixels/vector regions on a page.

Call this:

```text
visual change
```

not automatically:

```text
wire changed
symbol moved
net changed
```

because appearance can change due to:

- text;
- reference annotation;
- drawing sheet;
- theme;
- page fields;
- symbol graphics;
- wire geometry.

Semantic interpretation belongs to a later parser/analyzer.

---

# 33. Future Semantic Schematic Diff

Possible later categories:

```text
symbol added / removed / moved / rotated
symbol property changed
wire added / removed / reshaped
junction changed
label changed
global label changed
hierarchical label changed
bus changed
sheet pin changed
sheet instance added / removed
sheet source changed
text / graphic changed
```

These should be tied to stable object UUIDs where possible.

As with PCB mode:

> observed structure first, engineering judgment later.

---

# 34. Net and Connectivity Features

A later Schematic Observer may support:

```text
search net label
highlight symbols/pins/wires associated with a net
navigate hierarchical connections
```

This becomes more complex across hierarchy.

Distinguish:

```text
same displayed label text
```

from:

```text
same resolved electrical net in full hierarchy
```

and distinguish both from:

```text
verified electrical correctness
```

The initial schematic observer does not need to solve full connectivity semantics.

---

# 35. ERC Integration

ERC should remain an optional sidecar.

If enabled:

```text
Snapshot #87
  render complete
  ERC result complete

Snapshot #88
  render complete
  ERC pending
```

The UI must not display:

```text
ERC PASS
```

without showing which project snapshot was checked.

As with PCB DRC, rendering success and ERC success are independent statuses.

---

# 36. Review Baseline for Multi-Sheet Designs

Review baseline becomes especially valuable on schematics.

The user can mark:

```text
Reviewed project snapshot #50
```

Later:

```text
Current #73
```

The observer can summarize:

```text
Changed since review:

Root             unchanged
Power            changed
Channel A        changed
Channel B        changed
Diagnostics      unchanged
```

Because `Channel A` and `Channel B` may share one source file, the observer can additionally explain:

```text
Channel A and Channel B changed because both instantiate channel.kicad_sch
```

This is exactly the sort of context a plain filesystem diff lacks.

---

# 37. Multi-Root / Multi-Project Repository

A repository may contain:

```text
main_controller/
  main_controller.kicad_pro
  main_controller.kicad_sch

programmer/
  programmer.kicad_pro
  programmer.kicad_sch

test_fixture/
  fixture.kicad_pro
  fixture.kicad_sch
```

Do not merge these into one sheet tree.

Recommended model:

```text
Observer Workspace
├─ Main Controller
├─ Programmer
└─ Test Fixture
```

Each project has:

- independent root;
- independent snapshot sequence;
- independent history;
- independent LIVE/HOLD state;
- separate render queue or controlled shared scheduler.

Workspace support is a later feature.

The MVP only needs one root schematic per session.

---

# 38. Proposed UI Modes

## Detail Mode

Primary working view.

```text
hierarchy tree + one large sheet
```

## Overview Mode

Passive monitoring.

```text
thumbnail grid + changed indicators
```

## Compare Mode

Review.

```text
same logical sheet
snapshot A vs snapshot B
```

The application should not try to show every feature simultaneously.

---

# 39. Recommended MVP

The schematic MVP should be deliberately conservative.

Required:

- explicit root schematic selection;
- recursive hierarchy discovery;
- watch root + reachable child `.kicad_sch` files;
- project-level debounce/stable capture;
- immutable dependency-set snapshot;
- whole-design `kicad-cli sch export svg`;
- reliable output-to-instance mapping;
- hierarchy tree;
- page navigation;
- pan/zoom;
- per-sheet viewport memory;
- LIVE;
- HOLD;
- bounded HISTORY;
- changed-sheet markers;
- freshness/status display;
- last-known-good project bundle;
- no source modification.

Not required in MVP:

- semantic wire diff;
- full connectivity analysis;
- ERC;
- project overview mosaic;
- selective page rendering;
- agent integration;
- schematic editing;
- automatic navigation to active sheet;
- full workspace/multi-project mode.

---

# 40. Schematic MVP Functional Requirements

## SCH-FR-001 — Root Selection

The user shall select one root schematic/project as the observer session anchor.

## SCH-FR-002 — Recursive Hierarchy Discovery

The observer shall discover reachable hierarchical schematic dependencies from the root.

## SCH-FR-003 — Logical Instance Model

The observer shall represent sheet instances independently from physical `.kicad_sch` source files.

## SCH-FR-004 — Shared File Support

If one source schematic file is instantiated multiple times, all logical instances shall remain separately navigable.

## SCH-FR-005 — Dynamic Dependency Update

If hierarchy references change, the observer shall update the dependency graph without requiring a restart.

## SCH-FR-006 — Project-Level Stable Capture

The observer shall capture a stable dependency set before rendering.

## SCH-FR-007 — Root-Context Rendering

The observer shall render the design using root-project context where required for correct logical sheet output.

## SCH-FR-008 — Complete Bundle Publication

The MVP shall publish a new project render bundle only after all required logical pages for that snapshot are rendered and mapped.

## SCH-FR-009 — Sheet Tree

The viewer shall present the logical hierarchy, not merely a flat source-file list.

## SCH-FR-010 — Sheet Navigation

The user shall be able to navigate among logical sheet instances.

## SCH-FR-011 — Per-Sheet View State

The viewer shall retain pan/zoom state independently for logical sheets.

## SCH-FR-012 — No Auto-Jump

A change in another sheet shall not automatically move the user away from the sheet currently being inspected.

## SCH-FR-013 — Affected-Sheet Indication

When a physical source-file change affects multiple logical instances, all affected instances shall be identifiable in the UI.

## SCH-FR-014 — Snapshot-Coherent HOLD

When HOLD is active, navigation among sheets shall remain within the held project snapshot.

## SCH-FR-015 — Historical Hierarchy

When viewing a historical snapshot, the viewer shall show the hierarchy as it existed in that snapshot.

## SCH-FR-016 — Missing Child Handling

An unresolved referenced child schematic shall produce an explicit observer state rather than crashing the session.

---

# 41. Schematic Acceptance Tests

## SCH-AT-001 — Simple Two-Sheet Project

Given a root and one child sheet, changes to either are reflected in a new complete render bundle.

## SCH-AT-002 — Nested Hierarchy

Given:

```text
Root → A → B → C
```

all four logical sheets are discovered and navigable.

## SCH-AT-003 — Shared Child File

Given:

```text
Root
├─ Channel A → channel.kicad_sch
└─ Channel B → channel.kicad_sch
```

the tree shows two logical instances and one source edit marks both affected.

## SCH-AT-004 — Shared Child Rendering Context

Both instances of a shared child render using the correct project-instance context.

## SCH-AT-005 — Child Added

If the agent adds a new hierarchical child, the observer discovers and begins watching it without restart.

## SCH-AT-006 — Child Removed

If a child is removed from the hierarchy, later snapshots no longer expose it as a current sheet while older snapshots still do.

## SCH-AT-007 — Missing Child File

If a referenced child does not exist temporarily, the last known-good bundle remains visible and the missing dependency is reported.

## SCH-AT-008 — Rapid Multi-File Saves

If several schematic files are saved in rapid succession, the observer coalesces the activity and avoids an unbounded render backlog.

## SCH-AT-009 — Historical Consistency

While viewing historical snapshot #N, switching sheets never silently displays a page from snapshot #N+1.

## SCH-AT-010 — Current-Sheet Stability

If another sheet changes, the user's current sheet and viewport remain unchanged.

## SCH-AT-011 — Hierarchy Rename

Renaming a logical sheet updates the hierarchy while retaining historical names in old snapshots.

## SCH-AT-012 — Unicode / Spaces

Sheet names and paths containing spaces or non-ASCII text render and map correctly.

## SCH-AT-013 — Page Reordering

Changing sheet page identifiers does not cause the observer to confuse logical instance identity.

## SCH-AT-014 — Observer Failure

Stopping or crashing Schematic Observer does not prevent the agent from continuing to edit/save the project.

---

# 42. Phase 0 Tests Before Implementation

Before building the live watcher, test KiCad CLI against deliberately constructed hierarchy cases.

Create or select test designs covering:

### Case A — Flat

```text
Root only
```

### Case B — Basic Hierarchy

```text
Root
├─ Power
└─ MCU
```

### Case C — Nested

```text
Root
└─ Drive
    └─ Phase
        └─ Gate
```

### Case D — Reused Source File

```text
Root
├─ Channel A → channel.kicad_sch
├─ Channel B → channel.kicad_sch
└─ Channel C → channel.kicad_sch
```

### Case E — Hierarchy Mutation

During observation:

```text
add child
rename child
remove child
change child source file
```

Record:

- generated SVG count;
- output filenames;
- page order;
- output mapping;
- shared-instance behavior;
- render time;
- SVG size;
- reference/instance rendering behavior;
- failure behavior when one child is missing.

Do not design the production mapping layer until these results are known.

---

# 43. Suggested Scheduler Strategy

PCB and schematic observers may share one render scheduler.

Example priority:

```text
1. currently visible project/sheet
2. newest LIVE update
3. background thumbnails
4. historical rerenders
5. optional ERC/DRC jobs
```

However, a schematic whole-design export may itself generate all sheets in one CLI process.

The scheduler should therefore reason in terms of **render jobs**, not one process per sheet unless selective rendering is intentionally enabled later.

---

# 44. Suggested Observer Workspace Experience

Eventually:

```text
┌────────────────────────────────────────────┐
│ Project: GAS_IRT                          │
│ [PCB] [Schematic]                         │
└────────────────────────────────────────────┘
```

PCB mode:

```text
one board
many layers
```

Schematic mode:

```text
one logical design
many hierarchical sheet instances
```

The two modes can share:

- snapshot timeline;
- review baseline;
- LIVE/HOLD/HISTORY;
- freshness language;
- local server;
- cache management.

But they should retain separate navigation models.

---

# 45. Architectural Insight

For PCB observation, the dominant dimension is:

```text
one design × many layers
```

For schematic observation, the dominant dimensions are:

```text
one design
× many physical files
× many logical sheet instances
× hierarchy
```

Therefore the schematic observer should not be implemented as:

> “Run the PCB observer once for every `.kicad_sch` file.”

That approach fails specifically on the cases that matter most:

- hierarchy;
- shared child files;
- instance context;
- page navigation;
- project-level history.

Instead:

> **One observer session represents one root schematic design, and each rendered page is a logical sheet instance inside that design.**

---

# 46. One-Sentence Schematic Definition

> **Schematic Observer is a read-only spectator view of a complete KiCad schematic hierarchy: it watches the root and all reachable child files, captures stable project states, renders each logical sheet instance in root-project context, and lets the human move through LIVE/HOLD/HISTORY without disturbing the agent.**

---

# 47. Recommended Integration With the Existing PCB Observer Work

Do not fork the entire project into two unrelated tools.

Implement a shared core first or refactor toward one as soon as the first schematic prototype proves viable.

Recommended conceptual interfaces:

```text
ProjectAdapter
  discover_dependencies()
  capture_inputs()
  validate_snapshot()
  create_render_plan()
  run_render()
  build_manifest()

ViewerModel
  list_views()
  view_identity()
  default_view()
  preserve_view_state()
```

Possible adapters:

```text
PcbAdapter
  view = PCB layer composition

SchematicAdapter
  view = logical sheet instance
```

The key abstraction should not be “a file being watched”.

It should be:

> **a versioned design snapshot that exposes one or more human-viewable views.**

That abstraction supports both PCB and schematic observation without forcing either one into the other's geometry model.

---

# 48. Official KiCad Behaviors to Treat as Design Inputs

The current KiCad documentation relevant to this addendum states that:

- schematic files use the `.kicad_sch` format;
- hierarchical designs represent sheet instances using UUID-based instance paths;
- a hierarchical sheet stores a child file-name property and sheet-instance information;
- symbol instance information can vary by project and instance path;
- `kicad-cli sch export svg` exports each sheet in a schematic design to its own SVG file;
- the CLI can select pages with `--pages` when exporting from the root schematic.

These facts are why logical instance identity and root-context rendering are first-class requirements rather than optional implementation details.

References:

- KiCad Developer Documentation — **Schematic File Format**
- KiCad 10 Documentation — **Command-Line Interface / Schematic export: SVG**

---

# 49. Immediate Next Step

While the repository/document analysis is already running, the most useful additional prototype task is narrowly scoped:

> **Validate hierarchical schematic rendering before designing the final watcher.**

Specifically, create one test project where the same `channel.kicad_sch` is instantiated at least twice, then determine:

1. how KiCad CLI names the per-sheet SVG outputs;
2. whether both shared instances are exported separately;
3. how reference designators and other instance-specific data appear;
4. how `--pages` addresses those two instances;
5. what happens after page reorder or sheet rename;
6. whether a child-file-only edit is reflected correctly when export is invoked from the root;
7. render latency for 1, 5, 10, and 20 logical pages.

The result of this test should determine whether the first implementation uses:

```text
whole-project export on every stable snapshot
```

or whether selective per-page rendering is safe enough to introduce immediately.

Until that experiment is complete, whole-design export should be treated as the conservative baseline.
