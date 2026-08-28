def fallback_areas($p):
  if ($p | test("(?i)secret|auth|token")) or ($p | contains("/bus/")) then
    ["security"]
  elif ($p | test("^(src/|infra/|tests/|\\.github/workflows/)")) then
    ["architecture"]
  else
    ["other"]
  end;

def fallback_risk($areas):
  if ($areas | index("security")) then "high"
  elif ($areas | index("architecture")) then "medium"
  else "low"
  end;

def specialist_areas:
  map(select(. == "architecture" or . == "security"));

($claude.files // []) as $cfiles
| ($cfiles | map({key: .path, value: .}) | from_entries) as $cmap
| {
    pullRequest: $pr,
    files: [
      $git[]
      | . as $g
      | ($cmap[$g.path] // null) as $c
      | ($c.areas // fallback_areas($g.path)) as $areas
      | {
          path: $g.path,
          risk: ($c.risk // fallback_risk($areas)),
          areas: $areas
        }
    ],
    findings: []
  }
| . as $base
| (
    if $claude != null then
      $base + {
        risk: $claude.risk,
        requestedReviewers: (
          ($claude.requestedReviewers // [])
          | unique
          | specialist_areas
        ),
        skipped: ($claude.skipped // false)
      }
    else
      ($base.files | map(.areas[]) | unique | specialist_areas) as $rev
      | $base + {
          risk: {
            level: (
              if ($rev | length) == 0 then "low"
              elif ($rev | index("security")) then "high"
              else "medium"
              end
            ),
            reasons: ["Triage model output missing; used path-based fallback."]
          },
          requestedReviewers: $rev,
          skipped: (($rev | length) == 0)
        }
    end
  )
| if .skipped then .requestedReviewers = [] else . end
