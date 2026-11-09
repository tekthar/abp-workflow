# Changelog

All notable changes to Toda Workflow are written here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the packages follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html): before 1.0, a minor version may change the API.

## [Unreleased]

## [0.1.0-preview.1] - 2026-10-31

The first release as packages, after the engine ran Toda's records and assets.

### Added

- `Toda.Workflows.Contracts`:
  - `IWorkflowItemKind` for items of a host's or a plugin's own;
  - `IWorkflowExtension` for conditions, checks and actions;
  - `IWorkflowStatusType` for steps that leave at once or wait;
  - `IWorkflowSignals`, item references and events;
  - the definition's format number (1).
- `Toda.Workflows`, the ABP module:
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
- Error codes `Toda.Workflows:Forbidden` and `Toda.Workflows:Conflict`.
- A sample host (`samples/Toda.Workflows.SampleHost`) and its PostgreSQL tests.
