# Working on the dbdatabuild code base

dbdatabuild is a .NET 10 CLI that builds analytics tables and views on SQL Server, PostgreSQL and Fabric from DuckDB-dialect SQL. `DESIGN.md` is the design (sections marked "as built" describe what exists); `docs/progress/` is the log (`state-and-apply.md` entries, `REVIEW.md` for the owner, `OPEN-ITEMS.md` for what is unfinished). Read the section of DESIGN.md for the area you touch before changing it. If you are writing models or plans *with* the tool rather than changing the tool, you want the skill instead: `dbdatabuild agent-kit`.

## Build and test

```
dotnet build                                   # warnings are errors
dotnet test tests/DbDataBuild.Tests.Unit       # no database needed; about 15 seconds
scripts/test-engines.sh up                     # SQL Server 2022 and PostgreSQL 17 in docker, loopback only
eval "$(scripts/test-engines.sh env)"
dotnet test tests/DbDataBuild.Tests.Conformance   # real engines, about 3 minutes; skipped without the env vars
scripts/test-engines.sh down
UPDATE_GOLDEN=1 dotnet test tests/DbDataBuild.Tests.Unit   # rewrites golden files; review the diff
scripts/publish.sh linux-x64                   # one self-contained executable
scripts/test-duckdb-preview.sh                 # the unit tests against DuckDB's preview library (2.0 alpha); see docs/research/duckdb-2.0
```

## Rules the tests enforce (read before you add code)

- **Statements to a target go through `MutationGate` (writes) or `ReadSession` (reads), nowhere else** (`GateInvariantTests` scans the source). Logins come from `DBDATABUILD_<TARGET>_<READ|WRITE>`, with no fallback from one to the other. Driver messages are never echoed: failures report the exception type and error number (the real-engine scrub test checks this).
- **Every command** has a `CommandSpecs` entry (with its effect class), a case in `CliApp.Build`, a `--format json` document that satisfies a closed `data` schema in `schemas/output.schema.json` (edit it by hand; a test fails if a command has none), and appears in the TUI catalog automatically. A new option needs a description. `tui` is the one command with no JSON form.
- **Every diagnostic code** is in `DiagnosticCatalog.All`, has a fixture in `DiagnosticCatalogTests` (or is listed there with the test that covers it), and has `supported`, `fix` and `explanation` text. Never reuse a code.
- **The support matrix** (`matrix/constructs.yml`) is data: a row needs a fixture in `MatrixLinterTests`, a case in `spike/constructs.yml`, and a status per target. Changing it changes the matrix hash, which is in every rendered header, so regenerate the goldens (`UPDATE_GOLDEN=1`) and look at the diff. Do not use polyglot's `unsupportedLevel: raise`; the linter walks the AST.
- **Lowering and target rules** (`DbDataBuild.Lowering`, `DbDataBuild.Targets/Rules`): anything the lowerer cannot reproduce faithfully is refused (DDB-324), never approximated silently. A rule is tested three ways: the unit text, a differential run against DuckDB, and a real-engine test with the rows compared to DuckDB's.
- **Plan files and rendered files are hashed**; a test that edits one must expect a refusal. Rendered output is deterministic: no timestamps, no machine names.
- **Inputs a person edits** (YAML files, plans, answers) are covered by seeded mutation tests (`FuzzTests`); a loader must answer a damaged file with a diagnostic, never an exception.
- **The TUI is a client of the JSON surface**: it runs commands in process and shows their documents. Do not give it logic or a second code path to a database. Check screens with `scripts/tui_drive.py` (needs pyte); Terminal.Gui has no headless driver.

## Conventions

- Match the surrounding code: comments explain why, not what; names are long and plain. Records for data, static classes for pure logic, no hidden state.
- Prefer a table or a data file over a switch when the rules will grow (the decision table, the matrix, the hook events).
- When behavior changes, update in the same commit: the test, `DESIGN.md` ("as built" section), a numbered entry in `docs/progress/state-and-apply.md`, and `OPEN-ITEMS.md` if something is left undone. Say plainly what was verified on a real engine and what was not; Fabric has never been run.
- `native/` (the polyglot library) is built by `scripts/build-polyglot.sh` and is not committed.
