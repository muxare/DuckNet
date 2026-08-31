<script setup lang="ts">
import type { NodeProps } from "@vue-flow/core";
import SideHandles from "./SideHandles.vue";

type Data = {
  title: string;
  kicker: string;
  accent: string;
  lines?: string[];
  error?: string;
};

defineProps<NodeProps<Data>>();
</script>

<template>
  <div class="dn-circle" :style="{ '--accent': data.accent }" :aria-label="data.title">
    <SideHandles />
    <div class="dn-circle-kicker">{{ data.kicker }}</div>
    <div class="dn-circle-title">{{ data.title }}</div>
    <div v-if="data.error" class="dn-circle-error">{{ data.error }}</div>
    <div v-else class="dn-circle-lines">
      <div v-for="line in data.lines ?? []" :key="line">{{ line }}</div>
    </div>
  </div>
</template>
