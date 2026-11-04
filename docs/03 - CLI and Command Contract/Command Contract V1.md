---
title: Command Contract V1
type: protocol
product: cli
status: in-progress
doc_status: current
validation_status: partial
availability: branch-only
graph_cluster: cli
last_reviewed: 2026-10-10
reviewed_commit: 366c1ec57c6e5bde6d452208d4ef6411ae99ebf1
sources: [docs/spec/command-contract/README.md, src/Igloo.Command.Contracts/CommandProtocol.cs, src/Igloo.Cli/Igloo.Cli/Program.cs]
aliases: []
tags: [igloo]
---

# Command Contract V1

The [checked-in specification](../spec/command-contract/README.md) and its [request](../spec/command-contract/request.schema.json), [result](../spec/command-contract/result.schema.json), [capability](../spec/command-contract/capabilities.schema.json) and [stream-event](../spec/command-contract/stream-event.schema.json) schemas are canonical for the current branch. This note is a navigation and scope guide, not a second specification. [[03 - CLI and Command Contract/Current Capability Registry]] records the registered IDs.

**CODE OBSERVATION:** `CommandProtocol.Version` is integer `1`. `CommandRequest` carries `protocolVersion`, `capabilityId`, integer `capabilityVersion`, UUID request/correlation IDs and typed `input`. `CommandResult.succeeded` and structured `error.code` determine domain outcome. Product, protocol, capability and schema versions are distinct. Unknown fields are rejected by the configured serializer, and a destructive semantic broadening requires a new capability version. Discovery must be consulted because a schema's existence does not mean an installed provider is available.

Human commands and `--json` use the CLI's dispatcher; `invoke --machine` emits a JSON Lines `hello`, `accepted`, progress and one terminal result over stdout after a stdin request. The first-party process client checks product/protocol hello and correlation IDs. Diagnostic stderr is not a success signal. A timeout, malformed stream or killed process leaves an accepted operation's outcome unknown until a durable status can be read; killing the process is not proof of cancellation. Current durable status is limited to ISO acquisition. These semantics are described in the canonical [specification](../spec/command-contract/README.md#executable-machine-transport-current-implementation) and inspected by `tests/Igloo.Command.Tests/CliProcessBoundaryTests.cs`.

The supplied whitepaper §§5–6 uses illustrative examples and explicitly delegates exact V1 JSON Schema to the Command Contract. Do not paste its `protocolVersion: "1.0"` or old field names into runnable instructions. This checkout's machine entry point is `Igloo.Cli.exe invoke --machine`; [the current spec](../spec/command-contract/README.md#discovery-versioning-and-errors) defines human commands. `iGloo.exe` is the separate Community WPF application. Process placement and metadata checks are not cryptographic signature verification, so [[02 - Architecture/Architecture Convergence Plan]] keeps packaging trust open.
