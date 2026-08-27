# Database engineering guidance

The database deployment model for this repository is intentionally simple.

## Desired-state principle

Database deployment should bring a supported existing database to the schema
and data state required by the current application.

It does not need to reconstruct, authenticate, or model the repository's
historical migration path.

Do not build a migration framework or historical-schema interpreter.

## Existing databases

Assume an existing database may be old.

Deployment should:

- validate only external prerequisites required to perform the update safely;
- create missing application-owned objects;
- add or alter schema required by the current application;
- perform necessary data transformations;
- establish required current constraints and indexes;
- validate important final application invariants;
- be rerunnable/idempotent where practical;
- fail atomically when a required transformation cannot safely be performed.

Do not require an old database to exactly match a previously released schema
shape before upgrading it.

## Avoid historical-shape machinery

Unless a concrete correctness requirement demands otherwise, do not:

- enumerate every historical column combination;
- identify named historical releases;
- maintain historical schema fingerprints;
- canonicalize SQL definitions merely to recognize old versions;
- reject harmless additional DBA-created indexes or constraints;
- create exhaustive historical-schema fixtures;
- add generalized migration bookkeeping, version history, checksums, or
  baselines.

Validate only what is necessary to safely reach the desired state.

## Complexity budget

Treat substantial increases in deployment-code size as an architectural signal.

If a database change requires hundreds of lines of validation or state
classification, reconsider whether the implementation is solving a broader
problem than the application requires.

Prefer straightforward SQL that expresses:

current state -> necessary transformation -> desired state

over machinery that attempts to prove how the current state was produced.

## Review guidance

When reviewing database changes, evaluate both correctness and whether the
change preserves this intentionally simple deployment model.

A change can be technically correct and still be unsuitable if it introduces
unnecessary architectural complexity.

Fixes should simplify or preserve the design whenever possible rather than
continually adding exceptions to existing machinery.
