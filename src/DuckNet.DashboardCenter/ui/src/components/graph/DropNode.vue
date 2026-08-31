<script setup lang="ts">
import { Handle, Position, type NodeProps } from "@vue-flow/core";

type Data = {
  label: string;
};

defineProps<NodeProps<Data>>();
</script>

<template>
  <div class="dn-drop">
    <Handle id="top" type="target" :position="Position.Top" />
    {{ data.label }}
  </div>
</template>
