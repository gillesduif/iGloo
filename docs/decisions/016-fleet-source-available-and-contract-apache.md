# ADR-016: Fleet is source-available in its own repository; the command contract is Apache-2.0

**Status:** Accepted
**Date:** 2026-11-07

## Context

iGloo is GPL-3.0-or-later ([ADR-010](010-relicense-gpl3.md)). Fleet is the
product for organisations and will be sold. GPL permits anyone to sell, host
and build a business on the code as long as modifications are published, and
a hosted Fleet Server is never distributed, so GPL would publish nothing. No
open-source license forbids a competing commercial offering; that needs a
non-compete clause, which only source-available licenses carry.

Every commit in the repository is by the maintainer (dependabot commits are
version bumps), so relicensing needs no external consent. Contributions are
accepted under a DCO, which certifies the contributor's right to submit under
the project license and grants no right to relicense commercially.

Until this decision `Igloo.Fleet.Agent` referenced `Igloo.Core.Host` in
process beside its CLI path (Architecture Convergence Plan AC-10). A proprietary
or source-available Agent linking GPL Core and contributor-written GPL plugins
in one process would make the combined work GPL-bound.

## Decision

1. **Licenses by directory.** `src/Igloo.Core*`, `Igloo.Preflight`,
   `Igloo.Iso`, `Igloo.Migration`, `Igloo.UsbWriter`, `Igloo.Cli`,
   `Igloo.Community.App`, `Igloo.One.App`, the mascot projects and every
   `distros/` plugin stay GPL-3.0-or-later. `src/Igloo.Command.Contracts` and
   `src/Igloo.Cli.Client` become Apache-2.0, each with its own `LICENSE`,
   `NOTICE` and package metadata, published as NuGet packages
   `Igloo.Command.Contracts` and `Igloo.Cli.Client` (first version
   `1.0.0-alpha.1`). Fleet is licensed under the Functional Source License,
   FSL-1.1-ALv2, plus a commercial license for customers.
2. **Fleet moves to its own repository**, `gillesduif/iGloo-Fleet`, with its
   history extracted from this one: `Igloo.Fleet.Agent`, `Contracts`,
   `Domain`, `Persistence`, `Server`, `Web`, `Igloo.Fleet.Tests`,
   `Igloo.Fleet.Web.UiTests` and the two `scripts/test-fleet-phase*.ps1`
   demos. The Fleet documentation in `docs/fleet/` and `docs/07 - Fleet/`
   stays in this vault; the Fleet README links to it.
3. **AC-10 is done.** The Agent references `Igloo.Cli.Client` and
   `Igloo.Command.Contracts` only. The observation and recoverable-mutation
   record types it needs (`Observation<T>`, `WindowsStorageSnapshot`,
   `BitLockerVolumeObservation`, `StorageState`, `BootState`,
   `IRecoverableMutationAdapter<T>` and companions) are Fleet-owned copies in
   `Igloo.Fleet.Domain`; `BootRestorationAdapter`, which nothing in Core used,
   moved there with its tests. The Agent reports the contract assembly version
   as `IglooVersion`, since it no longer links the engine.
4. **Contribution terms.** This repository keeps the DCO; contributions to the
   two Apache-2.0 projects are accepted under Apache-2.0. The Fleet repository
   accepts no external contributions until a contributor license agreement is
   in place.

## Rationale

- FSL forbids exactly the use the maintainer wants to prevent: a competing
  commercial product or service built on Fleet. Each version converts to
  Apache-2.0 two years after release, which rules out lock-in for public-sector
  buyers. The text is fixed, so there is nothing to draft; BSL 1.1 would need a
  custom Additional Use Grant and change date.
- Community, Core, CLI and the plugins have been GPL since release and stay
  that way; released GPL code cannot be retracted. Fleet has never shipped,
  so nothing is retracted.
- A separate repository gives GitHub one license per repository, a CLA that
  applies only where it is needed, and a Fleet that can stay private until
  launch. The process boundary of `Igloo.Cli.exe` with a typed JSON Lines
  contract is the mechanism the GPL FAQ treats as "normally separate
  programs"; making the contract and client Apache-2.0 removes any doubt for
  Fleet and for third-party integrators alike.
- The Fleet-owned record copies mirror what any external integrator would do:
  deserialize the CLI's JSON into its own types. Sharing the Core types would
  either keep the GPL link or push engine types into the contract package.

## Consequences

- **Positive:** the licensing story is per repository and per package;
  Fleet Server and Web already had no Core reference, and the Agent now has
  none; the drift register's DR-01 observation about the Agent is closed.
- **Negative:** the Fleet repository needs the two packages from a feed. Until
  they are on nuget.org, a sibling checkout of this repository with
  `artifacts/packages` produced by `dotnet pack` serves as the feed. The
  Agent's packaged CLI must be staged from an iGloo build
  (`build/IglooCli.targets` in the Fleet repository) instead of being built
  in the same solution. The record copies can drift from Core's; the JSON
  shape is the contract, not the C# type.
- **Open for the maintainer:** register the `iGloo` trademark, have counsel
  review the FSL application and draft the commercial license, publish the
  packages, and create the GitHub repository for the extracted Fleet history.
  This ADR does not claim any of those happened.
