<script setup lang="ts">
import { ref, watch } from "vue";
import { isCenterId } from "../hash";
import { overviewCaption, type CenterId } from "../system-map";
import type { LiveSnapshot } from "../useLive";
import FlowCanvas from "./graph/FlowCanvas.vue";
import { metricsFor } from "./graph/metrics";
import { overviewEdges, overviewNodes } from "./graph/to-flow";

const props = defineProps<{
  snap: LiveSnapshot;
}>();

const emit = defineEmits<{
  open: [id: CenterId];
  all: [];
}>();

const nodes = ref(overviewNodes());
const edges = overviewEdges();

watch(
  () => props.snap,
  (snap) => {
    nodes.value = nodes.value.map((node) => {
      const metrics = metricsFor(node.id as CenterId, snap);
      return {
        ...node,
        data: { ...node.data, lines: metrics.lines, error: metrics.error },
      };
    });
  },
  { deep: true, immediate: true },
);

function onOpen(id: string) {
  if (isCenterId(id)) {
    emit("open", id);
  }
}
</script>

<template>
  <div>
    <div class="d-flex flex-wrap justify-content-between align-items-start gap-2 mb-3">
      <p class="text-secondary small mb-0" style="max-width: 46rem">
        {{ overviewCaption }}
      </p>
      <button class="btn btn-sm btn-outline-dark" type="button" @click="emit('all')">
        All detail
      </button>
    </div>
    <div class="dn-flow-shell dn-flow-shell-overview">
      <FlowCanvas :nodes="nodes" :edges="edges" @open="onOpen" />
    </div>
  </div>
</template>
