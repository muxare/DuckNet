<script setup lang="ts">
import { ref, watch } from "vue";
import { isCenterId } from "../hash";
import { overviewCaption, type CenterId } from "../system-map";
import type { LiveSnapshot } from "../useLive";
import FlowCanvas from "./graph/FlowCanvas.vue";
import { metricsFor } from "./graph/metrics";
import { allDetailFlow } from "./graph/to-flow";

const props = defineProps<{
  snap: LiveSnapshot;
}>();

const emit = defineEmits<{
  back: [];
  open: [id: CenterId];
}>();

const { nodes: initialNodes, edges } = allDetailFlow();
const nodes = ref(initialNodes);

watch(
  () => props.snap,
  (snap) => {
    nodes.value = nodes.value.map((node) => {
      if (node.type !== "group") {
        return node;
      }
      const centerId = (node.data as { centerId: CenterId }).centerId;
      const metrics = metricsFor(centerId, snap);
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
        Messy on purpose — this is the whole running system. {{ overviewCaption }}
      </p>
      <button class="btn btn-sm btn-outline-secondary" type="button" @click="emit('back')">
        Back to map
      </button>
    </div>
    <div class="dn-flow-shell dn-flow-shell-all">
      <FlowCanvas :nodes="nodes" :edges="edges" @open="onOpen" />
    </div>
  </div>
</template>
