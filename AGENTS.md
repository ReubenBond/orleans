# Agent Guidelines for Orleans

This document provides guidelines for AI agents working on the Orleans codebase.

## Task Workflow

Tasks are tracked using a simple file-based workflow in the `tasks/` directory (git-ignored):

```
tasks/
  todo/           # Tasks not yet started
  in-progress/    # Tasks currently being worked on
  done/           # Completed tasks
```

### Workflow Steps

1. **Planning**: Create markdown files in `tasks/todo/` describing the work to be done. Use numbered prefixes for ordering (e.g., `00-overview.md`, `01-first-task.md`).

2. **Starting Work**: Move the task file from `tasks/todo/` to `tasks/in-progress/` when beginning work. Update the file with progress notes.

3. **Completion**: Move the task file from `tasks/in-progress/` to `tasks/done/` when the task is complete. Add completion notes and any follow-up items.

### Task File Format

```markdown
# Task Title

## Overview
Brief description of the task.

## Tasks
- [ ] Subtask 1
- [ ] Subtask 2
- [x] Completed subtask

## Notes
Progress notes, decisions made, issues encountered.

## Files Changed
- `path/to/file.cs` - Description of changes
```

## Project Structure

- `src/` - Production source code
- `test/` - Test projects
- `samples/` - Sample applications
- `playground/` - Experimental/development applications

### Key Directories

- `src/Orleans.Runtime/` - Core runtime implementation
- `src/Orleans.Core.Abstractions/` - Public interfaces and abstractions
- `src/Orleans.Core/` - Core client functionality
- `src/Orleans.Serialization/` - Serialization framework

## Coding Conventions

- Follow existing code style in the file being modified
- Use nullable reference types (`#nullable enable`)
- Use source-generated logging (`[LoggerMessage]` attribute)
- Prefer `partial class` for classes with source-generated code
- Use `internal` for implementation details, `public` for API surface

## Testing

- Unit tests go in corresponding `test/` projects
- Use xUnit for testing
- Use `[Fact]` for single tests, `[Theory]` for parameterized tests
- Test categories: `[TestCategory("BVT")]` for basic verification, `[TestCategory("SlowBVT")]` for longer tests

## Building

```powershell
# Build the solution
dotnet build Orleans.sln

# Run tests
dotnet test Orleans.sln
```

## Common Patterns

### Grain Directory
- `IGrainDirectory` - Core interface for grain registration/lookup
- `LocalGrainDirectory` - Default DHT-based implementation
- `DistributedGrainDirectory` - Experimental Virtual Synchrony implementation

### Lifecycle
- Implement `ILifecycleParticipant<ISiloLifecycle>` for silo lifecycle hooks
- Use `ServiceLifecycleStage` constants for ordering

### Dependency Injection
- Use `TryAddSingleton` for optional services
- Use `AddFromExisting` to register existing instances under additional interfaces
