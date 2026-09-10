"use strict";

// Publish the backlog grooming report as one sticky issue, rewritten in place.
// No Claude. Nothing else on the board is touched: the report is advisory, and
// a workflow that silently relabels or closes issues is a workflow nobody
// trusts to run weekly.

const fs = require("fs");

const STICKY = "<!-- ducknet-backlog-groom -->";
const TITLE = "Backlog grooming report";
const LABELS = {
  backlog: { color: "1d76db", description: "Backlog item" },
  "backlog-groom": {
    color: "fbca04",
    description: "Opened or updated by the weekly backlog groom",
  },
};

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
  if (!fs.existsSync("groom-report.md")) {
    core.setFailed("groom-report.md was not produced");
    return;
  }
  const body = fs.readFileSync("groom-report.md", "utf8");
  if (!body.includes(STICKY)) {
    core.setFailed(`groom-report.md is missing the sticky marker ${STICKY}`);
    return;
  }

  for (const name of Object.keys(LABELS)) {
    await ensureLabel(github, owner, repo, name);
  }

  const open = await github.paginate(github.rest.issues.listForRepo, {
    owner,
    repo,
    state: "open",
    labels: "backlog-groom",
    per_page: 100,
  });
  const sticky = open.find(
    (i) => !i.pull_request && (i.body || "").includes(STICKY)
  );

  if (sticky) {
    await github.rest.issues.update({
      owner,
      repo,
      issue_number: sticky.number,
      title: TITLE,
      body,
    });
    core.notice(`Updated grooming report #${sticky.number}`);
    return;
  }

  const created = await github.rest.issues.create({
    owner,
    repo,
    title: TITLE,
    body,
    labels: Object.keys(LABELS),
  });
  core.notice(`Opened grooming report #${created.data.number}`);
}

module.exports = apply;
