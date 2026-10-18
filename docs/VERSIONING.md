# Versioning rules

CLIo uses semantic versioning: `MAJOR.MINOR.PATCH`. The version exists in one place, `<Version>` in `CLIo.csproj`; everything else (`AppVersion`, the update check, release packaging, CLIo's own answers) reads it from there.

## When to bump

| Change | Bump | Example |
| --- | --- | --- |
| Breaking change to stored data, commands or the update format | MAJOR | a schema change older versions cannot read |
| New user-visible feature or command | MINOR | `__repeat`, recurring events |
| Bug fix, copy fix, refactor with no behavior change | PATCH | a date-detection fix |

A pull request that changes what the app does **must** bump the version. A change that only touches docs, tests or tooling does not.

## What must change together

1. `<Version>` in `CLIo.csproj`.
2. A new entry at the **top** of `PatchNotes` in `Services/SystemPromptBuilder.cs`, written for the user (what they can now do), with `ReleaseDate` updated if it is a release.

The build enforces this: `CheckVersionHasPatchNotes` in `CLIo.csproj` fails the build if the newest patch-notes entry is not exactly `<Version>`. Do not work around it.

## Releasing

Follow `docs/RELEASING.md`. After the release is published, tag the commit `v<version>` (the release does this) so the version, the tag and the signed package all name the same commit. Never reuse or move a tag, and never publish two different builds under one version.

## Why this is strict

Self-update compares versions to decide whether a newer build exists. If behavior changes without a bump, an installed copy and a newer one look identical and the update is skipped (or the wrong thing is reported). Merged work that is still labelled with an old version is a bug.
