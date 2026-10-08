# Building the lifecycle model: plan and status

Design: `docs/research/lifecycle-model.md`. For people who use the tool: `docs/concepts.md`. This file is the working plan; its **status column is kept current** as work lands, so it survives between sessions. Progress-log entries (`docs/progress/state-and-apply.md`) carry the evidence.

## 1. Principles for the build

* **Breaking, in one direction.** Old verbs are removed, not aliased (owner). Sample projects and the docs move with the code.
* **Fix the bugs first, then restructure, then add.** The scheduled-refresh failure and the doubled row counts are independent of the new names and are fixed in phase 1.
* **One inner name, one outer name.** The command tree users see is new; the code keeps one `CommandSpec` per command and the JSON `command` field carries the new path (`connection deploy`). Tests use the new names.
* **Every phase ends green**: unit suite, the real-engine suite for anything that touches a database, a progress-log entry saying what was verified where, docs regenerated (`docs/commands.md` is generated from the command tree).
* **The tracking layout changes once** (layout 5, phase 5). Until then new facts go in existing columns.

## 2. Phases

| # | Phase | Delivers | Depends on | Breaking | Status |
|---|---|---|---|---|---|
| 0 | Docs | this plan, the design, `docs/concepts.md` | | no | **done** |
| 1.1 | Event ids; the "applied once" rule per event | scheduled `refresh`/`run` with unchanged content works every time; ids `dep-…`/`ref-…` in `plan_id` columns, plan hash in `plan_hash` | | report shows ids | **run lane done** (entry 132); deploy lane keeps its content id until phase 3 |
| 1.2 | `rows_loaded` | the rows inserted by a load, not the sum of every statement's count | | report numbers change | **done** (entry 133) |
| 1.3 | Plan noise | one DDB-424 per cause; each note once; no "not interactive" note without a question | | text only | planned |
| 2 | The command tree | `project`, `connection`, `ui`, `help` nouns; old verbs removed; JSON `command`, schemas, TUI catalog, MCP tool names, generated docs | 1 | **yes** | planned |
| 3 | `deploy` as one flow | `status` read-only; `deploy` interactive; `--write-plan`, `--apply-plan`, `--ack`, `--reason`; continuing an incomplete event; no `--resume` | 2 | yes | planned |
| 4 | Refresh from a compiled plan | `project compile` writes `refresh.plan.yml` with `requires`; `connection refresh` runs it; check levels `none`, `project`, `objects`, `live`; `--on-fail` | 2, 5 for `project` | yes | planned |
| 5 | Plan policy, events, layout 5 | `plans.*.keep/audit`, `event_log`, `plan_store`, statement log without tracking writes, `retention.statement_logs_days`; `init --upgrade` from 4 | 1 | tracking layout | planned |
| 6 | `status` and `monitor` views | structure/data/attention; the event timeline and drill-down | 5 | | planned |
| 7 | Hints | lane and disposition in every header, `Next:` blocks, `next` in JSON, one error per cause | 2 | | planned |
| 8 | Interfaces | terminal menu groups, web tabs, MCP tool names and `next`, lane chips | 2, 6, 7 | | planned |
| 9 | Close | templates' READMEs, skill, getting-started, operations guide, `docs/commands.md` regenerated, open items | all | | planned |
| later | `connection inspect`, `project model create`, `project tests list`, `connection tests run` | the new commands that are not renames | 2 | | planned |

## 3. Work items in detail

### Phase 1.1 Event ids and the rule

* Today: `Plan.Id` = date + 8 hex of the content hash; `migration_log.plan_id` is that id; `apply` refuses a plan whose id has a `completed` (or started) row (DDB-438); `run` writes the same id every time its content is the same.
* Change: a plan keeps its content id (file name, `plan.id`). Each `apply` or `run` creates an **event id** (`dep-<utc yyyyMMddTHHmmss>-<4 hex>` or `ref-…`). All tracking rows that carry `plan_id` carry the event id instead; `migration_log.plan_hash` carries the plan's content hash. The history reader and `report` read the event id where they read the plan id.
* Rules: look the plan's hash up in `migration_log`; `completed` and the lane is deploy → refuse (DDB-438, names the event); incomplete → continue that event (reuse its id, skip finished steps); none, or lane refresh → new event.
* Tests: unit (id format, rule table); real engines: `run` twice with the same content succeeds twice; a stopped deploy continues when the same plan is applied again; a completed deploy plan is refused.

### Phase 1.2 `rows_loaded`

* Today the executor records `ExecuteNonQuery`'s return, which is the sum of the counts of the DELETE, SELECT INTO and INSERT of a load.
* Change as built: no change to the rendered scripts. The executor takes the count of the last statement that reports one (SQL Server: `StatementCompleted`; PostgreSQL: the per-statement counts of the command), which is the INSERT or MERGE of a load.
* Tests: a load of N rows records N on both engines, first load and reload.

### Phase 1.3 Noise

* Dedupe diagnostics at the command level (`Code`, `Location`, `Found`) before printing; collapse the per-file DDB-424 into one "rendered files are missing or out of date: run `project compile`" with the count; print the "not interactive" note only when a question is open.

### Phase 2 The command tree

* `CommandSpec` gains `Target` (`project`, `connection`, `ui`, `help`), `Verb` (and sub-verb), `Lane`, `Disposition`; `Name` becomes the full path (`connection deploy`).
* `CliApp.Build` builds the nested `System.CommandLine` tree from the specs; each leaf keeps its handler. `<c>` becomes a positional argument (optional when there is one default connection); `--connection` disappears.
* Every document's `command` is the full path; `schemas/output.schema.json` is regenerated for the new names (a script, not by hand); `TuiCatalog`, the MCP tool surface and `docs/commands.md` follow from the specs.
* Tests move to the new names mechanically where the behaviour is a rename (`validate` → `project compile --check` and so on); a test lists the command tree and fails on an old verb.
* Done when: no old verb parses; the unit suite and the conformance suite pass on the new names; README, templates and the skill use them.

### Phase 3 `deploy`

* `connection status` = today's `check` plus the freshness and attention lines. `connection deploy` wraps `PlanningSession` + the question loop + `ApplyEngine`; interactive prompts are today's `ConsolePrompter`; `--write-plan` stops before the apply; `--apply-plan` takes a file or a stored plan id; `--ack` reuses `AckCommand`'s recorder.
* Done when: every row of section 12 of the design for `plan`, `apply`, `ack` is reachable and tested on both engines, including continuing a stopped event.

### Phase 4 Refresh

* Compile emits `rendered/<c>/refresh.plan.yml` per connection: model order, operations, statement hashes, `requires` (object → shape hash) and the project hash. `connection refresh` loads it, applies the check level, runs the loads as an event of lane refresh.
* Done when: `refresh` runs with `--check none` without a catalog query (proved by a statement log with none), `objects` names the behind objects, `live` detects an out-of-band change, and a refresh never changes structure.

### Phase 5 Policy and layout 5

* Layout 5 adds `event_log` and `plan_store`; `init --upgrade` migrates 4 to 5; `ddl_log`/`run_log`/`schema_version` keep their columns. `plans.*` settings are read, validated (config schema) and acted on; the statement log omits tracking writes; retention prunes by age.

### Phases 6 to 9 are as in the design (sections 9 and 11); their tests are the generated command reference, the JSON schema test, the TUI pty walk-through, the web page tests and the MCP server tests.

## 4. Test strategy

| Layer | What | Where |
|---|---|---|
| Unit | id format, rule tables, the command tree, settings validation, hints present on every command, no old verb | `tests/DbDataBuild.Tests.Unit` |
| Golden | rendered scripts (phase 1.2), `docs/commands.md` | `GoldenFile`, `CommandReferenceTests` |
| Real engines | event rule, continuing an event, refresh checks, policy, layout upgrade | conformance, SQL Server 2022 and PostgreSQL 17 |
| Scale | refresh of 300 models with the `none` check issues no catalog query; 1,000 events in `monitor` | `DDB_SCALE=1` |
| Interfaces | terminal walk-through, web page, MCP tool list and descriptions | existing suites |

## 5. Risks

| Risk | Mitigation |
|---|---|
| The rename touches ~1,800 tests | mechanical script for pure renames; behaviour changes (`plan`/`apply`/`ack`) are rewritten by hand per phase |
| Layout 5 on live tracking tables | `init --upgrade` prints the script first (as for layout 4), tested from 4 on both engines |
| Fabric | none of this is run on Fabric; the matrix keeps saying so |
| Scope | each phase is committed and pushed alone and leaves the tool working |

## 6. Status log

| Date | Phase | Note |
|---|---|---|
| 2026-10-07 | 0 | design, plan and concepts written |
| 2026-10-07 | 1.1 | `run` gives each run an id `ref-<utc>-<4 hex>`: repeated runs of unchanged content no longer fail with DDB-438; verified on SQL Server 2022 and PostgreSQL 17 |
| 2026-10-07 | 1.2 | `rows_affected` of a load is the rows inserted; verified on both engines |
