import { describe, expect, it } from "vitest";
import { emptyStack, pinPreview, popPin, showPreview } from "./stack";
import type { PinFrame } from "./types";

function frame(termId: string, x = 10, y = 20): PinFrame {
  return { termId, x, y, context: { centerId: "alarm" } };
}

describe("inspect stack", () => {
  it("hover then pin moves preview onto the stack", () => {
    let state = emptyStack();
    state = showPreview(state, frame("inbox"));
    state = pinPreview(state);
    expect(state.preview).toBeNull();
    expect(state.pins).toHaveLength(1);
    expect(state.pins[0].termId).toBe("inbox");
    expect(state.pins[0].x).toBe(10);
    expect(state.pins[0].y).toBe(20);
  });

  it("second pin stacks with offset", () => {
    let state = emptyStack();
    state = showPreview(state, frame("inbox"));
    state = pinPreview(state);
    state = showPreview(state, frame("event-id", 40, 50));
    state = pinPreview(state);
    expect(state.pins).toHaveLength(2);
    expect(state.pins[1].termId).toBe("event-id");
    expect(state.pins[1].x).toBe(40 + 24);
    expect(state.pins[1].y).toBe(50 + 24);
  });

  it("esc pops preview first, then the top pin", () => {
    let state = emptyStack();
    state = showPreview(state, frame("inbox"));
    state = pinPreview(state);
    state = showPreview(state, frame("dlq"));
    state = popPin(state);
    expect(state.preview).toBeNull();
    expect(state.pins).toHaveLength(1);
    state = popPin(state);
    expect(state.pins).toHaveLength(0);
  });

  it("pin is a no-op without a preview", () => {
    const state = emptyStack();
    expect(pinPreview(state)).toEqual(state);
  });
});
