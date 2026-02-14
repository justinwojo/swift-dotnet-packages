# AGENTS.md

## Scope
Instructions for Codex in this repository.

## Default agent mode
- Codex is primarily a focused plan and code reviewer in this repository.
- Do not execute a plan, run implementation steps, or make code changes unless the user explicitly asks for execution/implementation.
- If a request is ambiguous, stay in review mode and ask for confirmation before making edits or running execution workflows.

## Hard constraints
- Detect runtime first (`uname -s`).
- If runtime is not macOS (`Darwin`), do not run mac-only tooling or scripts (for example: `xcodebuild`, iOS simulator validation scripts, or any script that requires Xcode/simulator runtimes).
- If runtime is macOS and required tools are installed, mac-only validation may be run directly.
- If macOS validation is required but unavailable in the current runtime, prepare changes and ask the user (or Claude on a Mac) to run the commands and share results.

## Project guidance
- Follow project conventions and workflows documented in `CLAUDE.md`.
- If guidance conflicts, this file takes precedence for Codex execution constraints.

## Codex execution notes (macOS)
- Codex runs on macOS but still inside a sandbox by default. For simulator/process operations, rerun with escalated permissions if needed (for example: `xcrun simctl ...`, `dotnet build` for iOS app packaging, `ps`).
- The default command timeout can be too short for iOS builds; use a longer timeout before assuming a hang.
- If `dotnet build` appears stuck after restore in Codex, rerun with escalated permissions; sandboxed runs can stall without producing useful output.
