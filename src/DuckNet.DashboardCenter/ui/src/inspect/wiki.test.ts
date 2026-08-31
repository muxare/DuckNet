import { describe, expect, it } from "vitest";
import { unknownWikiIds, parseWiki, wikiLinkIds } from "./wiki";

describe("parseWiki", () => {
  it("parses [[inbox]]", () => {
    expect(parseWiki("see [[inbox]] here")).toEqual([
      { type: "text", value: "see " },
      { type: "link", id: "inbox", label: "inbox" },
      { type: "text", value: " here" },
    ]);
  });

  it("parses aliased [[inbox|the inbox]]", () => {
    expect(parseWiki("[[inbox|the inbox]]")).toEqual([{ type: "link", id: "inbox", label: "the inbox" }]);
  });

  it("collects link ids", () => {
    expect(wikiLinkIds("[[inbox]] and [[bus|IEventBus]]")).toEqual(["inbox", "bus"]);
  });

  it("flags unknown ids", () => {
    expect(unknownWikiIds("[[inbox]] [[nope]]", new Set(["inbox"]))).toEqual(["nope"]);
  });
});
