<script setup lang="ts">
import type { NodeProps } from "@vue-flow/core";

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
  <div class="dn-group" :style="{ '--accent': data.accent }" :aria-label="data.title">
    <div class="dn-group-kicker">{{ data.kicker }}</div>
    <div class="dn-group-title">{{ data.title }}</div>
    <div v-if="data.error" class="dn-circle-error">{{ data.error }}</div>
    <div v-else class="dn-group-lines">
      <span v-for="line in data.lines ?? []" :key="line">{{ line }}</span>
    </div>
  </div>
</template>
