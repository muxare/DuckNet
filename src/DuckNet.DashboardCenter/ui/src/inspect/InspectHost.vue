<script setup lang="ts">
import { onMounted, onUnmounted } from "vue";
import { isCenterId } from "../hash";
import { centerById } from "../system-map";
import { useLiveCenters } from "../useLive";
import { getTerm } from "./corpus";
import { liveFacts } from "./live";
import TermBody from "./TermBody.vue";
import { isPinHotkey, useInspect } from "./useInspect";
import type { PinFrame } from "./types";

const { preview, pins, pin, pop, close, hold, leave } = useInspect();
const { snapshot } = useLiveCenters();

const CARD_WIDTH = 352;

function cardStyle(frame: PinFrame, interactive: boolean): Record<string, string> {
  const maxX = Math.max(12, window.innerWidth - CARD_WIDTH - 12);
  const x = Math.min(Math.max(12, frame.x), maxX);
  const y = Math.min(Math.max(12, frame.y), Math.max(12, window.innerHeight - 220));
  return {
    left: `${x}px`,
    top: `${y}px`,
    pointerEvents: interactive ? "auto" : "none",
  };
}

function termOf(frame: PinFrame) {
  return getTerm(frame.termId);
}

function strip(frame: PinFrame) {
  const term = termOf(frame);
  if (!term?.live) {
    return { facts: [], hint: undefined };
  }
  return liveFacts(term.live, frame.context, snapshot.value);
}

function codeOf(frame: PinFrame) {
  const term = termOf(frame);
  if (term?.code?.length) {
    return term.code;
  }
  const id = frame.context.centerId ?? (isCenterId(frame.termId) ? frame.termId : undefined);
  if (id) {
    return centerById[id].code;
  }
  return [];
}

function onKey(event: KeyboardEvent) {
  if (event.key === "Escape") {
    pop();
    return;
  }
  if (!isPinHotkey(event) || !preview.value) {
    return;
  }
  event.preventDefault();
  event.stopPropagation();
  pin();
}

onMounted(() => {
  window.addEventListener("keydown", onKey, true);
});

onUnmounted(() => {
  window.removeEventListener("keydown", onKey, true);
});
</script>

<template>
  <Teleport to="body">
    <div class="dn-inspect-layer">
    <div
      v-for="(frame, index) in pins"
      :key="`${frame.termId}-${index}`"
      class="dn-inspect dn-inspect-pin"
      :style="cardStyle(frame, true)"
    >
      <button class="dn-inspect-x" type="button" aria-label="Close" @click="close(index)">×</button>
      <div class="dn-inspect-kicker">{{ termOf(frame)?.kind }} · Esc close</div>
      <div class="dn-inspect-title">{{ termOf(frame)?.title }}</div>
      <div v-if="strip(frame).facts.length" class="dn-inspect-live">
        <span v-for="fact in strip(frame).facts" :key="fact.label">
          {{ fact.label }} {{ fact.value }}
        </span>
      </div>
      <p v-else-if="strip(frame).hint" class="dn-inspect-hint">{{ strip(frame).hint }}</p>
      <p class="dn-inspect-summary">{{ termOf(frame)?.summary }}</p>
      <TermBody v-if="termOf(frame)" :body="termOf(frame)!.body" :scope="frame.context.centerId" />
      <ul v-if="codeOf(frame).length" class="dn-inspect-code">
        <li v-for="link in codeOf(frame)" :key="link.path">
          <code>{{ link.path }}</code>
          <span>{{ link.why }}</span>
        </li>
      </ul>
    </div>
    <div
      v-if="preview && termOf(preview)"
      class="dn-inspect dn-inspect-preview"
      :style="cardStyle(preview, false)"
    >
      <div class="dn-inspect-kicker-row">
        <button
          class="dn-inspect-pin-btn"
          type="button"
          title="Enter to pin"
          @mouseenter="hold"
          @mouseleave="leave()"
          @click="pin"
        >
          Pin
        </button>
        <div class="dn-inspect-kicker">{{ termOf(preview)!.kind }} · Enter to pin</div>
      </div>
      <div class="dn-inspect-title">{{ termOf(preview)!.title }}</div>
      <div v-if="strip(preview).facts.length" class="dn-inspect-live">
        <span v-for="fact in strip(preview).facts" :key="fact.label">
          {{ fact.label }} {{ fact.value }}
        </span>
      </div>
      <p class="dn-inspect-summary mb-0">{{ termOf(preview)!.summary }}</p>
    </div>
    </div>
  </Teleport>
</template>
