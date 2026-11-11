# One Web project, strangler migration from the worker

## Status

Accepted

## Context

The current `StreamCaptureService` is a `Microsoft.NET.Sdk.Worker` proof of concept with a different domain model. The implementation plan targets a single ASP.NET Core `Microsoft.NET.Sdk.Web` project at `src/StreamKeepr` with `Domain/`, `Configuration/`, `Infrastructure/`, `Hosting/`, `Components/`, and `wwwroot/`, plus tests at `tests/StreamKeepr.Tests`.

## Decision

Migrate to one Web SDK project using a strangler: scaffold `src/StreamKeepr` beside the existing worker, port only the salvageable Twitch/media/options pieces, keep both projects buildable, then delete the old worker and its test project at the final step. Do not split the app into separate class libraries yet.

## Considered options

- **In-place conversion** of the worker to a Web SDK: rejected because it mixes the old state machine with the new host and makes rollback harder.
- **Fresh rewrite** with an immediate delete: rejected because it loses the buildable reference and the incremental verification path.
- **Split class libraries** (`Domain`, `Infrastructure`, `Web`): rejected for now because the plan defines one deployable project and no concrete test or build constraint requires the split.

## Consequences

- Two runnable projects exist temporarily; the solution and CI build both until the final switch.
- The new app becomes the runnable entry point, and the old worker is deleted once the ported pieces are covered.
- The new test project references the app from its first commit; the old vacuous test project is deleted with the old app.
- The container contract is delivered by the Hosting ticket; the old broken Docker assets are removed with the old project.
