# IME Log Design

## Purpose

This note captures how IME log data should be structured and presented so an administrator can quickly answer operational questions instead of manually interpreting raw log lines.

The current product already exposes a timeline with `Time`, `Severity`, `Flow`, `Phase`, `Component`, `Correlation`, `Message`, `File`, and `Line`, backed by [ImeLogTimelineEntry.cs](../src/Intune.Services/Models/ImeLogTimelineEntry.cs), [IntuneAgentView.xaml](../src/Plugins.IntuneAgent/UI/IntuneAgentView.xaml), and [IntuneAgentViewModel.cs](../src/Plugins.IntuneAgent/ViewModels/IntuneAgentViewModel.cs).

That is a useful base, but it is still event-centric. A stronger model parses individual rows, extracts correlation keys, and builds stateful flows per app, script, policy, user, and session.

## Problem

Raw or lightly structured log rows are not enough for the main admin questions:

- Why did this application not install?
- Did detection run, and what did it conclude?
- Did a remediation script run, and did it fix anything?
- Was the failure in applicability, download, enforcement, reporting, or check-in?
- What changed after `syncapp`, IME restart, or state cleanup?
- Is this one failed attempt, a retry loop, or a stale state issue?

The UI should therefore optimize for entities and outcomes, not only for lines.

## Target Model

IME log information should exist on three levels.

### 1. Raw Events

Each parsed line remains available for audit and detail inspection.

Recommended fields:

- `timestamp`
- `source_file`
- `line_number`
- `severity`
- `component`
- `message`
- `raw_message`
- `subsystem`
- `flow`
- `phase`
- `effect`
- `entity_type`
- `entity_id`
- `session_id`
- `policy_id`
- `user_id`
- `device_context`
- `result`
- `result_code`

This is the minimum level for exact troubleshooting and export.

### 2. Correlated Flows

Rows should be grouped into correlated runs such as:

- application installation / detection / reporting
- remediation or health script execution
- policy sync
- status service check-in
- managed installer activity

Suggested aggregate model:

```csharp
public sealed record ImeCorrelatedFlow(
    string FlowType,
    string EntityType,
    string EntityId,
    string SessionId,
    string PolicyId,
    string UserId,
    string DeviceContext,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    TimeSpan? Duration,
    string CurrentState,
    string LastPhase,
    string Result,
    string ResultCode,
    int AttemptCount,
    bool IsComplete,
    bool IsRetry,
    string Summary,
    IReadOnlyList<ImeLogTimelineEntry> Events);
```

The important point is not the exact class shape. The important point is that a flow represents one understandable operational unit.

### 3. Summaries

Flows should feed compact summaries that answer the most common admin questions without opening raw logs.

Each summary row should expose:

- entity
- current state
- last completed phase
- result and result code
- first seen / last seen
- duration
- retry count
- latest explanatory message

## Core Correlation Keys

The parser guidance already points in the right direction. Flow correlation should primarily use:

- `app_id`
- `policy_id`
- `session_id`
- `user_id`

Additional useful correlation dimensions:

- system vs. user context
- sidecar or executor process identity
- service restart boundaries
- IME check-in window

Without these keys, detection, install, remediation, and reporting steps from different runs will be mixed together.

## Main Entity Views

The UI should offer purpose-built views for the most important IME workloads.

### Application View

Per app:

- applicability
- detection before install
- content resolution and download
- enforcement / execution
- detection after install
- reporting
- retry history

This is the primary answer surface for "why not installed?".

### Remediation / Health Script View

Per script:

- detection start and result
- remediation start and result
- post-remediation detection
- final report state
- script exit code and timeout information

This is the primary answer surface for "did it actually remediate?".

### Policy Sync View

Per sync run:

- sync start
- service interaction
- payload received
- policies expanded to downstream work
- resulting app or script actions

This is the primary answer surface for "did IME even get new policy?".

### Reporting View

Per app or script result:

- local result creation
- send to service
- acknowledgement or retry
- dropped or stale report conditions

This matters when local execution succeeded but the portal view is still wrong.

### Run View

Per check-in or IME restart window:

- what started in this run
- what completed
- what remained pending
- what failed

This is the right view after an operator triggers an action such as `Restart IME`.

## Required Administrator Functions

### 1. Grouping and Filtering

Minimum useful filters:

- by app
- by script
- by policy
- by session
- by user
- by flow type
- by result
- only failed
- only incomplete
- only latest run

The current row filtering is useful, but it should be extended from line filtering to flow filtering.

### 2. Explain Failure

The UI should generate a short explanation from the correlated flow, for example:

- detection says app already installed
- applicability rejected device context
- content download failed
- installer exited with code `...`
- post-install detection still returned not installed
- reporting did not complete

This should be deterministic and based on observed phases, not AI-generated free text.

### 3. State Transition View

Every major flow should show state progression, for example:

`Detected -> Applicable -> Downloaded -> Installing -> DetectionFailed -> Reported`

This removes most of the guesswork from app troubleshooting.

### 4. Attempt / Retry History

Admins need to know whether they are looking at:

- a first attempt
- a repeated retry
- a failure loop
- an old failure with no new execution

That requires attempt counters and run boundaries.

## Shared Log Reader TODOs

These items apply to the extracted shared log-reading infrastructure and the current Windows Update log viewer implementation.

- TODO: Remove synchronous waits from refresh shutdown paths. `StopInstallStatusAutoRefresh()` should become fully async so the UI cannot stall while a background refresh task is stopping.
- TODO: Reduce UI churn when rebuilding visible log rows. Avoid `Clear()` plus full re-add when only a small sliding window change occurred.
- TODO: Make polling intervals and tail limits configurable per log source so slow links and very large logs can use less aggressive defaults.
- TODO: Add optional paging or "load older" behavior for very large logs so analysis scenarios can move outside the live window without forcing the grid to render everything at once.
- TODO: Consider a reusable WPF attached behavior for auto-follow, pause-on-selection, and resume-on-end instead of keeping that logic local to one view.

### Completed

- DONE: Replace the forward-only tail scan with a backward tail reader that starts near the end of the file and stops after the last `N` lines.
- DONE: Add incremental follow support for install-progress logs so refreshes continue from the last file position instead of re-reading the file tail on every polling cycle.
- DONE: Open shared log file streams with explicit async-friendly file options and review buffer sizes for remote reads.

### 5. Before / After Action Comparison

For actions such as:

- `syncapp`
- IME restart
- clearing failed state
- deleting app state

the UI should show what new flows appeared after the action and whether existing failed flows progressed.

### 6. Cross-Log Correlation

The UI should correlate across:

- `AppWorkload*.log`
- `IntuneManagementExtension*.log`
- `AgentExecutor*.log`
- `HealthScripts*.log`

An administrator should not have to manually jump between files to understand one app installation or one remediation run.

## Design Principle

The timeline remains important, but it should become the drill-down view, not the primary view.

Primary views should answer:

- what entity was processed
- in what run
- how far it got
- why it stopped
- whether it retried
- what the final observable result is

## Suggested Implementation Steps

### Step 1

Keep `ImeLogTimelineEntry` as the raw event model and add a new correlated model, for example `ImeCorrelatedFlow`.

### Step 2

Move parser output beyond `Flow`, `Phase`, and `CorrelationSummary` into stable correlation keys and flow states.

### Step 3

Add a summary grid for applications and scripts with:

- entity
- current state
- last phase
- result code
- duration
- attempt count
- latest message

### Step 4

Add failure-focused filters:

- failed only
- incomplete only
- latest run only
- changed after operator action

### Step 5

Add an explain panel for the selected flow that reconstructs the shortest useful narrative from the correlated events.

## Non-Goals

The goal is not to hide raw logs or replace exact evidence with abstraction.

The goal is:

- keep raw lines available
- derive structured flows from them
- present summaries that match real admin tasks

If the UI only shows lines, administrators still have to mentally reconstruct the system. That is the part the product should do for them.
