<script setup lang="ts">
import type { CenterId } from "../system-map";
import InspectTerm from "./InspectTerm.vue";
import { paragraphs, parseWiki } from "./wiki";

const props = defineProps<{
  body: string;
  scope?: CenterId;
}>();

function tokens(paragraph: string) {
  return parseWiki(paragraph);
}
</script>

<template>
  <p v-for="(para, p) in paragraphs(props.body)" :key="p" class="dn-inspect-body mb-2">
    <template v-for="(token, i) in tokens(para)" :key="i">
      <InspectTerm v-if="token.type === 'link'" :id="token.id" :scope="scope">{{ token.label }}</InspectTerm>
      <template v-else>{{ token.value }}</template>
    </template>
  </p>
</template>
