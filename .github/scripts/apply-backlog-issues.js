"use strict";

// Apply a backlog-build action plan to GitHub issues. No Claude.
//
// Epic and story bodies reference each other with {{ref:KEY}}. A single pass
// cannot resolve both directions — an epic listing its stories is created
// before they exist — so this runs create first, then rewrites every body it
// wrote with the complete key -> number map. A reference that still cannot be
// resolved is left as literal text rather than silently dropped: a dangling
// {{ref:...}} in an issue is visible, a vanished dependency is not.

const fs = require("fs");

const LABELS = {
  backlog: { color: "1d76db", description: "Backlog item" },
  "backlog-build": {
    color: "0e8a16",
    description: "Opened or updated by the backlog build chain",
  },
  epic: { color: "5319e7", description: "Parent issue grouping stories" },
};

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
    const spec = LABELS[name] || { color: "ededed", description: "" };
    await github.rest.issues.createLabel({
      owner,
      repo,
      name,
      color: spec.color,
      description: spec.description,
    });
  }
}

async function apply({ github, context, core }) {
  const { owner, repo } = context.repo;
  if (!fs.existsSync("backlog-actions.json")) {
    core.setFailed("backlog-actions.json was not produced");
    return;
  }
  const plan = JSON.parse(fs.readFileSync("backlog-actions.json", "utf8"));
  const actions = plan.actions || [];

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

  const written = [];
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
    written.push({ action, number: created.data.number });
    core.info(`Created #${created.data.number} (${action.key})`);
  }

  for (const action of actions) {
    if (action.action !== "update") {
      continue;
    }
    written.push({ action, number: action.number });
  }

  // Second pass: every body, now that every key has a number.
  for (const { action, number } of written) {
    const body = subst(action.body, map);
    await github.rest.issues.update({ owner, repo, issue_number: number, body });
    if ((action.labels || []).length) {
      await github.rest.issues.addLabels({
        owner,
        repo,
        issue_number: number,
        labels: action.labels,
      });
    }
    if (action.action === "update") {
      core.info(`Updated #${number} (${action.key}, ${action.reason})`);
    }
    if (/\{\{ref:/.test(body)) {
      core.warning(`#${number} (${action.key}) still has an unresolved {{ref:...}}`);
    }
  }

  const s = plan.summary || {};
  core.notice(
    `backlog-build: ${s.create || 0} created, ${s.update || 0} updated, ` +
      `${s.skip || 0} skipped (closed), ${s.dropped || 0} dropped by the independent pass`
  );
}

module.exports = apply;
