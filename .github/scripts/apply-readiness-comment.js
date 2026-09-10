"use strict";

// Post the readiness verdict as one sticky comment per issue, edited in place.
// No Claude. A new comment on every edit would turn a busy issue into a wall of
// bot noise, so there is exactly one, and it is rewritten.

const fs = require("fs");

const STICKY = "<!-- ducknet-readiness -->";

async function apply({ github, context, core }) {
  const { owner, repo } = context.repo;
  const number = context.payload.issue && context.payload.issue.number;
  if (!number) {
    core.setFailed("no issue number in the event payload");
    return;
  }
  if (!fs.existsSync("readiness-comment.md")) {
    core.setFailed("readiness-comment.md was not produced");
    return;
  }
  const body = fs.readFileSync("readiness-comment.md", "utf8");
  if (!body.includes(STICKY)) {
    core.setFailed(`readiness-comment.md is missing the sticky marker ${STICKY}`);
    return;
  }

  const comments = await github.paginate(github.rest.issues.listComments, {
    owner,
    repo,
    issue_number: number,
    per_page: 100,
  });
  const sticky = comments.find((c) => (c.body || "").includes(STICKY));

  if (sticky) {
    await github.rest.issues.updateComment({
      owner,
      repo,
      comment_id: sticky.id,
      body,
    });
    core.info(`Updated readiness comment on #${number}`);
    return;
  }

  await github.rest.issues.createComment({
    owner,
    repo,
    issue_number: number,
    body,
  });
  core.info(`Posted readiness comment on #${number}`);
}

module.exports = apply;
