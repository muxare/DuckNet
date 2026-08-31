import { computed, ref } from "vue";
import { getTerm } from "./corpus";
import {
  clearPreview,
  closePin,
  emptyStack,
  pinPreview,
  popPin,
  showPreview,
} from "./stack";
import type { InspectContext, InspectStackState, PinFrame } from "./types";

const HOVER_MS = 300;
const LEAVE_MS = 350;

const state = ref<InspectStackState>(emptyStack());
let timer: ReturnType<typeof setTimeout> | null = null;
let pending: PinFrame | null = null;

function apply(next: InspectStackState) {
  state.value = next;
}

function clearTimer() {
  if (timer !== null) {
    clearTimeout(timer);
    timer = null;
  }
}

export function useInspect() {
  function hover(termId: string, x: number, y: number, context: InspectContext = {}) {
    if (!getTerm(termId)) {
      console.warn(`inspect: unknown term "${termId}"`);
      return;
    }
    pending = { termId, x, y, context };
    clearTimer();
    timer = setTimeout(() => {
      if (pending) {
        apply(showPreview(state.value, pending));
      }
    }, HOVER_MS);
  }

  function leave(termId?: string) {
    clearTimer();
    pending = null;
    const preview = state.value.preview;
    if (!preview || (termId !== undefined && preview.termId !== termId)) {
      return;
    }
    timer = setTimeout(() => {
      const current = state.value.preview;
      if (current && (termId === undefined || current.termId === termId)) {
        apply(clearPreview(state.value));
      }
      timer = null;
    }, LEAVE_MS);
  }

  function hold() {
    clearTimer();
  }

  function pin() {
    clearTimer();
    let next = state.value;
    if (!next.preview && pending) {
      next = showPreview(next, pending);
    }
    pending = null;
    apply(pinPreview(next));
  }

  function pop() {
    apply(popPin(state.value));
  }

  function close(index: number) {
    apply(closePin(state.value, index));
  }

  return {
    preview: computed(() => state.value.preview),
    pins: computed(() => state.value.pins),
    hover,
    leave,
    hold,
    pin,
    pop,
    close,
  };
}

export function isTypingTarget(el: EventTarget | null): boolean {
  if (typeof HTMLElement === "undefined" || !(el instanceof HTMLElement)) {
    return false;
  }
  const tag = el.tagName;
  return tag === "INPUT" || tag === "TEXTAREA" || el.isContentEditable;
}

export type PinHotkeyEvent = {
  key: string;
  repeat?: boolean;
  isComposing?: boolean;
  metaKey?: boolean;
  ctrlKey?: boolean;
  altKey?: boolean;
  target?: EventTarget | null;
};

export function isPinHotkey(event: PinHotkeyEvent): boolean {
  if (event.isComposing || event.repeat) {
    return false;
  }
  if (event.metaKey || event.ctrlKey || event.altKey) {
    return false;
  }
  if (event.key !== "Enter") {
    return false;
  }
  if (isTypingTarget(event.target ?? null)) {
    return false;
  }
  if (typeof document !== "undefined" && isTypingTarget(document.activeElement)) {
    return false;
  }
  return true;
}
