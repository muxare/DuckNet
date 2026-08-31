<script setup lang="ts">
import { Handle, Position } from "@vue-flow/core";
</script>

<template>
  <Handle id="top" type="target" :position="Position.Top" />
  <Handle id="bottom" type="source" :position="Position.Bottom" />
  <Handle id="left" type="source" :position="Position.Left" />
  <Handle id="left-t" type="target" :position="Position.Left" />
  <Handle id="right" type="source" :position="Position.Right" />
  <Handle id="right-t" type="target" :position="Position.Right" />
</template>
