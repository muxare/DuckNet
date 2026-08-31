<script setup lang="ts">
import { onMounted, onUnmounted, ref } from "vue";
import { hashFor, parseHash, type AppView, type Zoom } from "./hash";
import DeveloperView from "./components/DeveloperView.vue";
import ReadModelView from "./components/ReadModelView.vue";

const view = ref<AppView>("developer");
const zoom = ref<Zoom>({ kind: "overview" });

function applyHash() {
  const parsed = parseHash(window.location.hash);
  view.value = parsed.view;
  zoom.value = parsed.zoom;
}

function go(next: AppView, nextZoom: Zoom = { kind: "overview" }) {
  view.value = next;
  zoom.value = next === "developer" ? nextZoom : { kind: "overview" };
  const hash = hashFor(view.value, zoom.value);
  if (window.location.hash !== hash) {
    window.location.hash = hash;
  }
}

onMounted(() => {
  if (!window.location.hash) {
    window.location.hash = "#developer";
  }
  applyHash();
  window.addEventListener("hashchange", applyHash);
});

onUnmounted(() => {
  window.removeEventListener("hashchange", applyHash);
});
</script>

<template>
  <nav class="navbar navbar-duck navbar-dark mb-4">
    <div class="container-fluid px-4">
      <span class="navbar-brand mb-0">
        <i class="bi bi-soundwave me-2"></i>DuckNet
        <span class="fw-normal text-white-50 fs-6 ms-1">
          {{ view === "developer" ? "developer" : "read model" }}
        </span>
      </span>
      <div class="btn-group btn-group-sm" role="group" aria-label="View">
        <button
          type="button"
          class="btn"
          :class="view === 'developer' ? 'btn-warning' : 'btn-outline-warning'"
          @click="go('developer', zoom)"
        >
          Developer
        </button>
        <button
          type="button"
          class="btn"
          :class="view === 'read-model' ? 'btn-warning' : 'btn-outline-warning'"
          @click="go('read-model')"
        >
          Read model
        </button>
      </div>
    </div>
  </nav>

  <DeveloperView v-if="view === 'developer'" :zoom="zoom" @zoom="go('developer', $event)" />
  <ReadModelView v-else />
</template>
