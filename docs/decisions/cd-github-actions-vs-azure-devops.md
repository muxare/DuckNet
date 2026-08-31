# CD home: GitHub Actions over Azure DevOps

**Status:** accepted (2026-08-31, Step 12a). Nothing Azure-side is provisioned yet. This records *why* Phase D keeps continuous delivery in GitHub Actions, so the one-line lock in [ImplementationPlan.md](../../ImplementationPlan.md) has an inspectable rationale.

Companion spec: [CD contract](../cd-contract.md) (identity planes, OIDC subjects, pipeline jobs).

## Context

Source of truth is already GitHub: the repo, PRs, [`ci.yml`](../../.github/workflows/ci.yml), ReviewFlow [`claude-review.yml`](../../.github/workflows/claude-review.yml), and [`deploy-center.yml`](../../.github/workflows/deploy-center.yml) (per-Center image → GHCR). Step 12c will deploy those images onto Azure Container Apps.

The candidates: **GitHub Actions** (extend what exists) vs **Azure DevOps Pipelines** (new product, new YAML, new identity).

## Decision

**GitHub Actions is the CD home.** Azure DevOps is not introduced. The pipeline that builds a Center is the pipeline that (from 12c) updates that Center’s Container App.

1. **The code already lives on GitHub.** Moving only CD to Azure DevOps would split CI from CD: two YAML dialects, two secret stores, two permission models, and a service connection to maintain for a repo that Azure DevOps does not host. That split is a real enterprise tax; it is not a teaching goal for DuckNet.
2. **2026 greenfield standard for this shape is Actions + OIDC → Azure.** [azure-deployment.md](../azure-deployment.md) already records that path. Workload identity federation (no `AZURE_CLIENT_SECRET`) is first-class on `azure/login`. Azure DevOps service connections can do the same, but they would be a second federation to the same Entra app for no gain.
3. **CCA-F D3 practice is already GitHub Actions.** Headless Claude review, path-filtered deploy, GitHub Environments as approval gates — that story stays one product. An ADO release pipeline would be a different exam anecdote and would not reuse ReviewFlow.
4. **Per-Center CD is already sketched in Actions.** `deploy-center.yml` already has `workflow_dispatch` (center × environment) and path-filtered fan-out. Step 12c adds an Azure job to that matrix; it does not rebuild the matrix in `azure-pipelines.yml`.
5. **Azure DevOps is the Telia-shaped *estate* answer, not this repo’s answer.** Large Nordic enterprises often still ship from ADO because the platform team, boards, and environments already live there. DuckNet is a GitHub-hosted lab teaching Center isolation and cloud mapping — not “how to live inside a 2018 VSTS org.”

## What Azure DevOps would buy, and why it doesn't tip the scale here

- **Boards + repos + pipelines in one Microsoft product** — this repo’s issues and PRs are already GitHub. Boards are not a DuckNet need.
- **Classic release gates / YAML environments** — GitHub Environments (`azure-dev`, `azure-prod`) plus required reviewers cover the same gate. Prod approval is the teaching point; the product name is not.
- **Self-hosted agents / private VNets** — not in scope. GitHub-hosted `ubuntu-latest` is enough to call `az` and push to ACR.
- **Alignment with a future employer’s ADO** — worth a *conversation* (“I know the split: GitHub for git-native teams, ADO when the platform team owns it”). Not worth operating two CI systems in this lab.

**Revisit trigger:** the repo is moved under an org that mandates Azure DevOps pipelines, or a platform team takes over deploy and already owns ADO service connections. Then CD could move; CI/review should still stay next to the code unless that org also mandates ADO repos.

## What this decision does *not* lock

- **IaC tool** — still Bicep; see [iac-bicep-vs-pulumi.md](./iac-bicep-vs-pulumi.md).
- **Registry** — GHCR stays the no-Azure artifact; ACR is the Azure runtime registry (see the [CD contract](../cd-contract.md)).
- **When Azure is mutated** — lab default is `workflow_dispatch`, not auto-deploy on every `main` merge (cost). Documented in the contract, not here.
