# IaC for Step 12b: Bicep over Pulumi

**Status:** accepted (re-evaluated 2026-08-30, while on Step 8). Implemented as compile-only Bicep in 12b (`infra/bicep/`); **applied in 12c**. CD home is GitHub Actions ([sister decision](./cd-github-actions-vs-azure-devops.md)).

## Context

Step 12c provisions the Azure target: Container Apps Environment + one Container App per Center, Service Bus, Event Hubs, PostgreSQL Flexible Server, Key Vault + managed identities, Log Analytics + App Insights, per `dev`/`prod` environment. The app is .NET 10 orchestrated locally by Aspire. Solo learning project; CD is GitHub Actions (`deploy-center.yml`, GHCR today, OIDC federation in 12c per the [CD contract](../cd-contract.md)).

The candidates: **Bicep** (Azure-native DSL), **Pulumi** (infra in C#), Terraform (dismissed below).

## Decision

**Bicep, with `azd` as the optional on-ramp.** Pulumi is a fine tool — arguably the nicer one for a .NET team in general — but for this repo it adds operational surface while removing the one free path to Azure this project has.

1. **Aspire's deployment toolchain emits Bicep, not Pulumi.** `azd init` / `azd up` / `azd infra synth` and the `Aspire.Hosting.Azure.*` packages generate Bicep from the AppHost model. The "optional `azd` profile" escape hatch in 12b only exists because the IaC is Bicep: the generated output is the scaffold `infra/bicep/` starts from. Choosing Pulumi means hand-writing every resource above from scratch *and* maintaining it apart from the toolchain the app already uses.
2. **No state backend.** Bicep deployments are ARM deployments — Azure itself is the state. Pulumi needs a state backend on day one (Pulumi Cloud account, or self-managed blob storage plus a secrets provider), which means state auth, locking, and secret encryption as new CI concerns for ~10 resources.
3. **CI stays thin.** The planned CD path is `az containerapp update` or `bicep what-if` + `az deployment group create` — `azure/login` with the already-planned OIDC federation and nothing else. Pulumi adds a CLI install, an access token secret, and `pulumi login` to `deploy-center.yml`.
4. **The scale doesn't justify a general-purpose language.** Pulumi pays off with many stacks, many environments, or programmatic logic. 12b is a fixed resource list in two parameter files; Bicep modules cover it. The portability seam DuckNet actually exercises is `IEventBus` (Step 11 RabbitMQ → 12b Service Bus) — application code, not IaC.
5. **Learning value.** ARM/Bicep reads directly onto any Azure estate; it is the explicit, interview-friendly form of what `azd` would generate anyway (see [azure-deployment.md](../azure-deployment.md)).

## What Pulumi would buy, and why it doesn't tip the scale here

- **C# end-to-end** — same language as the app, shared types, unit-testable infra. DuckNet's infra has no logic worth testing; it is a static resource list.
- **Multi-cloud** — the plan is Azure-only.
- **Preview/diff ergonomics** — `bicep what-if` covers the same need.

**Revisit trigger:** the target becomes multi-cloud, or the infrastructure grows real branching/programmatic logic (per-tenant stacks, dynamic topology). Either would reopen this decision in Pulumi's favor.

## Terraform

Not in contention: a third language (HCL) with third-party provider lag on Container Apps features, no Aspire story, and it carries the same external-state cost as Pulumi without the C# upside.
