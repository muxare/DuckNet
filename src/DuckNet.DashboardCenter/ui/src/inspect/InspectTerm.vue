<script setup lang="ts">
import type { CenterId } from "../system-map";
import { useInspect } from "./useInspect";

const props = defineProps<{
  id: string;
  scope?: CenterId;
}>();

const inspect = useInspect();

function enter(event: MouseEvent) {
  inspect.hover(props.id, event.clientX + 16, event.clientY + 16, { centerId: props.scope });
}

function leave() {
  inspect.leave(props.id);
}
</script>

<template>
  <span class="dn-term" @mouseenter="enter" @mouseleave="leave">
    <slot />
  </span>
</template>
