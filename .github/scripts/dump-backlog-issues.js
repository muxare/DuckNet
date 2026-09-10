"use strict";

// Dump the issues the backlog planner matches against. No Claude.
//
// Shared by backlog-build (the dump the plan was resolved against) and
// backlog-apply (the dump it is re-resolved against at apply time). The two
// must produce the same shape from the same repository state: apply replays the
// planner over the build's dump to prove nothing frozen moved, and a field that
// appeared or vanished between the two would look exactly like tampering.
// One function, one shape, both callers.
//
// Open issues plus closed ones this machinery filed: a closed match means a
// human decided, and the planner skips it rather than reopening the argument.

const fs = require("fs");

async function dumpIssues({ github, context, core }, out = "existing-issues.json") {
  const { owner, repo } = context.repo;
  const open = await github.paginate(github.rest.issues.listForRepo, {
    owner,
    repo,
    state: "open",
    per_page: 100,
  });
  const closed = await github.paginate(github.rest.issues.listForRepo, {
    owner,
    repo,
    state: "closed",
    labels: "backlog-build",
    per_page: 100,
  });

  const seen = new Set();
  const issues = [];
  for (const issue of [...open, ...closed]) {
    if (issue.pull_request || seen.has(issue.number)) continue;
    seen.add(issue.number);
    issues.push({
      number: issue.number,
      title: issue.title,
      body: issue.body || "",
      state: issue.state,
      labels: (issue.labels || []).map((l) => (typeof l === "string" ? l : l.name)),
    });
  }

  // Sort by number: pagination order is not a contract, and the planner's
  // first-match-wins indexing would otherwise be at the mercy of it.
  issues.sort((a, b) => a.number - b.number);

  fs.writeFileSync(out, JSON.stringify(issues, null, 2));
  core.info(`Dumped ${issues.length} issues for matching -> ${out}`);
  return issues;
}

module.exports = dumpIssues;
