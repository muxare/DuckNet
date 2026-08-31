<script setup lang="ts">
import type { CenterId } from "../system-map";
import type { Zoom } from "../hash";
import { useLiveCenters } from "../useLive";
import InspectHost from "../inspect/InspectHost.vue";
import AllDetailMap from "./AllDetailMap.vue";
import CenterFlow from "./CenterFlow.vue";
import OverviewMap from "./OverviewMap.vue";

defineProps<{
  zoom: Zoom;
}>();

const emit = defineEmits<{
  zoom: [zoom: Zoom];
}>();

const { snapshot } = useLiveCenters();

function open(id: CenterId) {
  emit("zoom", { kind: "center", id });
}
</script>

<template>
  <main class="container-fluid px-4 pb-5">
    <p class="text-secondary small mb-2">Hover · Enter pin · Esc close</p>
    <OverviewMap
      v-if="zoom.kind === 'overview'"
      :snap="snapshot"
      @open="open"
      @all="emit('zoom', { kind: 'all' })"
    />
    <AllDetailMap
      v-else-if="zoom.kind === 'all'"
      :snap="snapshot"
      @back="emit('zoom', { kind: 'overview' })"
      @open="open"
    />
    <CenterFlow
      v-else
      :center-id="zoom.id"
      :snap="snapshot"
      @back="emit('zoom', { kind: 'overview' })"
    />
    <InspectHost />
  </main>
</template>
