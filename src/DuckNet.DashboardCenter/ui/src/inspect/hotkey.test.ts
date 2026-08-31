import { describe, expect, it } from "vitest";
import { isPinHotkey, type PinHotkeyEvent } from "./useInspect";

function key(init: PinHotkeyEvent): PinHotkeyEvent {
  return init;
}

describe("isPinHotkey", () => {
  it("accepts unmodified Enter", () => {
    expect(isPinHotkey(key({ key: "Enter" }))).toBe(true);
  });

  it("rejects Ctrl/Meta/Alt Enter so the browser keeps new-tab", () => {
    expect(isPinHotkey(key({ key: "Enter", ctrlKey: true }))).toBe(false);
    expect(isPinHotkey(key({ key: "Enter", metaKey: true }))).toBe(false);
    expect(isPinHotkey(key({ key: "Enter", altKey: true }))).toBe(false);
  });

  it("rejects T and other letters", () => {
    expect(isPinHotkey(key({ key: "t" }))).toBe(false);
    expect(isPinHotkey(key({ key: "T" }))).toBe(false);
  });

  it("rejects repeat and composing Enter", () => {
    expect(isPinHotkey(key({ key: "Enter", repeat: true }))).toBe(false);
    expect(isPinHotkey(key({ key: "Enter", isComposing: true }))).toBe(false);
  });
});
