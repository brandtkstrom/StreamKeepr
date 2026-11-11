# Retention automation is a non-goal

## Status

Accepted

## Context

The plan proposed optional `RetentionDays`/`MaxTotalGb` options and an hourly `RetentionService`, but defined no service, registration, task, or tests. The product decision is to keep recordings until the user deletes them.

## Decision

Retention automation is out of scope. The spec omits `RetentionDays`/`MaxTotalGb` and the hourly-service requirement; the plan document remains historical input. `DiskGuard.MinFreeGb` still stops recording when the disk is full; deletion is manual through the recording admin.

## Consequences

- No background deletion job or retention tests.
- Users must manage disk usage manually.
- Reintroducing retention is a new effort with its own decision.
