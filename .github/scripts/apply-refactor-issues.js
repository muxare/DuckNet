"use strict";

const fs = require("fs");

const STICKY_MARKER = "<!-- ducknet-refactor-scan -->";

function subst(body, map) {
  return String(body || "").replace(/\{\{ref:([^}]+)\}\}/g, (match, key) => {
    const n = map[key];
    return n ? `#${n}` : match;
  });
}

async function ensureLabel(github, owner, repo, name) {
  try {
    await github.rest.issues.getLabel({ owner, repo, name });
  } catch (err) {
    if (err.status !== 404) {
      throw err;
    }
    const color = name === "refactor-scan" ? "0e8a16" : "5319e7";
    await github.rest.issues.createLabel({
      owner,
      repo,
      name,
      color,
      description:
        name === "refactor-scan"
          ? "Opened or updated by the weekly refactor scan"
          : "Refactoring work",
    });
  }
}

async function closeSticky(github, context, core, issues) {
  const { owner, repo } = context.repo;
  const sticky = issues.find((i) => (i.body || "").includes(STICKY_MARKER));
  if (!sticky || sticky.state !== "open") {
    return;
  }
  await github.rest.issues.createComment({
    owner,
    repo,
    issue_number: sticky.number,
    body:
      "Replaced by per-task issues from the refactor scan " +
      "(one GitHub issue per held finding / `proposed_issues` item). " +
      "This rollup is closed so it is not updated in place.",
  });
  await github.rest.issues.update({
    owner,
    repo,
    issue_number: sticky.number,
    state: "closed",
    state_reason: "not_planned",
  });
  core.info(`Closed sticky refactor-scan rollup #${sticky.number}`);
}

async function apply({ github, context, core }) {
  const { owner, repo } = context.repo;
  if (!fs.existsSync("refactor-actions.json")) {
    core.setFailed("refactor-actions.json was not produced");
    return;
  }
  const plan = JSON.parse(fs.readFileSync("refactor-actions.json", "utf8"));
  const actions = plan.actions || [];
  const existing = fs.existsSync("existing-issues.json")
    ? JSON.parse(fs.readFileSync("existing-issues.json", "utf8"))
    : [];

  const labelNames = new Set();
  for (const action of actions) {
    for (const label of action.labels || []) {
      labelNames.add(label);
    }
  }
  for (const name of labelNames) {
    await ensureLabel(github, owner, repo, name);
  }

  const map = {};
  for (const action of actions) {
    if (action.number) {
      map[action.key] = action.number;
    }
  }

  for (const action of actions) {
    if (action.action === "skip") {
      core.info(`Skip ${action.key} (closed #${action.number})`);
      continue;
    }
    if (action.action !== "create") {
      continue;
    }
    const created = await github.rest.issues.create({
      owner,
      repo,
      title: action.title,
      body: subst(action.body, map),
      labels: action.labels || [],
    });
    map[action.key] = created.data.number;
    core.info(`Created #${created.data.number} (${action.key})`);
  }

  for (const action of actions) {
    if (action.action !== "update") {
      continue;
    }
    await github.rest.issues.update({
      owner,
      repo,
      issue_number: action.number,
      body: subst(action.body, map),
    });
    if ((action.labels || []).length) {
      await github.rest.issues.addLabels({
        owner,
        repo,
        issue_number: action.number,
        labels: action.labels,
      });
    }
    core.info(`Updated #${action.number} (${action.key}, ${action.reason})`);
  }

  const changed = actions.some(
    (a) => a.action === "create" || a.action === "update"
  );
  if (changed) {
    await closeSticky(github, context, core, existing);
  }
}

module.exports = apply;
