<script setup lang="ts">
import { Handle, Position, type NodeProps } from "@vue-flow/core";

type Data = {
  label: string;
  note?: string;
};

defineProps<NodeProps<Data>>();
</script>

<template>
  <div class="dn-decision">
    <Handle id="top" type="target" :position="Position.Top" />
    <Handle id="yes" type="source" :position="Position.Right" />
    <Handle id="no" type="source" :position="Position.Bottom" />
    <div class="dn-decision-inner">
      <div class="dn-step-label">{{ data.label }}</div>
    </div>
  </div>
</template>
