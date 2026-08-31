<script setup lang="ts">
import { computed, ref } from "vue";
import { useMutation, useQueryClient } from "@tanstack/vue-query";
import { fileName, postCenter, rebuildDashboard } from "../api";
import InspectTerm from "../inspect/InspectTerm.vue";
import { centerById, type CenterId } from "../system-map";
import type { LiveSnapshot } from "../useLive";
import FlowCanvas from "./graph/FlowCanvas.vue";
import { centerObjectFlow, centerProcessFlow } from "./graph/to-flow";

const props = defineProps<{
  centerId: CenterId;
  snap: LiveSnapshot;
}>();

const emit = defineEmits<{
  back: [];
}>();

const meta = computed(() => centerById[props.centerId]);
const queryClient = useQueryClient();
const ingestDuck = ref("duck-1");
const mode = ref<"process" | "objects">("process");

const graph = computed(() =>
  mode.value === "process" ? centerProcessFlow(props.centerId) : centerObjectFlow(props.centerId),
);

function format(value: number | undefined): string {
  if (value === undefined) {
    return "—";
  }
  return value.toLocaleString();
}

function invalidateLive() {
  void queryClient.invalidateQueries({ queryKey: ["live"] });
}

const ingest = useMutation({
  mutationFn: () =>
    postCenter(props.snap.telemetryUrl, "/ingest/squeak", {
      duckId: ingestDuck.value,
      volumeDb: 60,
    }),
  onSuccess: invalidateLive,
});

const poison = useMutation({
  mutationFn: () => postCenter(props.snap.telemetryUrl, "/bus/poison"),
  onSuccess: invalidateLive,
});

const resolveAlarm = useMutation({
  mutationFn: (duckId: string) =>
    postCenter(props.snap.alarmUrl, `/alarms/${encodeURIComponent(duckId)}/resolve`),
  onSuccess: invalidateLive,
});

const rebuild = useMutation({
  mutationFn: rebuildDashboard,
  onSuccess: () => {
    invalidateLive();
    void queryClient.invalidateQueries({ queryKey: ["dashboard"] });
  },
});

const shards = computed(() => {
  if (props.centerId === "alarm") {
    return props.snap.alarm?.shards ?? [];
  }
  if (props.centerId === "dashboard") {
    return props.snap.dashboardMetrics?.shards ?? props.snap.dashboard?.shards ?? [];
  }
  if (props.centerId === "billing") {
    return props.snap.billing?.shards ?? [];
  }
  return [];
});

const dlq = computed(() => {
  if (props.centerId === "alarm") {
    return props.snap.alarmDlq;
  }
  if (props.centerId === "billing") {
    return props.snap.billingDlq;
  }
  return props.snap.dashboardDlq;
});
</script>

<template>
  <div>
    <div class="d-flex flex-wrap gap-2 align-items-center mb-3">
      <button class="btn btn-sm btn-outline-secondary" type="button" @click="emit('back')">
        Back to map
      </button>
      <div>
        <div class="map-kicker mb-0">{{ meta.role }}</div>
        <h1 class="h4 mb-0">{{ meta.title }}</h1>
      </div>
      <span class="badge text-bg-light border">owns {{ meta.owns }}</span>
      <div class="btn-group btn-group-sm ms-auto" role="group" aria-label="Diagram">
        <button
          type="button"
          class="btn"
          :class="mode === 'process' ? 'btn-dark' : 'btn-outline-dark'"
          @click="mode = 'process'"
        >
          Process
        </button>
        <button
          type="button"
          class="btn"
          :class="mode === 'objects' ? 'btn-dark' : 'btn-outline-dark'"
          @click="mode = 'objects'"
        >
          Objects
        </button>
      </div>
    </div>

    <div class="row g-4">
      <div class="col-lg-6">
        <div class="card border-0 shadow-sm h-100">
          <div class="card-header bg-white">
            <strong>{{ mode === "process" ? "Process" : "Object graph" }}</strong>
          </div>
          <div class="card-body p-0">
            <div class="dn-flow-shell dn-flow-shell-center">
              <FlowCanvas :key="`${centerId}-${mode}`" :nodes="graph.nodes" :edges="graph.edges" />
            </div>
            <p v-if="meta.failure" class="text-secondary small mb-0 px-3 py-2">{{ meta.failure }}</p>
          </div>
        </div>
      </div>
      <div class="col-lg-6">
        <div class="card border-0 shadow-sm mb-3">
          <div class="card-header bg-white"><strong>Live</strong></div>
          <div class="card-body">
            <template v-if="centerId === 'telemetry'">
              <div class="map-metrics mb-3">
                <InspectTerm id="log-count" scope="telemetry">log {{ format(snap.telemetry?.logCount) }}</InspectTerm>
                <InspectTerm id="last-offset" scope="telemetry">offset {{ format(snap.telemetry?.lastOffset) }}</InspectTerm>
                <span>{{ fileName(snap.telemetry?.database) }}</span>
              </div>
              <div class="d-flex flex-wrap gap-2 align-items-center">
                <input v-model="ingestDuck" class="form-control form-control-sm" style="max-width: 10rem" />
                <button class="btn btn-sm btn-warning" type="button" :disabled="ingest.isPending.value" @click="ingest.mutate()">
                  Ingest squeak
                </button>
                <button class="btn btn-sm btn-outline-danger" type="button" :disabled="poison.isPending.value" @click="poison.mutate()">
                  POST /bus/poison
                </button>
              </div>
              <p v-if="ingest.error.value || poison.error.value" class="text-danger small mt-2 mb-0">
                {{ (ingest.error.value ?? poison.error.value)?.message }}
              </p>
            </template>

            <template v-else-if="centerId === 'alarm'">
              <div class="map-metrics mb-3">
                <InspectTerm id="last-offset" scope="alarm">offset {{ format(snap.alarm?.lastOffset) }}</InspectTerm>
                <InspectTerm id="catch-up" scope="alarm">behind log {{ format(snap.alarmCatchUp) }}</InspectTerm>
                <InspectTerm id="alarm-count" scope="alarm">alarms {{ format(snap.alarm?.alarmCount) }}</InspectTerm>
                <InspectTerm id="dlq-count" scope="alarm">dlq {{ format(snap.alarm?.dlqCount) }}</InspectTerm>
                <span>
                  <InspectTerm id="threshold" scope="alarm">threshold {{ format(snap.alarm?.threshold) }}</InspectTerm>
                  /
                  <InspectTerm id="window" scope="alarm">{{ format(snap.alarm?.windowSeconds) }}s</InspectTerm>
                </span>
              </div>
              <div v-if="shards.length" class="row g-2 mb-3">
                <div v-for="shard in shards" :key="shard.id" class="col-4">
                  <div class="small border rounded p-2" :class="{ 'stat-card-hot': shard.queued > 0 || shard.lag > 0 }">
                    <InspectTerm id="shard-lag" scope="alarm">
                      shard {{ shard.id }} · q {{ shard.queued }} · lag {{ shard.lag }}
                    </InspectTerm>
                  </div>
                </div>
              </div>
              <div class="table-responsive">
                <table class="table table-sm mb-0">
                  <thead><tr><th>Duck</th><th><InspectTerm id="alarms" scope="alarm">Rate</InspectTerm></th><th></th></tr></thead>
                  <tbody>
                    <tr v-if="!snap.alarms.length">
                      <td colspan="3" class="text-secondary">No active alarms</td>
                    </tr>
                    <tr v-for="row in snap.alarms" :key="row.eventId">
                      <td><code>{{ row.duckId }}</code></td>
                      <td>{{ row.rate.toFixed(1) }}</td>
                      <td class="text-end">
                        <button class="btn btn-sm btn-outline-warning" type="button" @click="resolveAlarm.mutate(row.duckId)">
                          Resolve
                        </button>
                      </td>
                    </tr>
                  </tbody>
                </table>
              </div>
            </template>

            <template v-else-if="centerId === 'dashboard'">
              <div class="map-metrics mb-3">
                <InspectTerm id="last-offset" scope="dashboard">offset {{ format(snap.dashboard?.lastOffset) }}</InspectTerm>
                <InspectTerm id="catch-up" scope="dashboard">behind log {{ format(snap.dashboardCatchUp) }}</InspectTerm>
                <InspectTerm id="total-squeaks" scope="dashboard">squeaks {{ format(snap.dashboard?.totalSqueaks) }}</InspectTerm>
                <InspectTerm id="dlq-count" scope="dashboard">dlq {{ format(snap.dashboard?.dlqCount) }}</InspectTerm>
                <span>{{ fileName(snap.dashboard?.database) }}</span>
              </div>
              <div v-if="shards.length" class="row g-2 mb-3">
                <div v-for="shard in shards" :key="shard.id" class="col-4">
                  <div class="small border rounded p-2" :class="{ 'stat-card-hot': shard.queued > 0 || shard.lag > 0 }">
                    <InspectTerm id="shard-lag" scope="dashboard">
                      shard {{ shard.id }} · q {{ shard.queued }} · lag {{ shard.lag }}
                    </InspectTerm>
                  </div>
                </div>
              </div>
              <button class="btn btn-sm btn-warning" type="button" :disabled="rebuild.isPending.value" @click="rebuild.mutate()">
                <InspectTerm id="rebuild" scope="dashboard">Rebuild from offset 0</InspectTerm>
              </button>
            </template>

            <template v-else-if="centerId === 'billing'">
              <div class="map-metrics mb-3">
                <InspectTerm id="last-offset" scope="billing">offset {{ format(snap.billing?.lastOffset) }}</InspectTerm>
                <InspectTerm id="catch-up" scope="billing">behind log {{ format(snap.billingCatchUp) }}</InspectTerm>
                <InspectTerm id="reserved" scope="billing">reserved {{ format(snap.billing?.reserved) }}</InspectTerm>
                <InspectTerm id="released" scope="billing">released {{ format(snap.billing?.released) }}</InspectTerm>
                <InspectTerm id="expired" scope="billing">expired {{ format(snap.billing?.expired) }}</InspectTerm>
                <InspectTerm id="saga-timeout" scope="billing">timeout {{ format(snap.billing?.sagaTimeoutSeconds) }}s</InspectTerm>
              </div>
              <div class="table-responsive">
                <table class="table table-sm mb-0">
                  <thead><tr><th>Duck</th><th>State</th><th>Expires</th></tr></thead>
                  <tbody>
                    <tr v-if="!snap.sagas.length">
                      <td colspan="3" class="text-secondary">No sagas</td>
                    </tr>
                    <tr v-for="row in snap.sagas" :key="row.alarmId">
                      <td><code>{{ row.duckId }}</code></td>
                      <td><InspectTerm id="saga-state" scope="billing">{{ row.state }}</InspectTerm></td>
                      <td class="font-monospace small">{{ row.expiresAt }}</td>
                    </tr>
                  </tbody>
                </table>
              </div>
            </template>

            <template v-else>
              <p class="text-secondary small mb-0">
                No live broker stats this pass. Aspire hosts RabbitMQ; tests use InMemoryEventBus when the connection string is unset.
              </p>
            </template>
          </div>
        </div>

        <div
          v-if="centerId === 'alarm' || centerId === 'dashboard' || centerId === 'billing'"
          class="card border-0 shadow-sm mb-3"
        >
          <div class="card-header bg-white">
            <strong><InspectTerm id="dlq" :scope="centerId">DLQ</InspectTerm></strong>
          </div>
          <div class="card-body">
            <p v-if="!dlq.length" class="text-secondary small mb-0">Empty</p>
            <ul v-else class="list-unstyled small mb-0">
              <li v-for="row in dlq" :key="row.id">
                #{{ row.id }} · {{ row.error }}
              </li>
            </ul>
          </div>
        </div>

        <div class="card border-0 shadow-sm">
          <div class="card-header bg-white"><strong>Start here in code</strong></div>
          <ul class="list-group list-group-flush">
            <li v-for="link in meta.code" :key="link.path" class="list-group-item">
              <code class="small">{{ link.path }}</code>
              <div class="text-secondary small">{{ link.why }}</div>
            </li>
          </ul>
          <div class="card-footer bg-white small text-secondary">
            As-built: {{ meta.docs.join(" · ") }}
          </div>
        </div>
      </div>
    </div>
  </div>
</template>
