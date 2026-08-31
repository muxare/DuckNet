import { describe, expect, it } from "vitest";
import { mapTermIds } from "../system-map";
import { allTermIds, terms } from "./corpus";
import { livePanelTermIds } from "./live";
import { unknownWikiIds } from "./wiki";

describe("corpus", () => {
  const known = new Set(allTermIds());

  it("every system-map term exists", () => {
    const missing = mapTermIds().filter((id) => !known.has(id));
    expect(missing).toEqual([]);
  });

  it("every live-panel term exists", () => {
    const missing = livePanelTermIds.filter((id) => !known.has(id));
    expect(missing).toEqual([]);
  });

  it("every [[id]] in bodies resolves", () => {
    const dangling: string[] = [];
    for (const term of Object.values(terms)) {
      for (const id of unknownWikiIds(term.body, known)) {
        dangling.push(`${term.id} → ${id}`);
      }
    }
    expect(dangling).toEqual([]);
  });
});
