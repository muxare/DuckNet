import type { InspectStackState, PinFrame } from "./types";

export const PIN_OFFSET = 24;

export function emptyStack(): InspectStackState {
  return { preview: null, pins: [] };
}

export function showPreview(state: InspectStackState, frame: PinFrame): InspectStackState {
  return { ...state, preview: frame };
}

export function clearPreview(state: InspectStackState): InspectStackState {
  return { ...state, preview: null };
}

export function pinPreview(state: InspectStackState): InspectStackState {
  if (!state.preview) {
    return state;
  }
  const n = state.pins.length;
  const pinned: PinFrame = {
    ...state.preview,
    x: state.preview.x + PIN_OFFSET * n,
    y: state.preview.y + PIN_OFFSET * n,
  };
  return { preview: null, pins: [...state.pins, pinned] };
}

export function popPin(state: InspectStackState): InspectStackState {
  if (state.preview) {
    return { ...state, preview: null };
  }
  if (state.pins.length === 0) {
    return state;
  }
  return { ...state, pins: state.pins.slice(0, -1) };
}

export function closePin(state: InspectStackState, index: number): InspectStackState {
  return { ...state, pins: state.pins.filter((_, i) => i !== index) };
}
