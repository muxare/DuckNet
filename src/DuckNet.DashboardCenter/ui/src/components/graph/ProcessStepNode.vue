<script setup lang="ts">
import { Handle, Position, type NodeProps } from "@vue-flow/core";

type Data = {
  label: string;
  note?: string;
};

defineProps<NodeProps<Data>>();
</script>

<template>
  <div class="dn-step">
    <Handle id="top" type="target" :position="Position.Top" />
    <Handle id="bottom" type="source" :position="Position.Bottom" />
    <Handle id="left" type="target" :position="Position.Left" />
    <Handle id="right" type="source" :position="Position.Right" />
    <div class="dn-step-label">{{ data.label }}</div>
    <div v-if="data.note" class="dn-step-note">{{ data.note }}</div>
  </div>
</template>
