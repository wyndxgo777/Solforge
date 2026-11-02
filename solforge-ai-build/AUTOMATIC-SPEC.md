# Solforge AI automatic mode

This is the current application contract. Source owners are packages/experimental/solforge-automatic, solforge-model-tiers and the desktop Solforge overlays.

## Goal lifecycle

/automatic <goal> starts a goal in the receiving chat and replaces the prior app-wide automatic goal. /automatic or /automatic status reads status; /automatic stop|off|cancel stops it. Only the exact command name dispatches. Strip buttons use /automatic-strip stop|hide|resume|discard.

States are Off, Running, Complete, Blocked, Stopped and Limit. Only Running authorizes continuation. Defaults are unlimited nudges (maxNudges=0), 240 minutes, 45 idle seconds and three rounds before a tactic-change nudge. Options are read on each tick. Goal time includes time spent closed after a resume.

A goal binds to one root Session. Its delegated workers belong to its runtime ownership chain, while only the main executor changes goal state. Reports recheck the same goal and Session after asynchronous review. Replacing, stopping, resuming or completing a goal invalidates outstanding reports.

## Model team

1. A user-started automatic goal receives a read-only Frontier plan before the Mid executor's first step.
2. Mid executes with available files, commands, relevant REA and Solforge/Sol MCP capabilities and task skills. It delegates isolated file/function work to Low.
   Subscription Mid workers request the admitted parent subagent tool through a solforge-delegate JSON block. The parent launches the configured Low route and returns its activation/result; a delegation request cannot also mark the goal complete. The desktop config sets child model defaults on tool-subagent, while the subagent service owns depth and capacity.
3. Mid reports a finished step with goal_progress(done, remaining?, evidence?), including relevant code/diff excerpts and verification output.
4. The configured Frontier ladder reviews only the explicit goal, report, evidence and remaining work. Unrelated retained conversation content is excluded.
5. Corrections return to Mid in the tool result and remaining-work field. A successful progress report ends that execution round; the watchdog schedules continuation when idle.
6. goal_complete(summary, evidence) requires accepted Frontier review. Rejection keeps the goal Running and immediately returns corrections to Mid. An unavailable or malformed review cannot complete the goal and yields an actionable blocked state.

Frontier receives no action-capable editor MCP or write grant. It distinguishes catalogs, synthetic selftests, endpoint health and actual gameplay evidence. Fallback uses only the configured tier ladder. A worker that may already have written files is never replayed on the next model.

## Tool admission and delegated clients

automatic_start(goal, replace?, send_prompt?), automatic_status() and automatic_stop(reason?) are application tools. Goal-report tools are admitted only while this Session owns the live goal. Execution repeats the check; an inactive or different chat cannot mutate it.

Historical prompts do not imply current authority after a restart. A saved goal is offered for explicit resume. Resume queues a fresh start prompt and retains progress, remaining work, original time and nudge count.

Subscription CLI workers cannot invoke the parent application's tools. Their solforge-result JSON block maps to an admitted parent goal tool. Progress includes done, remaining and evidence; completion includes summary/evidence; blocked names a user prerequisite. Missing goal tools in the worker MCP list are expected. Successful reports conclude the turn through the real executor result. Failed calls and rejected completion never produce a false Recorded success.

## File access and approvals

An explicit live Session Full access selection grants Mid/Low system-wide local file and command access; its starting directory is not a confinement boundary. Other modes retain their workspace policy, nested-directory access, tier write settings and one-turn approvals. Frontier remains read-only. Permission changes apply to subsequent worker turns.

Eligible one-time approvals are automatically allowed and logged while a goal the user typed or resumed covers the asking agent. An agent-started goal cannot grant itself this authority. The optional Auto-allow setting also covers eligible routine requests. Review denials, user hooks, prompt-enhancement consent, trust/elevation changes, sandbox escape and lasting grants remain explicit policy decisions. User questions are never auto-answered.

The desktop bridge answers eligible editor approvals according to the active goal and editor policy; requests and outcomes remain visible. Shared MCP processes are not restarted merely to reduce an instance count. A failed transport is distinct from a missing frame or gameplay feature.

## Continuation, stop and persistence

A nudge requires an idle live agent, no pending approval/question, an empty inbox and the configured quiet period. Busy turns and compaction postpone nudges. Repeated output gets a repeat nudge; rounds without progress get a tactic-change nudge. Time and optional nudge limits yield Limit.

The app never creates or restarts an agent for a nudge. A missing agent sets waitingForEngine. Non-transient terminal model failures stop the goal; transient failures permit the next nudge to retry.

automatic-goal.json contains Goal, Started, Nudges, Progress, Remaining and SessionId, saved with a temporary file and rename. Ended goals withdraw queued nudges and remove the save file. A running goal counts as workspace activity during idle gaps, preventing deletion while it can still wake. Archive-with-stop disarms continuation.

The strip exposes state, goal, source, nudge count, elapsed/quiet time, progress, remaining work, result and evidence. The live mode route refreshes connection and automatic-running state. Goal state, resume offers, one-time approvals and explicit Frontier review inputs/decisions are durable plugin records.
