# Changelog

All notable changes to this project will be documented here.

The project uses semantic versioning where practical.

## [1.0.10] - 2026-09-19

- Register imported projects with the Codex 26.x project model instead of
  writing folder paths into `project-order`: imported projects now get
  `local-projects` entries, `thread-project-assignments`, and
  `sidebar-project-thread-orders`, so the sidebar keeps showing every project
  after a migration.
- Stop mixing path strings into the project id list. Path-shaped leftovers from
  previous runs are removed from `project-order` while the legacy
  workspace-root arrays keep their paths for older builds and the folder
  picker.
- Link imported threads to their project row in `state_5.sqlite`
  (`threads.project_id`) when the matching app-server project id is already
  known, without touching existing assignments.

## [Unreleased]

- Export backups as complete `.codex` directory copies instead of minimal
  session-only folders, preserving databases, settings, Skills, logs, caches,
  and other local contents while excluding root-level login credential files.
- Name exported folders `Codex_backup` so they remain visible on macOS and Linux.

## [1.0.9] - 2026-09-18

- Stop reporting false "rollout path is missing or stale" issues on Windows:
  paths are now compared with the verbatim (`\\?\`) prefix and separator/case
  differences normalized, and a thread that owns several rollout files (for
  example after a continuation) is validated once instead of once per file.
- Log the complete diagnostics report: quick diagnostics and full verification
  now write every field, every issue, and the `codex doctor --json` output into
  the log drawer instead of leaving it empty or showing a single summary line.
- Report a failed full verification with its details (the report is returned
  even when issues exist), and add a Copy button to the log drawer.

## [1.0.7] - 2026-06-21

- Use a zero-pixel optical text offset on Windows while retaining the existing macOS/Linux alignment.
- Embed the project icon and version metadata into Windows executables.
- Hide the console window when launching the Windows release GUI.

## [1.0.6] - 2026-06-21

- Added multi-select deletion for rollback snapshots.
- Added a default-selected tri-state “Select all” control for rollback records.
- Embedded user images and tool screenshots directly in exported single-file HTML.
- Added completion dialogs for import, backup export, HTML export, and path repair.
- Added Chinese and English interfaces with system-language detection.
- Added path repair, parent-directory mapping, and HTML conversation export.
- Added selective project/session import, conflict preview, transactional rollback, and Codex Desktop project registration.
