# Changelog

All notable changes to AbpWorkflow are written here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the packages follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html): before 1.0, a minor version may change the API.

## [Unreleased]

## [0.1.0] - 2026-11-11

The first release on nuget.org. The engine was built as `Toda.Workflows` before this and never published under that
name; a host that built it from source under those names renames them as below.

### Added

- `AbpWorkflow.Contracts`:
  - `IWorkflowItemKind` for items of a host's or a plugin's own;
  - `IWorkflowExtension` for conditions, checks and actions;
  - `IWorkflowStatusType` for steps that leave at once or wait;
  - `IWorkflowSignals`, item references and events;
  - the definition's format number (1).
- `AbpWorkflow`, the ABP module:
  - definitions with drafts and published versions, checked on save and publish (`WorkflowService`, including
    `CreateAsync` from a definition), and `WorkflowsModel.Json` for hosts that read or write definitions themselves;
  - the runner's phases;
  - `WorkflowItems` for start, transitions, approvals, runs and bindings;
  - built-in conditions, checks and actions (`field-value`, `person`, `required`, `changed`, `pattern`,
    `set-field`), whose settings schemas say what each setting holds (`format`, `x-field`, `x-enumTitles`) so a
    designer can offer pickers;
  - built-in status types (`automated`, `decision`, `wait`, `approval`);
  - the background worker and the tracer, with retention (`Workflows:Trace`);
  - the EF Core model a host maps (`ConfigureWorkflows`, schema `workflows`).
- Seams with defaults: `IWorkflowPeople`, `IWorkflowSiteContexts`, `IWorkflowEvents`, `IWorkflowExtensionSource`,
  `IWorkflowJoinSwitch`.
- Error codes `AbpWorkflow:Forbidden` and `AbpWorkflow:Conflict`.
- A sample host (`samples/AbpWorkflow.SampleHost`) and its PostgreSQL tests.

### Changed

- The packages are renamed, to be independent of any host (`docs/adr/0001-independent-of-any-host.md`):
  `Toda.Workflows.Contracts` is now `AbpWorkflow.Contracts` and `Toda.Workflows` is now `AbpWorkflow`, with their
  namespaces; `TodaWorkflowsModule` is now `AbpWorkflowModule`.
- The error codes are `AbpWorkflow:Forbidden` and `AbpWorkflow:Conflict` (were `Toda.Workflows:Forbidden` and
  `Toda.Workflows:Conflict`).
- The tests' server is named by `ABPWORKFLOW_TEST_POSTGRES` (was `TODA_WORKFLOW_TEST_POSTGRES`).
- No table, column or stored value changed: the tables stay in the `workflows` schema, so a host's migration for the
  rename is empty.
