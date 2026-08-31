<script setup lang="ts">
import { ref, watch } from "vue";
import { VueFlow, type Edge, type EdgeMouseEvent, type Node, type NodeMouseEvent } from "@vue-flow/core";
import { Background } from "@vue-flow/background";
import { Controls } from "@vue-flow/controls";
import { useInspect } from "../../inspect/useInspect";
import type { CenterId } from "../../system-map";
import CenterCircleNode from "./CenterCircleNode.vue";
import DecisionNode from "./DecisionNode.vue";
import DropNode from "./DropNode.vue";
import GroupNode from "./GroupNode.vue";
import ObjectBoxNode from "./ObjectBoxNode.vue";
import PortNode from "./PortNode.vue";
import ProcessStepNode from "./ProcessStepNode.vue";
import StoreNode from "./StoreNode.vue";

type InspectData = {
  termId?: string;
  centerId?: CenterId;
};

const props = defineProps<{
  nodes: Node[];
  edges: Edge[];
}>();

const emit = defineEmits<{
  open: [id: string];
}>();

const inspect = useInspect();
const localNodes = ref<Node[]>(props.nodes);
const localEdges = ref<Edge[]>(props.edges);

watch(
  () => props.nodes,
  (value) => {
    localNodes.value = value;
  },
);

watch(
  () => props.edges,
  (value) => {
    localEdges.value = value;
  },
);

function inspectOf(data: unknown): InspectData | null {
  const d = data as InspectData | undefined;
  if (!d?.termId) {
    return null;
  }
  return d;
}

function onNodeClick({ node }: NodeMouseEvent) {
  const data = node.data as { centerId?: string } | undefined;
  if (node.type === "center" || node.type === "port" || node.type === "group") {
    emit("open", data?.centerId ?? node.id.replace(/^g-/, ""));
    return;
  }
  if (node.parentNode && data?.centerId) {
    emit("open", data.centerId);
  }
}

function onNodeEnter({ event, node }: NodeMouseEvent) {
  const data = inspectOf(node.data);
  if (!data?.termId) {
    return;
  }
  inspect.hover(data.termId, event.clientX + 16, event.clientY + 16, { centerId: data.centerId });
}

function onNodeLeave({ node }: NodeMouseEvent) {
  const data = inspectOf(node.data);
  if (data?.termId) {
    inspect.leave(data.termId);
  }
}

function onEdgeEnter({ event, edge }: EdgeMouseEvent) {
  const data = inspectOf(edge.data);
  if (!data?.termId) {
    return;
  }
  inspect.hover(data.termId, event.clientX + 16, event.clientY + 16, {});
}

function onEdgeLeave({ edge }: EdgeMouseEvent) {
  const data = inspectOf(edge.data);
  if (data?.termId) {
    inspect.leave(data.termId);
  }
}
</script>

<template>
  <VueFlow
    v-model:nodes="localNodes"
    v-model:edges="localEdges"
    :nodes-draggable="false"
    :nodes-connectable="false"
    :elements-selectable="true"
    :select-nodes-on-drag="false"
    :min-zoom="0.35"
    :max-zoom="1.6"
    :fit-view-on-init="true"
    :pan-on-scroll="true"
    @node-click="onNodeClick"
    @node-mouse-enter="onNodeEnter"
    @node-mouse-leave="onNodeLeave"
    @edge-mouse-enter="onEdgeEnter"
    @edge-mouse-leave="onEdgeLeave"
  >
    <Background variant="dots" :gap="18" :size="1" pattern-color="#d6d0c4" />
    <Controls />
    <template #node-center="p">
      <CenterCircleNode v-bind="p" />
    </template>
    <template #node-port="p">
      <PortNode v-bind="p" />
    </template>
    <template #node-step="p">
      <ProcessStepNode v-bind="p" />
    </template>
    <template #node-decision="p">
      <DecisionNode v-bind="p" />
    </template>
    <template #node-store="p">
      <StoreNode v-bind="p" />
    </template>
    <template #node-drop="p">
      <DropNode v-bind="p" />
    </template>
    <template #node-object="p">
      <ObjectBoxNode v-bind="p" />
    </template>
    <template #node-group="p">
      <GroupNode v-bind="p" />
    </template>
  </VueFlow>
</template>
